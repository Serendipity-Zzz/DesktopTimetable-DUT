param([string]$ReleaseDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if(!$ReleaseDirectory){$ReleaseDirectory=(Get-Content (Join-Path $root 'docs/validation/current-release.json') -Raw|ConvertFrom-Json).directory}
& (Join-Path $PSScriptRoot 'verify-release.ps1') -Directory $ReleaseDirectory|Out-Null
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type ([regex]::Match((Get-Content (Join-Path $PSScriptRoot 'smoke-desktop.ps1') -Raw),"(?s)Add-Type @'\r?\n(.*?)\r?\n'@").Groups[1].Value)
Add-Type ([regex]::Match((Get-Content (Join-Path $PSScriptRoot 'validate-data-management.ps1') -Raw),"(?s)Add-Type @'\r?\n(.*?)\r?\n'@").Groups[1].Value)
$fixture=Join-Path $root ('artifacts/uninstall-ui-'+[Guid]::NewGuid().ToString('N'));$source=Join-Path $fixture 'source';New-Item -ItemType Directory -Path $source -Force|Out-Null
$owned=[Collections.Generic.List[Diagnostics.Process]]::new();$checks=[Collections.Generic.List[object]]::new()
$runKey='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run';$startupBefore=(Get-ItemProperty -LiteralPath $runKey -Name DlutDesktopTimetable -ErrorAction SilentlyContinue).DlutDesktopTimetable
function Named($e,[string]$name){$e.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name))}
function Id($e,[string]$name){$e.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$name))}
function Own([int]$owner,[string]$title){[DesktopAudit]::Windows($owner,$true)|Where-Object {$_.Title -eq $title -and $_.Visible}|Select-Object -First 1}
function Ui($w){[System.Windows.Automation.AutomationElement]::FromHandle([intptr]$w.Hwnd)}
function Wait-For([scriptblock]$condition){$until=[DateTime]::UtcNow.AddSeconds(40);while([DateTime]::UtcNow -lt $until){$v=&$condition;if($v){return $v};Start-Sleep -Milliseconds 60};throw ('Uninstall UI timed out: '+$script:stage)}
function Invoke-Id($w,[string]$id){(Id (Ui $w) $id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()}
function Start-Owned([string]$exe,[string[]]$arguments){$info=[Diagnostics.ProcessStartInfo]::new($exe);$info.UseShellExecute=$false;$info.WorkingDirectory=$fixture;foreach($a in $arguments){$info.ArgumentList.Add($a)};$p=[Diagnostics.Process]::Start($info);$owned.Add($p);return $p}
function Settings($p){$w=Own $p.Id '大工课表 · 设置';if(!$w){$gear=Wait-For {Own $p.Id '大工课表 · 按钮0'};(Named (Ui $gear) '设置').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke();$w=Wait-For {Own $p.Id '大工课表 · 设置'}};return $w}
function Exit-Owned($p){
 if($p.HasExited){return}
 $command=(Get-CimInstance Win32_Process -Filter "ProcessId=$($p.Id)").CommandLine
 if(!$command -or !$command.Contains($fixture)){throw 'Refusing to close an unverified process'}
 $window=Own $p.Id '大工课表 · 安装与更新';if(!$window){$window=Own $p.Id '大工课表 · 卸载'}
 if($window){(Ui $window).GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()}
 else{$s=Ui (Settings $p);(Named $s '通用').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select();(Named $s '退出程序').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()}
 if(!$p.WaitForExit(10000)){throw 'Owned fixture did not exit normally'}
}
try{
 $stage='prepare fictional source';$p=Start-Owned (Join-Path $ReleaseDirectory 'DlutTimetable.Desktop.exe') @('--data-directory',$source)
 $s=Wait-For {Own $p.Id '大工课表 · 设置'};$ui=Ui $s;(Named $ui '数据与账号').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 (Named $ui '导入 JSON').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke();$dialog=Wait-For {Own $p.Id '导入个人课表'}
 if(![OwnedDialogCancel]::SetFileName([intptr]$dialog.Hwnd,$p.Id,(Join-Path $root 'fixtures/dlut/synthetic-term.json')) -or ![OwnedDialogCancel]::Save([intptr]$dialog.Hwnd,$p.Id)){throw 'Cannot import fictional source'}
 Wait-For {(Id (Ui $s) 'OpenCalendarSettings').Current.IsEnabled}|Out-Null;Exit-Owned $p
 $sourceHash=(Get-FileHash -LiteralPath (Join-Path $source 'timetable.db')).Hash
 foreach($retain in @($true,$false)){
  $mode=if($retain){'keep'}else{'remove'};$application=Join-Path $fixture ($mode+'/安装 目录');$data=Join-Path $fixture ($mode+'/个人 数据')
  $stage=$mode+' install';$p=Start-Owned (Join-Path $ReleaseDirectory 'DlutTimetable.Desktop.exe') @('--install','--install-directory',$application,'--data-directory',$data,'--migrate-from',$source)
  $installer=Wait-For {Own $p.Id '大工课表 · 安装与更新'};Wait-For {(Id (Ui $installer) 'InstallUpdate').Current.IsEnabled}|Out-Null;Invoke-Id $installer 'InstallUpdate'
  Wait-For {$message=(Id (Ui $installer) 'InstallerStatus').Current.Name;if($message.StartsWith('未完成：')){throw $message};(Id (Ui $installer) 'InstallLaunch').Current.IsEnabled}|Out-Null
  $state=Get-Content (Join-Path $application 'install-state.json') -Raw|ConvertFrom-Json;$exe=Join-Path $application ($state.currentVersion+'/DlutTimetable.Desktop.exe')
  Invoke-Id $installer 'InstallLaunch'
  $info=Wait-For {Get-CimInstance Win32_Process -Filter "Name='DlutTimetable.Desktop.exe'"|Where-Object {$_.ExecutablePath -eq $exe -and $_.CommandLine.Contains($data)}|Select-Object -First 1}
  $main=Get-Process -Id $info.ProcessId;$owned.Add($main);Wait-For {Own $main.Id '大工课表 · 周课表'}|Out-Null
  $databaseHash=(Get-FileHash -LiteralPath (Join-Path $data 'timetable.db')).Hash
  $worker=$null;$browserIds=@()
  if($retain){
   $stage='open owned school worker before uninstall';$settings=Settings $main;$ui=Ui $settings;(Named $ui '数据与账号').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select();Invoke-Id $settings 'SchoolManualRefresh'
   $worker=Wait-For {Get-CimInstance Win32_Process -Filter "ParentProcessId=$($main.Id) AND Name='DlutTimetable.AuthWorker.exe'"|Select-Object -First 1}
   $login=Wait-For {Own $worker.ProcessId '大工课表 · 学校登录'};Wait-For {$state=Id (Ui $login) 'AuthBrowserState';$state -and $state.Current.HelpText -eq 'browser-ready'}|Out-Null
   $browsers=@(Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'");$parents=@([int]$worker.ProcessId)
   do{$next=@($browsers|Where-Object {$_.ParentProcessId -in $parents -and $_.ProcessId -notin $browserIds}|ForEach-Object {[int]$_.ProcessId});$browserIds+=$next;$parents=$next}while($next.Count)
   $extra=Join-Path ([IO.Path]::GetDirectoryName($exe)) 'user-extra.txt';Set-Content -LiteralPath $extra -Value 'fictional extra file, preserve me' -Encoding utf8NoBOM
  }
  $stage=$mode+' uninstall';$uninstaller=Start-Owned $exe @('--uninstall','--install-directory',$application,'--data-directory',$data,'--uninstall-quiet')
  $window=Wait-For {Own $uninstaller.Id '大工课表 · 卸载'};Wait-For {(Id (Ui $window) 'UninstallConfirm').Current.IsEnabled}|Out-Null
  $toggle=(Id (Ui $window) 'UninstallKeepData').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
  if($toggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On){throw 'Uninstall must default to keeping data'}
  if(!$retain){$toggle.Toggle()}
  Invoke-Id $window 'UninstallConfirm';Wait-For {$uninstaller.HasExited}|Out-Null
  if(!$main.WaitForExit(10000)){throw 'Uninstall did not finish the exact running main session'}
  $stage=$mode+' helper result'
  $resultFile=Wait-For {
   foreach($dir in Get-ChildItem -LiteralPath ([IO.Path]::GetTempPath()) -Directory -Filter 'DlutUninstall-*'|Sort-Object LastWriteTime -Descending){
    $planPath=Join-Path $dir.FullName 'plan.json';$result=Join-Path $dir.FullName 'result.json'
    if((Test-Path -LiteralPath $planPath) -and (Test-Path -LiteralPath $result)){$plan=Get-Content -LiteralPath $planPath -Raw|ConvertFrom-Json;if($plan.installDirectory -eq $application){return $result}}
   }
  }
  $result=Get-Content -LiteralPath $resultFile -Raw|ConvertFrom-Json;if(!$result.passed){throw $result.message}
  $helperPlan=Join-Path ([IO.Path]::GetDirectoryName($resultFile)) 'plan.json'
  Wait-For {!(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'"|Where-Object {$_.CommandLine -and $_.CommandLine.Contains($helperPlan)})}|Out-Null
  if($worker -and (Get-CimInstance Win32_Process -Filter "ProcessId=$($worker.ProcessId)")){throw 'Owned school worker survived uninstall'}
  foreach($browserId in $browserIds){if(Get-CimInstance Win32_Process -Filter "ProcessId=$browserId"){throw 'Owned browser descendant survived uninstall'}}
  if($retain){if((Get-FileHash -LiteralPath (Join-Path $data 'timetable.db')).Hash -ne $databaseHash -or !(Test-Path -LiteralPath $extra)){throw 'Keep-data mode changed cache or extra file'}}
  else{if((Test-Path -LiteralPath (Join-Path $data 'timetable.db')) -or (Test-Path -LiteralPath (Join-Path $data 'settings.json')) -or (Test-Path -LiteralPath $application)){throw 'Remove-data mode left selected data or known program directory'}}
  if((Get-FileHash -LiteralPath (Join-Path $source 'timetable.db')).Hash -ne $sourceHash){throw 'Uninstall modified migration source'}
  $checks.Add([ordered]@{mode=$mode;passed=$true;dataChoiceApplied=$true;runningMainExited=$true;workerWasRunning=($null -ne $worker);ownedWorkerAndBrowserExited=$(if($worker){$true}else{$null});observedBrowserDescendants=$browserIds.Count;cleanupHelperExited=$true;originalSourceUnchanged=$true;extraFilePreserved=$(if($retain){$true}else{$null})})
 }
}finally{
 foreach($p in $owned){Exit-Owned $p}
 $after=(Get-ItemProperty -LiteralPath $runKey -Name DlutDesktopTimetable -ErrorAction SilentlyContinue).DlutDesktopTimetable
 if($after -ne $startupBefore){throw 'Isolated uninstall validation changed real startup registration'}
}
[ordered]@{passed=$true;scope='Real self-contained EXE; fictional data; isolated install/data; actual Windows PowerShell 5.1 cleanup';releaseManifestSha256=(Get-FileHash -LiteralPath (Join-Path $ReleaseDirectory 'release-manifest.json')).Hash;checks=$checks.ToArray();realStartupUnchanged=$true;checkedAt=[DateTimeOffset]::Now.ToString('O')}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $root 'docs/validation/uninstallation-ui.json') -Encoding utf8NoBOM
Get-Content (Join-Path $root 'docs/validation/uninstallation-ui.json')
