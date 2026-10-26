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
$taskRoot=Join-Path $root ('artifacts/managed-fault-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskRoot|Out-Null
function Owned-Tree($parent){
 $ids=@([int]$parent);$all=@(Get-CimInstance Win32_Process)
 do{$next=@($all|Where-Object {$_.ParentProcessId -in $ids -and $_.ProcessId -notin $ids}|ForEach-Object {[int]$_.ProcessId});$ids+=$next}while($next.Count)
 return $ids
}
$process=$null
try {
 foreach($case in @('ui','background','unobserved','ui-worker','background-worker')){
  $mode=$case.Split('-')[0];$data=Join-Path $taskRoot $case;New-Item -ItemType Directory -Path $data|Out-Null
  [IO.File]::WriteAllText((Join-Path $data 'settings.json'),'{"schemaVersion":1,"startupDesired":false}')
  $output=Join-Path $data 'stdout.txt';$errorFile=Join-Path $data 'stderr.txt';$held=[Collections.Generic.List[Diagnostics.Process]]::new()
  try {
   $process=Start-Process -FilePath (Join-Path $root '.tools/dotnet/dotnet.exe') -ArgumentList @('"'+$app+'"','--data-directory','"'+$data+'"','--fault-mode',$mode) -WindowStyle Hidden -PassThru -RedirectStandardOutput $output -RedirectStandardError $errorFile
   $null=$process.Handle
   Wait-For {(Owned '设置') -and (Named (Element '设置') '数据与账号')} 'probe session ready'
   if($case.EndsWith('-worker')){
    (Named (Element '设置') '数据与账号').GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Invoke-Control '设置' '从学校刷新课表'
    Wait-For {Get-CimInstance Win32_Process -Filter "ParentProcessId=$($process.Id) AND Name='DlutTimetable.AuthWorker.exe'"} 'owned worker'
    $worker=Get-CimInstance Win32_Process -Filter "ParentProcessId=$($process.Id) AND Name='DlutTimetable.AuthWorker.exe'"|Select-Object -First 1
    Wait-For {
     $w=@([DesktopAudit]::Windows($worker.ProcessId)|Where-Object {$_.Visible -and $_.Title -eq '大工课表 · 学校登录'})|Select-Object -First 1
     if(!$w){return $false}
     $element=[System.Windows.Automation.AutomationElement]::FromHandle([intptr]$w.Hwnd)
     $state=$element.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'AuthBrowserState'))
     $state -and $state.Current.HelpText -eq 'browser-ready'
    } 'owned browser ready'
    foreach($id in @(Owned-Tree $worker.ProcessId)){
     try{$p=[Diagnostics.Process]::GetProcessById($id);$null=$p.Handle;$held.Add($p)}catch [ArgumentException]{}
    }
    Check "$case observed-browser-group" ($held.Count -ge 2)
   }
   Start-Sleep -Milliseconds 150
   $cache=Join-Path $data 'timetable.db';$cacheBefore=(Get-FileHash -LiteralPath $cache).Hash
   $watch=[Diagnostics.Stopwatch]::StartNew();[IO.File]::WriteAllText((Join-Path $data 'fault.trigger'),'trigger')
   Check "$case bounded-fault-exit" ($process.WaitForExit(12000) -and $process.ExitCode -eq 1)
   $elapsed=$watch.Elapsed.TotalMilliseconds
   foreach($p in $held){Check "$case owned-$($p.Id)-exits" ($p.WaitForExit(5000))}
   Check "$case cache-unchanged" ($cacheBefore -ceq (Get-FileHash -LiteralPath $cache).Hash)
   $logs=Get-ChildItem -LiteralPath (Join-Path $data 'logs') -Filter 'runtime-diagnostics.jsonl*'
   $lines=@($logs|ForEach-Object {Get-Content -LiteralPath $_.FullName});$text=$lines -join "`n";$events=@($lines|ForEach-Object {$_|ConvertFrom-Json})
   $expected=switch($mode){'ui'{'ApplicationUnhandled'} 'background'{'BackgroundUnhandled'} 'unobserved'{'UnobservedTask'}}
   Check "$case typed-fault-recorded" (@($events|Where-Object Code -eq $expected).Count -eq 1)
   Check "$case logs-omit-exception-content" (!$text.Contains('PrivateCrashSecret') -and !$text.Contains($data))
   $capturedError=[IO.File]::ReadAllText($errorFile)
   Check "$case stderr-omits-exception-content" ($capturedError -notmatch 'PrivateCrashSecret')
   Check "$case stdout-omits-exception-content" ([IO.File]::ReadAllText($output) -notmatch 'PrivateCrashSecret')
   $cases.Add(@{mode=$case;elapsedMilliseconds=$elapsed;exitCode=$process.ExitCode;observedOwnedProcesses=$held.Count;passed=$true})
   Write-Output "$case passed"
  } finally {
   foreach($p in $held){$p.Dispose()}
   if($process){if(!$process.HasExited){$process.Kill($true);$process.WaitForExit()};$process.Dispose();$process=$null}
  }
 }
 @{capturedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');checks=$checks;cases=$cases;scope='Real App handlers in probe executable; isolated empty caches and synthetic secret exceptions. Two anonymous owned browsers. No credentials entered. Excludes native fail-fast/OS dumps.'}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $root 'docs/validation/managed-faults.json') -Encoding utf8
 Write-Output "Managed faults: $($checks.Count) checks passed."
} catch {
 @{error=$_.Exception.ToString();checks=$checks;cases=$cases;taskDirectory=$taskRoot;mode=$case}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $root 'docs/validation/managed-faults-failure.json') -Encoding utf8
 throw
} finally {[DesktopAudit]::Move($cursor[0],$cursor[1])}
