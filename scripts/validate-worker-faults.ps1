param([string]$AppPath="artifacts/privacy-fault-probe/ManagedFault.Spike.dll")
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
$taskRoot=Join-Path $root ('artifacts/worker-fault-'+[guid]::NewGuid().ToString('N'));New-Item -ItemType Directory -Path $taskRoot|Out-Null
$trace=Join-Path $env:LOCALAPPDATA 'DLUTDesktopTimetable/logs/auth-worker.log';$process=$null
try {
 foreach($mode in @('ui','background','unobserved')){
  $data=Join-Path $taskRoot $mode;New-Item -ItemType Directory -Path $data|Out-Null
  $errorFile=Join-Path $data 'stderr.txt';$output=Join-Path $data 'stdout.txt'
  $process=Start-Process -FilePath (Join-Path $root '.tools/dotnet/dotnet.exe') -ArgumentList @(('"'+$app+'"'),'--data-directory',('"'+$data+'"'),'--fault-mode',('worker-'+$mode)) -WindowStyle Hidden -PassThru -RedirectStandardError $errorFile -RedirectStandardOutput $output
  $null=$process.Handle
  Wait-For {Owned '学校故障探针'} 'worker policy probe ready'
  $watch=[Diagnostics.Stopwatch]::StartNew();[IO.File]::WriteAllText((Join-Path $data 'fault.trigger'),'trigger')
  Check "$mode bounded-worker-fault-exit" ($process.WaitForExit(5000) -and $process.ExitCode -eq 74)
  $elapsed=$watch.Elapsed.TotalMilliseconds
  $lines=@([IO.File]::ReadAllLines($trace)|Where-Object {$_ -match "pid=$($process.Id) "})
  Check "$mode only-typed-correlated-fault" ($lines.Count -eq 1 -and $lines[0] -match 'request=[0-9a-f]{32} Unhandled$')
  Check "$mode trace-no-exception-payload" (($lines -join "`n") -notmatch 'PrivateCrashSecret')
  Check "$mode streams-no-exception-payload" (([IO.File]::ReadAllText($errorFile)+[IO.File]::ReadAllText($output)) -notmatch 'PrivateCrashSecret')
  Check "$mode no-personal-store-created" (!(Test-Path -LiteralPath (Join-Path $data 'timetable.db')))
  $cases.Add(@{mode=$mode;exitCode=$process.ExitCode;elapsedMilliseconds=$elapsed;passed=$true});$process.Dispose();$process=$null
 }
 @{capturedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');checks=$checks;cases=$cases;scope='Exact production WorkerFaultPolicy/WorkerTrace source linked into isolated probe; no browser, authentication or user data. Parent Job cleanup is verified separately.'}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $root 'docs/validation/worker-managed-faults.json') -Encoding utf8
 Write-Output "Worker fault policy: $($checks.Count) checks passed."
} finally {
 if($process){if(!$process.HasExited){$process.Kill($true);$process.WaitForExit()};$process.Dispose()}
 [DesktopAudit]::Move($cursor[0],$cursor[1])
}
