param([string]$SessionFile='docs/validation/current-g1-session.json',[string]$ValidationReport='docs/validation/convergence-stage.md')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$session=Get-Content -LiteralPath $SessionFile -Raw|ConvertFrom-Json
$release=Get-Content (Join-Path $root 'docs/validation/current-release.json') -Raw|ConvertFrom-Json
& (Join-Path $PSScriptRoot 'verify-release.ps1') -Directory $release.directory|Out-Null
$executable=Join-Path $release.directory 'DlutTimetable.Desktop.exe'
$owned=Get-CimInstance Win32_Process -Filter "ProcessId=$($session.parentPid)"
if(!$owned -or !$owned.CommandLine.Contains('DlutTimetable.Desktop') -or !$owned.CommandLine.Contains($session.dataDirectory)){
 throw 'Current session identity does not match; no process was stopped'
}
if(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($session.parentPid)"|Where-Object {$_.CommandLine -match 'DlutTimetable.AuthWorker'}){
 throw 'Finish the school operation before updating the desktop'
}
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type ([regex]::Match((Get-Content (Join-Path $PSScriptRoot 'smoke-desktop.ps1') -Raw),"(?s)Add-Type @'\r?\n(.*?)\r?\n'@").Groups[1].Value)
function Window([int]$owner,[string]$role){[DesktopAudit]::Windows($owner,$true)|Where-Object {$_.Visible -and $_.Title -eq ('大工课表 · '+$role)}|Select-Object -First 1}
function Named($window,[string]$name){
 if(!$window){return $null}
 $ui=[System.Windows.Automation.AutomationElement]::FromHandle([intptr]$window.Hwnd)
 $ui.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name))
}
function Wait-For([scriptblock]$condition){
 $deadline=[DateTime]::UtcNow.AddSeconds(20)
 while([DateTime]::UtcNow -lt $deadline){$value=&$condition;if($value){return $value};Start-Sleep -Milliseconds 50}
 throw 'Desktop session update timed out'
}
$database=Join-Path $session.dataDirectory 'timetable.db'
$before=(Get-FileHash -LiteralPath $database).Hash
$runKey='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runBefore=(Get-ItemProperty -LiteralPath $runKey -Name DlutDesktopTimetable -ErrorAction SilentlyContinue).DlutDesktopTimetable
$oldProcess=Get-Process -Id $session.parentPid
# Open the wait handle before exit: Get-Process may otherwise lose ExitCode
# when the process disappears before a handle has been retained.
$null=$oldProcess.Handle
if($session.parentStartedUtc -and $oldProcess.StartTime.ToUniversalTime().Ticks -ne ([datetimeoffset]$session.parentStartedUtc).UtcTicks){throw 'Current process creation time does not match'}
$settings=Window $oldProcess.Id '设置'
if(!$settings){
 $gear=Named (Window $oldProcess.Id '按钮0') '设置'
 if(!$gear){throw 'Open the current desktop settings before updating'}
 $gear.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
 $settings=Wait-For {Window $oldProcess.Id '设置'}
}
(Wait-For {Named $settings '通用'}).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
(Wait-For {Named $settings '退出程序'}).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
if(!$oldProcess.WaitForExit(10000)){throw 'Current desktop did not exit normally; replacement not launched'}
$start=[Diagnostics.ProcessStartInfo]::new($executable)
$start.UseShellExecute=$false
$start.ArgumentList.Add('--data-directory');$start.ArgumentList.Add($session.dataDirectory)
$process=[Diagnostics.Process]::Start($start)
# Record the new owner immediately so a later observation failure never leaves a stale PID.
$session.parentPid=$process.Id
$session|Add-Member parentStartedUtc $process.StartTime.ToUniversalTime().ToString('O') -Force
$session|Add-Member applicationPath $executable -Force
$session|Add-Member applicationVersion $release.version -Force
$session|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $SessionFile -Encoding utf8
$panel=Wait-For {if($process.HasExited){throw 'Replacement desktop exited'};Window $process.Id '周课表'}
$view=Wait-For {
 $ui=[System.Windows.Automation.AutomationElement]::FromHandle([intptr]$panel.Hwnd)
 # Read the public schedule summary; do not collect individual course text.
 $all=$ui.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
 foreach($item in $all){if($item.Current.Name -match '^第 \d+ 周课表$' -and $item.Current.ItemStatus -match '^本周有课'){return $item}}
}
$after=(Get-FileHash -LiteralPath $database).Hash
$runAfter=(Get-ItemProperty -LiteralPath $runKey -Name DlutDesktopTimetable -ErrorAction SilentlyContinue).DlutDesktopTimetable
$workerStarted=[bool](Get-CimInstance Win32_Process -Filter "ParentProcessId=$($process.Id)"|Where-Object {$_.CommandLine -match 'DlutTimetable.AuthWorker'})
$report=[ordered]@{capturedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');version=$release.version;pid=$process.Id;previousExitRequestedViaSettings=$true;previousExitCode=$oldProcess.ExitCode;normalPreviousExit=($oldProcess.ExitCode -eq 0);cachedCoursesVisible=[bool]$view;databaseUnchanged=($before -eq $after);startupRegistrationUnchanged=($runBefore -ceq $runAfter);schoolWorkerStarted=$workerStarted}
$report|ConvertTo-Json|Set-Content (Join-Path $root 'docs/validation/current-session-update.json') -Encoding utf8
if(!$report.cachedCoursesVisible -or !$report.databaseUnchanged -or !$report.startupRegistrationUnchanged -or $workerStarted){throw 'Session update validation failed; see report'}
$session.status='CachedCoursesRunning'
$session.latestValidation=$ValidationReport
$session|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $SessionFile -Encoding utf8
$report|ConvertTo-Json
