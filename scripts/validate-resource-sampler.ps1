$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$fixture=Join-Path $root 'artifacts/resource-fixture-probe/ResourceFixture.Spike.dll'
$dotnet=Join-Path $root '.tools/dotnet/dotnet.exe'
$output=Join-Path $root ('artifacts/resource-sampler-test-'+[Guid]::NewGuid().ToString('N'))
$checks=[Collections.Generic.List[object]]::new()
function Check([string]$name,[bool]$pass){$checks.Add(@{name=$name;passed=$pass});if(!$pass){throw ('Resource sampler validation failed: '+$name)}}
function Start-Probe([string]$mode){
 $start=[Diagnostics.ProcessStartInfo]::new($dotnet);$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true
 $start.ArgumentList.Add($fixture);$start.ArgumentList.Add($mode)
 $p=[Diagnostics.Process]::Start($start);$null=$p.Handle;return $p
}
$parent=Start-Probe '--tree-parent';$sentinel=Start-Probe '--tree-leaf';$completed=$false
try{
 $ready=$parent.StandardOutput.ReadLineAsync();Check 'controlled-parent-ready' ($ready.Wait(20000) -and $ready.Result -match '^Ready:\d+$')
 $childId=[int]($ready.Result.Split(':')[1])
 $readySentinel=$sentinel.StandardOutput.ReadLineAsync();Check 'unrelated-same-name-process-ready' ($readySentinel.Wait(20000) -and $readySentinel.Result -eq 'Ready')
 $rejected=$false
 try{& (Join-Path $PSScriptRoot 'measure-resource-budget.ps1') -RootProcessId $parent.Id -ExpectedProcessName 'wrong-owned-process-name' -WarmupSeconds 0 -SampleSeconds 1 -OutputDirectory $output}catch{$rejected=$true}
 Check 'wrong-identity-rejected-before-output' ($rejected -and !(Test-Path -LiteralPath $output))
 & (Join-Path $PSScriptRoot 'measure-resource-budget.ps1') -RootProcessId $parent.Id -ExpectedProcessName dotnet -WarmupSeconds 0 -SampleSeconds 6 -IntervalSeconds 1 -OutputDirectory $output
 $summary=Get-Content (Join-Path $output 'summary.json') -Raw|ConvertFrom-Json
 $samples=@(Get-Content (Join-Path $output 'samples.jsonl')|ForEach-Object {$_|ConvertFrom-Json})
 $identities=@($summary.observedProcessIdentities)
 Check 'controlled-parent-and-child-included' ($identities.Count -ge 2 -and $identities.pid -contains $parent.Id -and $identities.pid -contains $childId)
 # Windows may also create owned console hosts. Validate each extra descendant
 # independently with CIM rather than discarding real process-tree members.
 $invalid=@(foreach($entry in $identities){if($entry.pid -ne $parent.Id){
  $actual=Get-CimInstance Win32_Process -Filter "ProcessId=$($entry.pid)" -Property ParentProcessId,Name
  if(!$actual -or $identities.pid -notcontains $actual.ParentProcessId -or $actual.Name -notin @('dotnet.exe','conhost.exe')){$entry}
 }})
 Check 'all-extra-members-are-real-controlled-descendants' ($invalid.Count -eq 0)
 Check 'unrelated-same-name-process-excluded' ($identities.pid -notcontains $sentinel.Id)
 Check 'requested-full-window-measured' ($summary.completedRequestedDuration -and $summary.steadyWindowSeconds -ge 6 -and $summary.endReason -eq 'Completed')
 Check 'native-memory-counters-valid' (@($samples|ForEach-Object {$_.processes}|Where-Object {$_.privateBytes -le 0 -or $_.privateWorkingSetBytes -le 0 -or $_.privateWorkingSetBytes -gt $_.workingSetBytes}).Count -eq 0 -and $summary.readFailures -eq 0)
 $badSum=@($samples|Where-Object {$_.totals.privateBytes -ne ($_.processes|Measure-Object privateBytes -Sum).Sum})
 Check 'tree-private-commit-sum-matches-observed-members' ($badSum.Count -eq 0)
 $missing=@($samples|ForEach-Object {$_.processes}|Where-Object {$_.gpuStatus -ne 'available'})
 Check 'missing-gpu-never-becomes-zero' (@($missing|Where-Object {$null -ne $_.gpuDedicatedBytes -or $null -ne $_.gpuSharedBytes}).Count -eq 0)
 Check 'sampling-does-not-stop-processes' (!$parent.HasExited -and !$sentinel.HasExited)
 Check 'controlled-parent-exits-normally' ($parent.WaitForExit(20000) -and $parent.ExitCode -eq 0)
 Check 'unrelated-process-exits-normally' ($sentinel.WaitForExit(20000) -and $sentinel.ExitCode -eq 0)
 $completed=$true
 Write-Output ('Resource sampler: '+$checks.Count+' controlled checks passed.')
}finally{
 @{capturedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');completed=$completed;checks=$checks;measurementDirectory=$output;scope='Synthetic controlled process tree and unrelated same-name sentinel, with native counters. Console hosts are independently verified with CIM. No school/browser or production app fault injection.'}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $root 'docs/validation/resource-sampler-validation.json') -Encoding utf8NoBOM
 $parent.Dispose();$sentinel.Dispose()
}
