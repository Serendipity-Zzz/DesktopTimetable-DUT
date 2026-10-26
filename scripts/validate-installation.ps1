param([string]$ReleaseDirectory,[switch]$RepairStage)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if(!$ReleaseDirectory){$ReleaseDirectory=(Get-Content (Join-Path $root 'docs/validation/current-release.json') -Raw|ConvertFrom-Json).directory}
& (Join-Path $PSScriptRoot 'verify-release.ps1') -Directory $ReleaseDirectory|Out-Null
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type ([regex]::Match((Get-Content (Join-Path $PSScriptRoot 'smoke-desktop.ps1') -Raw),"(?s)Add-Type @'\r?\n(.*?)\r?\n'@").Groups[1].Value)
Add-Type ([regex]::Match((Get-Content (Join-Path $PSScriptRoot 'validate-data-management.ps1') -Raw),"(?s)Add-Type @'\r?\n(.*?)\r?\n'@").Groups[1].Value)
$fixture=Join-Path $root ('artifacts/install-ui-'+[Guid]::NewGuid().ToString('N'))
$source=Join-Path $fixture 'source';$data=Join-Path $fixture '个人 数据';$application=Join-Path $fixture '安装 目录'
New-Item -ItemType Directory -Path $fixture,$source -Force|Out-Null
$runKey='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$startupBefore=(Get-ItemProperty -LiteralPath $runKey -Name DlutDesktopTimetable -ErrorAction SilentlyContinue).DlutDesktopTimetable
$owned=[Collections.Generic.List[Diagnostics.Process]]::new()
function Named($e,[string]$name){$e.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name))}
function Id($e,[string]$name){$e.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$name))}
function Own([int]$owner,[string]$title){[DesktopAudit]::Windows($owner,$true)|Where-Object {$_.Title -eq $title -and $_.Visible}|Select-Object -First 1}
function Ui($window){[System.Windows.Automation.AutomationElement]::FromHandle([intptr]$window.Hwnd)}
function Invoke-Id($window,[string]$id){(Id (Ui $window) $id).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()}
function Wait-For([scriptblock]$condition){$until=[DateTime]::UtcNow.AddSeconds(30);while([DateTime]::UtcNow -lt $until){$v=&$condition;if($v){return $v};Start-Sleep -Milliseconds 60};throw ('Installation UI timed out at '+$script:stage)}
function Select-Tab($window,[string]$name){$tab=Wait-For {Named (Ui $window) $name};$tab.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()}
function Start-Owned([string]$exe,[string[]]$arguments){$info=[Diagnostics.ProcessStartInfo]::new($exe);$info.UseShellExecute=$false;$info.WorkingDirectory=$fixture;foreach($a in $arguments){$info.ArgumentList.Add($a)};$p=[Diagnostics.Process]::Start($info);$owned.Add($p);return $p}
function Settings([Diagnostics.Process]$p){
 $w=Own $p.Id '大工课表 · 设置'
 if(!$w){$gear=Wait-For {Own $p.Id '大工课表 · 按钮0'};(Named (Ui $gear) '设置').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke();$w=Wait-For {Own $p.Id '大工课表 · 设置'}}
 return $w
}
function Exit-Owned([Diagnostics.Process]$p){
 if($p.HasExited){return}
 $command=(Get-CimInstance Win32_Process -Filter "ProcessId=$($p.Id)").CommandLine
 if(!$command -or !$command.Contains($fixture)){throw 'Unexpected process command; refusing to close'}
 $installer=Own $p.Id '大工课表 · 安装与更新'
 if(!$installer){$installer=Own $p.Id '大工课表 · 卸载'}
 if($installer){(Ui $installer).GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()}
 else{$settings=Settings $p;Select-Tab $settings '通用';(Named (Ui $settings) '退出程序').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()}
 if(!$p.WaitForExit(10000)){throw 'Owned installation fixture did not exit normally'}
}
function Installer([string]$package,[bool]$migrate){
 $arguments=@('--install','--package-directory',$package,'--install-directory',$application,'--data-directory',$data)
 if($migrate){$arguments+=@('--migrate-from',$source)}
 $p=Start-Owned (Join-Path $ReleaseDirectory 'DlutTimetable.Desktop.exe') $arguments
 $w=Wait-For {Own $p.Id '大工课表 · 安装与更新'}
 Wait-For {(Id (Ui $w) 'InstallUpdate').Current.IsEnabled}|Out-Null
 return [pscustomobject]@{Process=$p;Window=$w}
}
function Finish-Install($installer,[string]$action){
 Invoke-Id $installer.Window $action
 Wait-For {
  $state=(Id (Ui $installer.Window) 'InstallerStatus').Current.Name
  if($state.StartsWith('未完成：')){throw $state}
  (Id (Ui $installer.Window) 'InstallLaunch').Current.IsEnabled
 }|Out-Null
}
function Launch-Installed($installer){
 $state=Get-Content (Join-Path $application 'install-state.json') -Raw|ConvertFrom-Json
 $exe=Join-Path $application ($state.currentVersion+'/DlutTimetable.Desktop.exe')
 Invoke-Id $installer.Window 'InstallLaunch'
 $processInfo=Wait-For {Get-CimInstance Win32_Process -Filter "Name='DlutTimetable.Desktop.exe'"|Where-Object {$_.ExecutablePath -eq $exe -and $_.CommandLine.Contains($data)}|Select-Object -First 1}
 $p=Get-Process -Id $processInfo.ProcessId;$owned.Add($p)
 Wait-For {Own $p.Id '大工课表 · 周课表'}|Out-Null
 if(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($p.Id) AND Name='DlutTimetable.AuthWorker.exe'"){throw 'Installed cache startup opened school browser'}
 $settings=Settings $p;Select-Tab $settings '数据与账号'
 if(!(Id (Ui $settings) 'OpenCalendarSettings').Current.IsEnabled){throw 'Installed application did not load the migrated dataset'}
 return $p
}
try {
 if($RepairStage){
  $stage='missing incoming manifest stays a recoverable UI error'
  $invalid=Join-Path $fixture 'empty-package';New-Item -ItemType Directory -Path $invalid|Out-Null
  $refusedApp=Join-Path $fixture 'refused-app';$refusedData=Join-Path $fixture 'refused-data'
  $refused=Start-Owned (Join-Path $ReleaseDirectory 'DlutTimetable.Desktop.exe') @('--install','--package-directory',$invalid,'--install-directory',$refusedApp,'--data-directory',$refusedData)
  $w=Wait-For {Own $refused.Id '大工课表 · 安装与更新'}
  Wait-For {(Id (Ui $w) 'InstallerStatus').Current.Name.StartsWith('无法安装：')}|Out-Null
  if($refused.HasExited -or (Id (Ui $w) 'InstallUpdate').Current.IsEnabled -or (Test-Path -LiteralPath $refusedApp) -or (Test-Path -LiteralPath $refusedData)){throw 'Invalid package opened storage or crashed the installer'}
  Exit-Owned $refused
 }
 $stage='source JSON import'
 $p=Start-Owned (Join-Path $ReleaseDirectory 'DlutTimetable.Desktop.exe') @('--data-directory',$source)
 $settings=Wait-For {Own $p.Id '大工课表 · 设置'};Select-Tab $settings '数据与账号'
 (Named (Ui $settings) '导入 JSON').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
 $dialog=Wait-For {Own $p.Id '导入个人课表'}
 if(![OwnedDialogCancel]::SetFileName([intptr]$dialog.Hwnd,$p.Id,(Join-Path $root 'fixtures/dlut/synthetic-term.json')) -or ![OwnedDialogCancel]::Save([intptr]$dialog.Hwnd,$p.Id)){throw 'Cannot import owned fictional source'}
 Wait-For {(Id (Ui $settings) 'OpenCalendarSettings').Current.IsEnabled}|Out-Null
 Exit-Owned $p
 $settingsPath=Join-Path $source 'settings.json';$s=Get-Content $settingsPath -Raw|ConvertFrom-Json;$s.startupDesired=$false;$s.startupInitialized=$true;$s|ConvertTo-Json -Depth 20|Set-Content $settingsPath -Encoding utf8NoBOM
 $sourceHash=(Get-FileHash -LiteralPath (Join-Path $source 'timetable.db')).Hash
 $stage='first install with migration';$first=Installer $ReleaseDirectory $true;Finish-Install $first 'InstallUpdate'
 $state1=Get-Content (Join-Path $application 'install-state.json') -Raw|ConvertFrom-Json
 $targetSettings=Get-Content (Join-Path $data 'settings.json') -Raw|ConvertFrom-Json
 $expectedScope=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFullPath($source).TrimEnd('\').ToUpperInvariant())))
 if($targetSettings.startupDesired -or $targetSettings.authProfileScopeId -ne $expectedScope){throw 'Migration lost startup opt-out or original profile scope'}
 $main1=Launch-Installed $first
 $targetHash=(Get-FileHash -LiteralPath (Join-Path $data 'timetable.db')).Hash
 $stage='prepare local second-version fixture'
 # Same real binaries with a separate unsigned preview manifest. This is only an update/rollback
 # fixture, not a second released build; no modified code or personal data is included.
 $nextPackage=Join-Path $fixture 'second-preview-fixture';New-Item -ItemType Directory -Path $nextPackage|Out-Null
 Get-ChildItem -LiteralPath $ReleaseDirectory -Force|Copy-Item -Destination $nextPackage -Recurse
 $manifestPath=Join-Path $nextPackage 'release-manifest.json';$manifest=Get-Content $manifestPath -Raw|ConvertFrom-Json;$manifest.version='0.2.1-preview.fixture';$manifest|ConvertTo-Json -Depth 10|Set-Content $manifestPath -Encoding utf8NoBOM
 $stage='update while first installed main is running';$second=Installer $nextPackage $false;Finish-Install $second 'InstallUpdate'
 if(!$main1.WaitForExit(1000)){throw 'Update did not wait for exact old session shutdown'}
 $state2=Get-Content (Join-Path $application 'install-state.json') -Raw|ConvertFrom-Json
 if($state2.previousVersion -ne $state1.currentVersion -or $state2.currentVersion -eq $state1.currentVersion){throw 'Update pointer or previous-version retention failed'}
 $main2=Launch-Installed $second
 $stage='rollback while second installed main is running';$restore=Installer $ReleaseDirectory $false;Finish-Install $restore 'InstallRollback'
 if(!$main2.WaitForExit(1000)){throw 'Rollback did not wait for exact current session shutdown'}
 $state3=Get-Content (Join-Path $application 'install-state.json') -Raw|ConvertFrom-Json
 if($state3.currentVersion -ne $state1.currentVersion){throw 'Rollback did not select first version'}
 $main3=Launch-Installed $restore;Exit-Owned $main3
 if($RepairStage){
  $stage='damage an unloaded worker configuration while main is running'
  $originalExe=Join-Path $application ($state3.currentVersion+'/DlutTimetable.Desktop.exe')
  $running=Start-Owned $originalExe @('--data-directory',$data);Wait-For {Own $running.Id '大工课表 · 周课表'}|Out-Null
  $damaged=Join-Path ([IO.Path]::GetDirectoryName($originalExe)) 'DlutTimetable.AuthWorker.runtimeconfig.json'
  $broken=[IO.File]::ReadAllBytes($damaged);$broken[0]=$broken[0] -bxor 1;[IO.File]::WriteAllBytes($damaged,$broken)
  $brokenHash=(Get-FileHash -LiteralPath $damaged).Hash
  $extra=Join-Path ([IO.Path]::GetDirectoryName($originalExe)) 'personal-extra.txt';Set-Content -LiteralPath $extra -Value 'keep this extra file' -Encoding utf8NoBOM
  $stage='repair running install from identical clean package'
  $fix=Installer $ReleaseDirectory $false;Finish-Install $fix 'InstallUpdate'
  if(!$running.WaitForExit(1000)){throw 'Repair did not wait for old main exit'}
  $fixed=Get-Content (Join-Path $application 'install-state.json') -Raw|ConvertFrom-Json
  if($fixed.currentVersion -eq $state3.currentVersion -or $fixed.currentVersion -notmatch '-repair-[a-f0-9]{8}$' -or $fixed.previousVersion -ne $state3.previousVersion){throw 'Repair pointer or verified previous version is wrong'}
  if((Get-FileHash -LiteralPath $damaged).Hash -ne $brokenHash -or !(Test-Path -LiteralPath $extra)){throw 'Repair overwrote original damaged files or extras'}
  $newConfig=Join-Path $application ($fixed.currentVersion+'/DlutTimetable.AuthWorker.runtimeconfig.json')
  if((Get-FileHash -LiteralPath $newConfig).Hash -ne (Get-FileHash -LiteralPath (Join-Path $ReleaseDirectory 'DlutTimetable.AuthWorker.runtimeconfig.json')).Hash){throw 'Worker config was not repaired'}
  $fixedMain=Launch-Installed $fix
  $stage='worker from repaired directory can actually open and cancel'
  $settings=Settings $fixedMain;Invoke-Id $settings 'SchoolManualRefresh'
  $worker=Wait-For {Get-CimInstance Win32_Process -Filter "ParentProcessId=$($fixedMain.Id) AND Name='DlutTimetable.AuthWorker.exe'"|Select-Object -First 1}
  $login=Wait-For {Own $worker.ProcessId '大工课表 · 学校登录'}
  Wait-For {$ready=Id (Ui $login) 'AuthBrowserState';$ready -and $ready.Current.HelpText -eq 'browser-ready'}|Out-Null
  $actualWorker=[IO.Path]::GetFullPath((Get-Process -Id $worker.ProcessId).MainModule.FileName)
  $expectedWorker=[IO.Path]::GetFullPath((Join-Path $application ($fixed.currentVersion+'/DlutTimetable.AuthWorker.exe')))
  if(![string]::Equals($actualWorker,$expectedWorker,[StringComparison]::OrdinalIgnoreCase)){throw ('Worker path mismatch; actual='+$actualWorker+'; expected='+$expectedWorker)}
  Invoke-Id $login 'AuthCancel';Wait-For {!(Get-CimInstance Win32_Process -Filter "ProcessId=$($worker.ProcessId)")}|Out-Null
  $stage='repeat install reuses healthy repair copy'
  $repeat=Installer $ReleaseDirectory $false;Finish-Install $repeat 'InstallUpdate'
  if(!$fixedMain.WaitForExit(1000)){throw 'Repeat installation did not close repaired main'}
  $same=Get-Content (Join-Path $application 'install-state.json') -Raw|ConvertFrom-Json
  if($same.currentVersion -ne $fixed.currentVersion -or $same.previousVersion -ne $fixed.previousVersion){throw 'Repeat installation created an unnecessary copy or lost previous version'}
  Exit-Owned $repeat.Process
  $stage='uninstall repaired current version, keep cache and damaged original'
  $fixedExe=Join-Path $application ($fixed.currentVersion+'/DlutTimetable.Desktop.exe')
  $uninstall=Start-Owned $fixedExe @('--uninstall','--install-directory',$application,'--data-directory',$data,'--uninstall-quiet')
  $w=Wait-For {Own $uninstall.Id '大工课表 · 卸载'};Wait-For {(Id (Ui $w) 'UninstallConfirm').Current.IsEnabled}|Out-Null
  Invoke-Id $w 'UninstallConfirm';Wait-For {$uninstall.HasExited}|Out-Null
  $resultFile=Wait-For {
   foreach($dir in Get-ChildItem -LiteralPath ([IO.Path]::GetTempPath()) -Directory -Filter 'DlutUninstall-*'|Sort-Object LastWriteTime -Descending){
    $plan=Join-Path $dir.FullName 'plan.json';$result=Join-Path $dir.FullName 'result.json'
    if((Test-Path -LiteralPath $plan) -and (Test-Path -LiteralPath $result)){$p=Get-Content -LiteralPath $plan -Raw|ConvertFrom-Json;if($p.installDirectory -eq $application){return $result}}
   }
  }
  $result=Get-Content -LiteralPath $resultFile -Raw|ConvertFrom-Json;if(!$result.passed){throw $result.message}
  $helperPlan=Join-Path ([IO.Path]::GetDirectoryName($resultFile)) 'plan.json'
  Wait-For {!(Get-CimInstance Win32_Process -Filter "Name='powershell.exe'"|Where-Object {$_.CommandLine -and $_.CommandLine.Contains($helperPlan)})}|Out-Null
  if((Test-Path -LiteralPath $fixedExe) -or (Get-FileHash -LiteralPath $damaged).Hash -ne $brokenHash -or !(Test-Path -LiteralPath $extra)){throw 'Uninstall did not preserve damaged original/extra or remove repaired executable'}
  $stage='reinstall same package after keep-data uninstall with residual files'
  $reinstall=Installer $ReleaseDirectory $false;Finish-Install $reinstall 'InstallUpdate'
  $reinstalled=Get-Content (Join-Path $application 'install-state.json') -Raw|ConvertFrom-Json
  if($reinstalled.currentVersion -eq $fixed.currentVersion -or $reinstalled.previousVersion){throw 'Reinstall selected incomplete files or a damaged rollback target'}
  $last=Launch-Installed $reinstall;Exit-Owned $last
  $repairEvidence=[ordered]@{invalidPackageDidNotCrashOrOpenStorage=$true;runningMainClosedBeforeSwitch=$true;freshVerifiedDirectoryUsed=$true;damagedOriginalAndExtraPreserved=$true;onlyVerifiedPreviousVersionKept=$true;repairedWorkerBrowserReadyAndCancelled=$true;repeatInstallReusedHealthyCopy=$true;repairedVersionUninstalled=$true;cleanupHelperExited=$true;samePackageReinstalledWithResidualFiles=$true;cacheAndPreferencesPreserved=$true}
 }
 if((Get-FileHash -LiteralPath (Join-Path $source 'timetable.db')).Hash -ne $sourceHash){throw 'Source data was changed'}
 if((Get-FileHash -LiteralPath (Join-Path $data 'timetable.db')).Hash -ne $targetHash){throw 'Update or rollback changed cached dataset'}
 $finalSettings=Get-Content (Join-Path $data 'settings.json') -Raw|ConvertFrom-Json
 if($finalSettings.startupDesired -or $finalSettings.authProfileScopeId -ne $expectedScope){throw 'Update or rollback lost preferences'}
 $report=[ordered]@{passed=$true;scope='Isolated real self-contained binaries; fictional JSON dataset; second manifest uses same binaries';releaseManifestSha256=(Get-FileHash -LiteralPath (Join-Path $ReleaseDirectory 'release-manifest.json')).Hash;migrationSucceeded=$true;originalDataUnchanged=$true;cachedUiOpenedFromInstalledPath=$true;updateWaitedForExactSession=$true;rollbackWaitedForExactSession=$true;previousVersionRestored=$true;startupOptOutPreserved=$true;authScopePreservedWithoutCookieCopy=$true;updateAndRollbackKeptDataset=$true;startupOpenedNoBrowser=$true;checkedAt=[DateTimeOffset]::Now.ToString('O')}
 if($RepairStage){$report['repair']=$repairEvidence}
} finally {
 foreach($p in $owned){Exit-Owned $p}
 $after=(Get-ItemProperty -LiteralPath $runKey -Name DlutDesktopTimetable -ErrorAction SilentlyContinue).DlutDesktopTimetable
 if($after -ne $startupBefore){throw 'Isolated install validation changed real startup registration'}
}
$report['realStartupUnchanged']=$true
$reportPath=Join-Path $root $(if($RepairStage){'docs/validation/installation-repair-ui.json'}else{'docs/validation/installation-ui.json'})
$report|ConvertTo-Json -Depth 6|Set-Content $reportPath -Encoding utf8NoBOM
Get-Content $reportPath
