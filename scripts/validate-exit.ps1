param([string]$AppPath='artifacts/exit-review/DlutTimetable.Desktop.dll')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$app=(Resolve-Path -LiteralPath $AppPath).Path
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type ([regex]::Match((Get-Content (Join-Path $PSScriptRoot 'smoke-desktop.ps1') -Raw),"(?s)Add-Type @'\r?\n(.*?)\r?\n'@").Groups[1].Value)
$cursor=[DesktopAudit]::Cursor()
$runKey='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$startupBefore=(Get-ItemProperty -LiteralPath $runKey -Name DlutDesktopTimetable -ErrorAction SilentlyContinue).DlutDesktopTimetable
$personal=Get-Content (Join-Path $root 'docs/validation/current-g1-session.json') -Raw|ConvertFrom-Json
$unrelated=Get-Process -Id $personal.parentPid
$unrelatedStarted=$unrelated.StartTime
$samples=[Collections.Generic.List[object]]::new()
function Window([string]$role){[DesktopAudit]::Windows($process.Id,$true)|Where-Object {$_.Visible -and $_.Title -eq ('大工课表 · '+$role)}|Select-Object -First 1}
function Wait-For([scriptblock]$condition){
 $until=[DateTime]::UtcNow.AddSeconds(15)
 while([DateTime]::UtcNow -lt $until){$value=&$condition;if($value){return $value};if($process.HasExited){throw "Process exited before $mode ready"};Start-Sleep -Milliseconds 25}
 throw "Exit validation timed out: $mode"
}
function Named($window,[string]$name){
 if(!$window){return $null}
 $ui=[System.Windows.Automation.AutomationElement]::FromHandle([intptr]$window.Hwnd)
 $ui.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name))
}
function Invoke-Owned([string]$role,[string]$name){
 (Wait-For {Named (Window $role) $name}).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Settings {
 Invoke-Owned '按钮0' '设置'
 $null=Wait-For {Window '设置'}
 (Wait-For {Named (Window '设置') '通用'}).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
}
try {
 foreach($mode in @('Settings','Movable','EdgeTopmost','Ball','BallMotion')){
  $data=Join-Path $root ('artifacts/exit-'+$mode+'-'+[guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Path $data|Out-Null
  $retry=[DateTimeOffset]::UtcNow.AddHours(1).ToString('O')
  @{schemaVersion=1;startupInitialized=$true;startupDesired=$false;state=0;edgeLevel=1;schoolRetryAfterUtc=$retry}|ConvertTo-Json|Set-Content (Join-Path $data 'settings.json') -Encoding utf8NoBOM
  $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
  $identity=$sid+[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($data)).ToUpperInvariant()
  $pipeName='DlutTimetable-'+[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($identity))).Substring(0,24)
  $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $root '.tools/dotnet/dotnet.exe'))
  $start.UseShellExecute=$false;$start.CreateNoWindow=$true
  foreach($arg in @($app,'--desktop-demo','--data-directory',$data)){$start.ArgumentList.Add($arg)}
  $process=[Diagnostics.Process]::Start($start)
  try {
   $null=Wait-For {(Window '按钮3') -and (Window '周课表')}
   $null=Wait-For {Test-Path (Join-Path $data 'desktop-own-frame.png')}
   $database=Join-Path $data 'timetable.db'
   $cacheBefore=(Get-FileHash -LiteralPath $database).Hash
   $closeXPreserved=$null;$motionObserved=$false;$topmostObserved=$false
   if($mode -eq 'Movable'){
    $pin=Wait-For {Named (Window '按钮3') '固定课表 / 允许拖动'}
    $pin.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    $null=Wait-For {$w=Window '周课表';$w -and ($w.ExStyle -band 0x20)-eq 0}
   }
   if($mode -eq 'EdgeTopmost'){
    Invoke-Owned '按钮1' '侧边隐藏'
    $strip=Wait-For {
     $w=Window '侧边条'
     if($w -and !(Window '周课表') -and ($w.ExStyle -band 8)-ne 0 -and [DesktopAudit]::Hit($w.X+[int]($w.Width/2),$w.Y+[int]($w.Height/2))-eq $w.Hwnd){$w}
    }
    [DesktopAudit]::Move($strip.X+[int]($strip.Width/2),$strip.Y+[int]($strip.Height/2))
    $panel=Wait-For {(Window '周课表') -and (Window '按钮0')}
    $topmostObserved=((Window '周课表').ExStyle -band 8)-ne 0
    if(!$topmostObserved){throw 'Edge panel was not topmost before exit'}
   }
   if($mode -eq 'Ball'){
    Invoke-Owned '按钮2' '收成悬浮球'
    $null=Wait-For {(Window '悬浮球') -and !(Window '收展过渡') -and !(Window '按钮2')}
   }
   if($mode -eq 'BallMotion'){
    Invoke-Owned '按钮2' '收成悬浮球'
    $null=Wait-For {(Window '收展过渡') -and ((Get-Content (Join-Path $data 'desktop-validation.log') -ErrorAction SilentlyContinue)|Where-Object {$_ -match 'BALL_BEGIN'})}
    $motionObserved=[bool](Window '收展过渡')
   }
   if($mode -notin @('Ball','BallMotion')){
    Settings
    if($mode -eq 'Settings'){
     $ui=[System.Windows.Automation.AutomationElement]::FromHandle([intptr](Window '设置').Hwnd)
     $ui.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
     $null=Wait-For {!(Window '设置')}
     $closeXPreserved=!$process.HasExited -and [bool](Window '周课表')
     Settings
    }
   }
   $watch=[Diagnostics.Stopwatch]::StartNew()
   if($mode -in @('Ball','BallMotion')){
    $pipe=[IO.Pipes.NamedPipeClientStream]::new('.', $pipeName,[IO.Pipes.PipeDirection]::Out)
    try{$pipe.Connect(1000);$pipe.WriteByte(2);$pipe.Flush()}finally{$pipe.Dispose()}
   }else{Invoke-Owned '设置' '退出程序'}
   if(!$process.WaitForExit(10000)){throw 'Exit deadline exceeded'}
   $elapsed=$watch.Elapsed.TotalMilliseconds
   $saved=Get-Content (Join-Path $data 'settings.json') -Raw|ConvertFrom-Json
   $passed=$process.ExitCode -eq 0 -and $elapsed -lt 10000 -and (Get-FileHash -LiteralPath $database).Hash -eq $cacheBefore -and !$saved.startupDesired -and $saved.schoolRetryAfterUtc -eq $retry
   if($mode -eq 'Settings'){$passed=$passed -and $closeXPreserved}
   if($mode -eq 'BallMotion'){$passed=$passed -and $motionObserved}
   $samples.Add(@{mode=$mode;passed=$passed;exitCode=$process.ExitCode;elapsedMs=$elapsed;settingsXPreservesSession=$closeXPreserved;motionObserved=$motionObserved;topmostObserved=$topmostObserved;cacheUnchanged=((Get-FileHash -LiteralPath $database).Hash -eq $cacheBefore);disabledStartupPreserved=(!$saved.startupDesired);retryDeadlinePreserved=($saved.schoolRetryAfterUtc -eq $retry);remainingWindows=@([DesktopAudit]::Windows($process.Id,$true)).Count})
  }catch{
   @{mode=$mode;error=$_.Exception.Message;completedSamples=$samples;windows=@([DesktopAudit]::Windows($process.Id,$true));log=@(Get-Content (Join-Path $data 'desktop-validation.log') -ErrorAction SilentlyContinue)}|ConvertTo-Json -Depth 7|Set-Content (Join-Path $root 'docs/validation/exit-desktop-failure.json') -Encoding utf8
   throw
  }finally{if(!$process.HasExited){$process.Kill($false);$process.WaitForExit()};$process.Dispose()}
 }
 $startupAfter=(Get-ItemProperty -LiteralPath $runKey -Name DlutDesktopTimetable -ErrorAction SilentlyContinue).DlutDesktopTimetable
 $stillUnrelated=Get-Process -Id $personal.parentPid
 $report=@{capturedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');samples=$samples;realStartupUnchanged=($startupBefore -ceq $startupAfter);personalSessionUntouched=($stillUnrelated.StartTime -eq $unrelatedStarted);scope='Isolated synthetic desktop; settings exit and same owned-session IPC exit. Actual system tray overflow interaction and in-flight school payload commit are not covered.'}
 $report|ConvertTo-Json -Depth 6|Set-Content (Join-Path $root 'docs/validation/exit-desktop-matrix.json') -Encoding utf8
 if(!$report.realStartupUnchanged -or !$report.personalSessionUntouched -or @($samples|Where-Object {!$_.passed}).Count){throw 'Exit matrix failed; see report'}
 $report|ConvertTo-Json -Depth 6
}finally{[DesktopAudit]::Move($cursor[0],$cursor[1])}
