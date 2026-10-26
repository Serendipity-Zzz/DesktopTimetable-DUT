param([string]$AppPath="artifacts/privacy-review/DlutTimetable.Desktop.dll")
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$app=(Resolve-Path -LiteralPath $AppPath).Path
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
# Reuse only the PID-scoped native helper, without executing the broader smoke.
$helper=[regex]::Match((Get-Content (Join-Path $PSScriptRoot 'smoke-desktop.ps1') -Raw),"(?s)Add-Type @'\r?\n(.*?)\r?\n'@").Groups[1].Value
Add-Type $helper
$checks=[Collections.Generic.List[object]]::new()
$cases=[Collections.Generic.List[object]]::new()
$cursor=[DesktopAudit]::Cursor()
function Check($name,$passed){$checks.Add(@{name=$name;passed=[bool]$passed});if(!$passed){throw "Failed: $name"}}
function Owned($role){@([DesktopAudit]::Windows($process.Id)|Where-Object {$_.Visible -and $_.Title -eq ('大工课表 · '+$role)})|Select-Object -First 1}
function Wait-For([scriptblock]$condition,$label){
 $deadline=[datetime]::UtcNow.AddSeconds(12)
 while([datetime]::UtcNow -lt $deadline){if(&$condition){return};if($process.HasExited){throw "Process exited: $label"};Start-Sleep -Milliseconds 35}
 throw "Timed out: $label"
}
function Element($role){$w=Owned $role;if($w){[System.Windows.Automation.AutomationElement]::FromHandle([intptr]$w.Hwnd)}}
function Named($element,$name){if($element){$element.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,$name))}}
function Invoke-Control($role,$name){
 Wait-For { $null -ne (Named (Element $role) $name) } $name
 (Named (Element $role) $name).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}
$taskRoot=Join-Path $root ('artifacts/install-privacy-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskRoot|Out-Null
$runKey='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$before=(Get-ItemProperty -LiteralPath $runKey -Name DlutDesktopTimetable -ErrorAction SilentlyContinue).DlutDesktopTimetable
$process=$null
try {
 foreach($mode in @('install','uninstall')){
  $directory=Join-Path $taskRoot ($mode+'-PrivatePathSecret');New-Item -ItemType Directory -Path $directory|Out-Null
  $data=Join-Path $taskRoot ($mode+'-data')
  $bad=Join-Path $directory $(if($mode -eq 'install'){'release-manifest.json'}else{'install-state.json'})
  [IO.File]::WriteAllText($bad,'{"PrivatePayloadSecret":');$hash=(Get-FileHash -LiteralPath $bad).Hash
  $options=if($mode -eq 'install'){@('--install','--package-directory',('"'+$directory+'"'),'--install-directory',('"'+(Join-Path $taskRoot 'target')+'"'))}else{@('--uninstall','--install-directory',('"'+$directory+'"'))}
  $process=Start-Process -FilePath (Join-Path $root '.tools/dotnet/dotnet.exe') -ArgumentList (@(('"'+$app+'"'))+$options+@('--data-directory',('"'+$data+'"'))) -WindowStyle Hidden -PassThru
  $null=$process.Handle
  $role=if($mode -eq 'install'){'安装与更新'}else{'卸载'}
  $statusId=if($mode -eq 'install'){'InstallerStatus'}else{'UninstallStatus'}
  Wait-For {
   $window=Element $role;if(!$window){return $false}
   $script:statusControl=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$statusId))
   $statusControl -and $statusControl.Current.Name.Contains('Failed')
  } 'typed install failure'
  $status=$statusControl.Current.Name
  Check "$mode fixed-error-and-correlation" ($status -match 'Failed · [0-9a-f]{32}' -and !$status.Contains('Secret'))
  Check "$mode invalid-source-unchanged" ($hash -ceq (Get-FileHash -LiteralPath $bad).Hash)
  $journal=Join-Path $data 'logs/runtime-diagnostics.jsonl';Wait-For {Test-Path -LiteralPath $journal} 'fault journal'
  $text=[IO.File]::ReadAllText($journal);$entry=($text|ConvertFrom-Json)
  Check "$mode fault-correlates-ui" ($status.Contains($entry.CorrelationId.Replace('-','')))
  Check "$mode log-omits-path-and-source" (!$text.Contains('Secret') -and !$text.Contains($directory))
  (Element $role).GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close()
  Check "$mode closes-normally" ($process.WaitForExit(5000) -and $process.ExitCode -eq 0)
  $process.Dispose();$process=$null
 }
 $helper=Join-Path $taskRoot 'cleanup-helper';New-Item -ItemType Directory -Path $helper|Out-Null
 $scriptPath=Join-Path $helper 'cleanup.ps1';[IO.File]::WriteAllText($scriptPath,[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'uninstall-cleanup.ps1')),[Text.UTF8Encoding]::new($true))
 $plan=Join-Path $helper 'PrivatePlanSecret.json';[IO.File]::WriteAllText($plan,'PrivatePayloadSecret')
 $powershell=Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/powershell.exe'
 $start=[Diagnostics.ProcessStartInfo]::new($powershell);$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardError=$true;$start.RedirectStandardOutput=$true
 $start.Environment['PSModulePath']=Join-Path (Split-Path $powershell -Parent) 'Modules'
 foreach($arg in @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$scriptPath,'-PlanPath',$plan,'-ParentProcessId','2147483000','-ParentStartTicks','0','-Quiet')){$start.ArgumentList.Add($arg)}
 $process=[Diagnostics.Process]::Start($start);$errorTask=$process.StandardError.ReadToEndAsync();$outputTask=$process.StandardOutput.ReadToEndAsync()
 $null=$process.Handle
 Check 'cleanup-invalid-plan-bounded-exit' ($process.WaitForExit(10000))
 $result=[IO.File]::ReadAllText((Join-Path $helper 'result.json'));$value=$result|ConvertFrom-Json
 Check 'cleanup-error-excludes-private-path-and-payload' (!$value.passed -and $result.Contains('CleanupFailed') -and !$result.Contains('Secret'))
 Check 'cleanup-invalid-plan-preserved' ([IO.File]::ReadAllText($plan) -eq 'PrivatePayloadSecret')
 Check 'cleanup-standard-streams-exclude-private-payload' (($errorTask.GetAwaiter().GetResult()+$outputTask.GetAwaiter().GetResult()) -notmatch 'Secret')
 $process.Dispose();$process=$null
 $after=(Get-ItemProperty -LiteralPath $runKey -Name DlutDesktopTimetable -ErrorAction SilentlyContinue).DlutDesktopTimetable
 Check 'real-startup-registration-unchanged' ($before -ceq $after)
 @{capturedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');checks=$checks;scope='Isolated invalid package/state/cleanup-plan errors containing synthetic secret markers. No actual installation, account, uninstall or real registry writes.'}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $root 'docs/validation/installation-privacy.json') -Encoding utf8
 Write-Output "Installation privacy: $($checks.Count) checks passed."
} finally {
 if($process){if(!$process.HasExited){$process.Kill($true);$process.WaitForExit()};$process.Dispose()}
 [DesktopAudit]::Move($cursor[0],$cursor[1])
}
