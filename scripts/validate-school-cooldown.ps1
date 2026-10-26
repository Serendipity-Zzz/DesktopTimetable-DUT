$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type ([regex]::Match((Get-Content (Join-Path $PSScriptRoot 'smoke-desktop.ps1') -Raw),"(?s)Add-Type @'\r?\n(.*?)\r?\n'@").Groups[1].Value)
function Named($element,[string]$name){$element.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name))}
function ById($element,[string]$id){$element.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id))}
function Wait-For([scriptblock]$condition){
 $until=[DateTime]::UtcNow.AddSeconds(30)
 while([DateTime]::UtcNow -lt $until){$value=&$condition;if($value){return $value};Start-Sleep -Milliseconds 60}
 throw 'School cooldown UI condition timed out'
}
function Settings([int]$owner){$window=[DesktopAudit]::Windows($owner)|Where-Object {$_.Title -eq '大工课表 · 设置' -and $_.Visible}|Select-Object -First 1;if($window){[System.Windows.Automation.AutomationElement]::FromHandle([intptr]$window.Hwnd)}}
function Exit-Owned($process){
 if($process.HasExited){return}
 $command=(Get-CimInstance Win32_Process -Filter "ProcessId=$($process.Id)").CommandLine
 if(!$command -or !$command.Contains($data) -or !$command.Contains('DlutTimetable.Desktop.dll')){throw 'Unexpected process; exit aborted'}
 $settings=Settings $process.Id
 (Named $settings '通用').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 (Named $settings '退出程序').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
 if(!$process.WaitForExit(10000)){throw 'Cooldown fixture desktop did not exit'}
}
$data=Join-Path $root ('artifacts/school-cooldown-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $data|Out-Null
$deadline=[DateTimeOffset]::UtcNow.AddSeconds(20)
@{schemaVersion=1;state=3;ballX=30;ballY=30;schoolRetryAfterUtc=$deadline.ToString('O')}|ConvertTo-Json|Set-Content (Join-Path $data 'settings.json') -Encoding utf8NoBOM
$app=Join-Path $root 'src/DlutTimetable.Desktop/bin/Release/net10.0-windows10.0.19041.0/DlutTimetable.Desktop.dll'
$dotnet=Join-Path $root '.tools/dotnet/dotnet.exe'
$process=$null
try {
 for($iteration=0;$iteration -lt 2;$iteration++){
  $process=Start-Process -FilePath $dotnet -ArgumentList @(('"'+$app+'"'),'--data-directory',('"'+$data+'"')) -WindowStyle Hidden -PassThru
  $settings=Wait-For {Settings $process.Id}
  (Named $settings '数据与账号').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  $refresh=ById $settings 'SchoolManualRefresh';$summary=ById $settings 'SchoolDataSummary'
  if($refresh.Current.IsEnabled -or $summary.Current.Name -notmatch '限制刷新'){throw 'Saved cooldown was ignored'}
  if(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($process.Id) AND Name='DlutTimetable.AuthWorker.exe'"){throw 'Cooldown started a school browser'}
  if($iteration -eq 0){
   # Exercise an ordinary settings write; it must preserve the future deadline.
   (Named $settings '通用').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
   (Named $settings '减少动画（≤120 ms）').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
   Exit-Owned $process
   $saved=Get-Content (Join-Path $data 'settings.json') -Raw
   $rawDeadline=[regex]::Match($saved,'"schoolRetryAfterUtc"\s*:\s*"([^"]+)"').Groups[1].Value
   if(!$rawDeadline -or [DateTimeOffset]::Parse($rawDeadline) -ne $deadline){throw 'Settings write lost the retry deadline'}
  }
 }
 Wait-For {(ById (Settings $process.Id) 'SchoolManualRefresh').Current.IsEnabled}|Out-Null
 if([DateTimeOffset]::UtcNow -lt $deadline){throw 'Refresh was enabled before server deadline'}
 if((ById (Settings $process.Id) 'SchoolDataSummary').Current.Name -match '限制刷新'){throw 'Expired cooldown hint remained'}
 if(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($process.Id) AND Name='DlutTimetable.AuthWorker.exe'"){throw 'Cooldown expiry automatically opened school browser'}
 [ordered]@{passed=$true;scope='Isolated settings with a fictional server deadline; no school request or personal cache';blockedUntilDeadline=$true;survivedSettingsWriteAndRestart=$true;reenabledOnExpiry=$true;expiryDidNotFetch=$true;checkedAt=[DateTimeOffset]::Now.ToString('O')}|ConvertTo-Json|Set-Content (Join-Path $root 'docs/validation/school-cooldown-ui.json') -Encoding utf8
 Get-Content (Join-Path $root 'docs/validation/school-cooldown-ui.json')
} finally {if($process -and !$process.HasExited){Exit-Owned $process}}
