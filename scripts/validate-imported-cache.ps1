param([string]$SessionFile='docs/validation/current-g1-session.json')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$session=Get-Content -LiteralPath $SessionFile -Raw|ConvertFrom-Json
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type ([regex]::Match((Get-Content (Join-Path $PSScriptRoot 'smoke-desktop.ps1') -Raw),"(?s)Add-Type @'\r?\n(.*?)\r?\n'@").Groups[1].Value)
function Named($element,[string]$name){$element.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name))}
function ById($element,[string]$id){$element.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id))}
function Wait-For([scriptblock]$condition){
 $until=[DateTime]::UtcNow.AddSeconds(15)
 while([DateTime]::UtcNow -lt $until){$value=&$condition;if($value){return $value};Start-Sleep -Milliseconds 40}
 throw 'Imported cache validation timed out'
}
function OwnedWindow([int]$owner,[string]$title){[DesktopAudit]::Windows($owner)|Where-Object {$_.Title -eq ('大工课表 · '+$title) -and $_.Visible}|Select-Object -First 1}
function Element($window){[System.Windows.Automation.AutomationElement]::FromHandle([intptr]$window.Hwnd)}
$owned=Get-CimInstance Win32_Process -Filter "ProcessId=$($session.parentPid)"
if(!$owned -or !$owned.CommandLine.Contains('DlutTimetable.Desktop.dll') -or !$owned.CommandLine.Contains($session.dataDirectory)){throw 'Unexpected desktop process; restart aborted'}
if(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($session.parentPid) AND Name='DlutTimetable.AuthWorker.exe'"){throw 'School window active; validation aborted'}
$database=Join-Path $session.dataDirectory 'timetable.db'
$before=(Get-FileHash -LiteralPath $database).Hash
$process=Get-Process -Id $session.parentPid
$settings=OwnedWindow $process.Id '设置'
if(!$settings){$button=Element (OwnedWindow $process.Id '按钮0');(Named $button '设置').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke();$settings=Wait-For {OwnedWindow $process.Id '设置'}}
$ui=Element $settings
(Named $ui '通用').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
(Named $ui '退出程序').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
if(!$process.WaitForExit(10000)){throw 'Owned desktop did not exit gracefully'}
$app=Join-Path $root 'src/DlutTimetable.Desktop/bin/Release/net10.0-windows10.0.19041.0/DlutTimetable.Desktop.dll'
$process=Start-Process -FilePath (Join-Path $root '.tools/dotnet/dotnet.exe') -ArgumentList @(('"'+$app+'"'),'--data-directory',('"'+$session.dataDirectory+'"')) -WindowStyle Hidden -PassThru
$session.parentPid=$process.Id
$session.status='RealCoursesImported'
$session|ConvertTo-Json -Depth 10|Set-Content -LiteralPath $SessionFile -Encoding utf8
$panel=Wait-For {OwnedWindow $process.Id '周课表'}
$view=(Element $panel).FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'第 8 周课表'))
if(!$view -or $view.Current.ItemStatus -notmatch '本周有课'){throw 'Cached courses not restored to the current week view'}
Start-Sleep -Milliseconds 500
if(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($process.Id) AND Name='DlutTimetable.AuthWorker.exe'"){throw 'Startup unexpectedly launched school browser'}
$button=Element (OwnedWindow $process.Id '按钮0')
(Named $button '设置').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
$settings=Wait-For {OwnedWindow $process.Id '设置'};$ui=Element $settings
(Named $ui '数据与账号').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
$summary=ById $ui 'SchoolDataSummary'
if(!$summary -or $summary.Current.Name -notmatch '已保存课表' -or $summary.Current.Name -match '未连接学校账号'){throw 'Cached timetable summary is inaccurate'}
$refresh=ById $ui 'SchoolManualRefresh'
$refresh.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
$worker=Wait-For {Get-CimInstance Win32_Process -Filter "ParentProcessId=$($process.Id) AND Name='DlutTimetable.AuthWorker.exe'"|Select-Object -First 1}
$login=Wait-For {OwnedWindow $worker.ProcessId '学校登录'}
$cancel=Wait-For {ById (Element $login) 'AuthCancel'}
$cancel.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Wait-For {! (Get-CimInstance Win32_Process -Filter "ProcessId=$($worker.ProcessId)")} | Out-Null
Wait-For {(ById (Element $settings) 'SchoolManualRefresh').Current.IsEnabled} | Out-Null
$after=(Get-FileHash -LiteralPath $database).Hash
if($before -ne $after){throw 'Cancel or restart unexpectedly changed the saved timetable'}
(Element $settings).GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
[ordered]@{restartRestoredRealCourseView=$true;cachedDataSummaryAccurate=$true;startupOpenedNoSchoolBrowser=$true;manualRefreshOpenedOwnedWindow=$true;cancelPreservedDatabaseBytes=$true;cancelClosedWorker=$true;refreshReenabled=$true;checkedAt=[DateTimeOffset]::Now.ToString('O')}|ConvertTo-Json|Set-Content (Join-Path $root 'docs/validation/imported-cache-ui.json') -Encoding utf8
Get-Content (Join-Path $root 'docs/validation/imported-cache-ui.json')
