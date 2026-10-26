param([string]$ReleaseDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if(!$ReleaseDirectory){$ReleaseDirectory=(Get-Content (Join-Path $root 'docs/validation/current-release.json') -Raw|ConvertFrom-Json).directory}
$verified=& (Join-Path $PSScriptRoot 'verify-release.ps1') -Directory $ReleaseDirectory
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type ([regex]::Match((Get-Content (Join-Path $PSScriptRoot 'smoke-desktop.ps1') -Raw),"(?s)Add-Type @'\r?\n(.*?)\r?\n'@").Groups[1].Value)
$fixture=Join-Path $root ('artifacts/portable-release-'+[Guid]::NewGuid().ToString('N'))
$application=Join-Path $fixture '中文 路径/应用'
$data=Join-Path $fixture 'data'
New-Item -ItemType Directory -Path $application,$data -Force|Out-Null
Get-ChildItem -LiteralPath $ReleaseDirectory -Force|Copy-Item -Destination $application -Recurse
$verified=& (Join-Path $PSScriptRoot 'verify-release.ps1') -Directory $application
$start=[Diagnostics.ProcessStartInfo]::new((Join-Path $application 'DlutTimetable.Desktop.exe'))
$start.UseShellExecute=$false;$start.WorkingDirectory=$fixture
$start.ArgumentList.Add('--data-directory');$start.ArgumentList.Add($data)
foreach($key in @('DOTNET_ROOT','DOTNET_ROOT_X64','DOTNET_ROOT_X86','DOTNET_HOST_PATH','DOTNET_SHARED_STORE','DOTNET_ADDITIONAL_DEPS')){$start.Environment.Remove($key)|Out-Null}
$start.Environment['DOTNET_ROOT']=Join-Path $fixture 'missing-runtime'
$start.Environment['DOTNET_ROOT_X64']=$start.Environment['DOTNET_ROOT'];$start.Environment['DOTNET_MULTILEVEL_LOOKUP']='0'
$start.Environment['PATH']=$env:SystemRoot+'\System32;'+$env:SystemRoot
$runKey='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$startupBefore=(Get-ItemProperty -LiteralPath $runKey -Name DlutDesktopTimetable -ErrorAction SilentlyContinue).DlutDesktopTimetable
$process=[Diagnostics.Process]::Start($start)
function Named($e,[string]$name){$e.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name))}
function Id($e,[string]$name){$e.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$name))}
function Own([int]$owner,[string]$title){[DesktopAudit]::Windows($owner,$true)|Where-Object {$_.Title -eq $title -and $_.Visible}|Select-Object -First 1}
function Ui($window){[System.Windows.Automation.AutomationElement]::FromHandle([intptr]$window.Hwnd)}
function Wait-For([scriptblock]$condition){$until=[DateTime]::UtcNow.AddSeconds(20);while([DateTime]::UtcNow -lt $until){$v=&$condition;if($v){return $v};if($process.HasExited){throw 'Portable application exited'};Start-Sleep -Milliseconds 50};throw 'Portable UI timed out'}
function HasBundledRuntime([int]$owner){$module=(Get-Process -Id $owner).Modules|Where-Object ModuleName -eq 'coreclr.dll'|Select-Object -First 1;return $module -and [IO.Path]::GetFullPath($module.FileName) -eq (Join-Path $application 'coreclr.dll')}
try {
 $stage='main settings'
 $settingsWindow=Wait-For {Own $process.Id '大工课表 · 设置'};$settings=Ui $settingsWindow
 if(!(HasBundledRuntime $process.Id)){throw 'Main application did not use bundled runtime'}
 if(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($process.Id) AND Name='DlutTimetable.AuthWorker.exe'"){throw 'Portable startup opened a school worker'}
 (Named $settings '数据与账号').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
 $refresh=Wait-For {Id $settings 'SchoolManualRefresh'}
 if(!$refresh.Current.IsEnabled){throw 'Packaged school worker unavailable'}
 $refresh.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
 $stage='worker process'
 $worker=Wait-For {Get-CimInstance Win32_Process -Filter "ParentProcessId=$($process.Id) AND Name='DlutTimetable.AuthWorker.exe'"|Select-Object -First 1}
 $stage='worker window'
 $login=Wait-For {Own $worker.ProcessId '大工课表 · 学校登录'}
 $stage='browser readiness'
 Wait-For {$ownUi=Ui $login;$state=Id $ownUi 'AuthBrowserState';$state -and $state.Current.HelpText -eq 'browser-ready'}|Out-Null
 $loginUi=Ui $login
 if(!(HasBundledRuntime $worker.ProcessId)){throw 'School worker did not use shared bundled runtime'}
 (Id $loginUi 'AuthCancel').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
 Wait-For {!(Get-CimInstance Win32_Process -Filter "ProcessId=$($worker.ProcessId)")}|Out-Null
 Wait-For {(Id $settings 'SchoolManualRefresh').Current.IsEnabled}|Out-Null
 $signature=(Get-AuthenticodeSignature -LiteralPath (Join-Path $application 'DlutTimetable.Desktop.exe')).Status.ToString()
 [ordered]@{passed=$true;scope='Relocated self-contained bundle, isolated empty data/profile, no credentials entered';manifestVerified=$verified.verified;fileCount=$verified.files;bundleBytes=$verified.bytes;chineseAndSpacePath=$true;developerRuntimeRemovedFromEnvironment=$true;mainLoadedBundledClr=$true;workerLoadedSameBundledClr=$true;startupOpenedNoBrowser=$true;manualWorkerReady=$true;cancelClosedWorker=$true;appSignatureStatus=$signature;checkedAt=[DateTimeOffset]::Now.ToString('O')}|ConvertTo-Json|Set-Content (Join-Path $root 'docs/validation/portable-release-ui.json') -Encoding utf8
 Get-Content (Join-Path $root 'docs/validation/portable-release-ui.json')
} catch {
 Write-Output ('Portable validation failed at: '+$stage)
 if($login){$state=Id (Ui $login) 'AuthBrowserState';if($state){Write-Output ('Own browser state: '+$state.Current.Name+'; help='+$state.Current.HelpText)}}
 throw
} finally {
 if(!$process.HasExited){
  $settingsWindow=Own $process.Id '大工课表 · 设置'
  if($settingsWindow){$settings=Ui $settingsWindow;(Named $settings '通用').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select();(Named $settings '退出程序').GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke();if(!$process.WaitForExit(10000)){throw 'Portable owned process did not exit'}}
 }
 $after=(Get-ItemProperty -LiteralPath $runKey -Name DlutDesktopTimetable -ErrorAction SilentlyContinue).DlutDesktopTimetable
 if($after -ne $startupBefore){throw 'Isolated portable validation changed real startup registration'}
}
