$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$source=Get-Content (Join-Path $PSScriptRoot 'smoke-desktop.ps1') -Raw
$helper=[regex]::Match($source,"(?s)Add-Type @'\r?\n(.*?)\r?\n'@")
if(!$helper.Success){throw 'Owned-window helper source unavailable'}
Add-Type -TypeDefinition $helper.Groups[1].Value
$dotnet=Join-Path $root '.tools/dotnet/dotnet.exe'
$app=Join-Path $root 'src/DlutTimetable.Desktop/bin/Release/net10.0-windows10.0.19041.0/DlutTimetable.Desktop.dll'
$registrationPath='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$before=(Get-ItemProperty -LiteralPath $registrationPath -Name DlutDesktopTimetable -ErrorAction SilentlyContinue).DlutDesktopTimetable
$checks=[System.Collections.Generic.List[object]]::new()
function Check([string]$name,[bool]$passed){$checks.Add(@{name=$name;passed=$passed})}
foreach($profile in @('missing','corrupt','future')){
 $directory=Join-Path $root ('artifacts/background-startup-'+$profile+'-'+[Guid]::NewGuid().ToString('N'))
 New-Item -ItemType Directory -Path $directory | Out-Null
 $settingsPath=Join-Path $directory 'settings.json'
 $original=switch($profile){'corrupt'{'broken'} 'future'{'{"schemaVersion":999}'} default{$null}}
 if($null -ne $original){[IO.File]::WriteAllText($settingsPath,$original)}
 $process=Start-Process -FilePath $dotnet -ArgumentList @(('"'+$app+'"'),'--background','--data-directory',('"'+$directory+'"')) -WindowStyle Hidden -PassThru
 function OwnWindow([string]$role){[DesktopAudit]::Windows($process.Id)|Where-Object Title -eq ('大工课表 · '+$role)|Select-Object -First 1}
 function Wait-For([scriptblock]$condition){
  $deadline=[DateTime]::UtcNow.AddSeconds(10)
  while([DateTime]::UtcNow -lt $deadline){if(&$condition){return};if($process.HasExited){throw 'Background process exited unexpectedly'};Start-Sleep -Milliseconds 30}
  throw 'Background startup observation timed out'
 }
 try {
  Wait-For {OwnWindow '按钮0'}
  Check ($profile+'-starts-panel-without-settings-popup') ((OwnWindow '周课表') -and !(OwnWindow '设置'))
  $gear=[System.Windows.Automation.AutomationElement]::FromHandle([IntPtr](OwnWindow '按钮0').Hwnd)
  $settingsName=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'设置')
  $gear.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$settingsName).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Wait-For {OwnWindow '设置'}
  $settings=[System.Windows.Automation.AutomationElement]::FromHandle([IntPtr](OwnWindow '设置').Hwnd)
  $general=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'通用')
  $settings.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$general).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
  $startupName=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'登录 Windows 后启动')
  $startup=$settings.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$startupName)
  Check ($profile+'-isolated-startup-switch-disabled') ($startup -and !$startup.Current.IsEnabled)
  $exitName=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'退出程序')
  $settings.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$exitName).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Check ($profile+'-explicit-exit-clean') ($process.WaitForExit(10000) -and $process.ExitCode -eq 0)
  if($null -ne $original){Check ($profile+'-original-settings-preserved') ([IO.File]::ReadAllText($settingsPath) -ceq $original)}
 } finally {
  # Only the process launched by this probe can be terminated if explicit exit failed.
  if(!$process.HasExited){$process.Kill($true);$process.WaitForExit()};$process.Dispose()
 }
}
$after=(Get-ItemProperty -LiteralPath $registrationPath -Name DlutDesktopTimetable -ErrorAction SilentlyContinue).DlutDesktopTimetable
Check 'real-login-registration-unchanged' ($before -ceq $after)
@{capturedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');checks=$checks;scope='Fresh --background processes in isolated data directories; not an actual Windows logon test.'}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $root 'docs/validation/background-startup.json') -Encoding utf8
if(@($checks|Where-Object {!$_.passed}).Count){throw 'Background startup checks failed'}
Write-Output "Background startup: $($checks.Count) checks passed; no real login registration changed."
