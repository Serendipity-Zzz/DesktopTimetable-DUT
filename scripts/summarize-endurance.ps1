param([string]$SessionFile='docs/validation/current-endurance-session.json',[string]$OutputFile)
# Offline/read-only evaluation. Sampling warmup and the PRD one-hour baseline
# are different concepts; never restart or splice an observation to align them.
$ErrorActionPreference='Stop'
$session=Get-Content -LiteralPath $SessionFile -Raw|ConvertFrom-Json
$directory=$session.measurementDirectory
$metadata=Get-Content -LiteralPath (Join-Path $directory 'metadata.json') -Raw|ConvertFrom-Json
if($metadata.rootPid -ne $session.rootPid -or ([datetimeoffset]$metadata.rootStartedUtc).UtcTicks -ne ([datetimeoffset]$session.rootStartedUtc).UtcTicks){throw 'Observation identity mismatch'}
$rows=[Collections.Generic.List[object]]::new()
$invalid=0;$previous=-1.0;$maximumGap=0.0
foreach($line in Get-Content -LiteralPath (Join-Path $directory 'samples.jsonl')){
 try{$row=$line|ConvertFrom-Json -ErrorAction Stop}catch{$invalid++;continue}
 $elapsed=$row.elapsedSeconds
 $root=@($row.processes|Where-Object {$_.pid -eq $session.rootPid -and ([datetimeoffset]$_.startedUtc).UtcTicks -eq ([datetimeoffset]$session.rootStartedUtc).UtcTicks})
 if($null -eq $elapsed -or ![double]::IsFinite([double]$elapsed) -or $elapsed -le $previous -or $root.Count -ne 1 -or $null -eq $row.totals.privateBytes -or $row.totals.privateBytes -le 0){$invalid++;continue}
 if($previous -ge 0){$maximumGap=[Math]::Max($maximumGap,$elapsed-$previous)}
 $previous=$elapsed;$rows.Add($row)
}
function Median($values){
 $sorted=@($values|Where-Object {$null -ne $_}|Sort-Object)
 if(!$sorted.Count){return $null}
 $middle=[int][Math]::Floor($sorted.Count/2)
 if($sorted.Count%2){return [double]$sorted[$middle]}
 return ([double]$sorted[$middle-1]+[double]$sorted[$middle])/2
}
$last=if($rows.Count){[double]$rows[-1].elapsedSeconds}else{0}
# Two fixed windows avoid treating an isolated first sample as the baseline.
$baseline=@($rows|Where-Object {$_.elapsedSeconds -ge 3600 -and $_.elapsedSeconds -le 3660})
$endpoint=@($rows|Where-Object {$_.elapsedSeconds -ge 86340 -and $_.elapsedSeconds -le 86460})
$required=[Math]::Max(2,[int][Math]::Floor(60/[double]$metadata.intervalSeconds))
$baselineBytes=Median @($baseline|ForEach-Object {$_.totals.privateBytes})
$endpointBytes=Median @($endpoint|ForEach-Object {$_.totals.privateBytes})
$summaryPath=Join-Path $directory 'summary.json'
$summary=if(Test-Path -LiteralPath $summaryPath){Get-Content -LiteralPath $summaryPath -Raw|ConvertFrom-Json}else{$null}
$terminal=$summary -and $summary.rootPid -eq $session.rootPid -and ([datetimeoffset]$summary.rootStartedUtc).UtcTicks -eq ([datetimeoffset]$session.rootStartedUtc).UtcTicks -and $summary.endReason -eq 'Completed' -and $summary.completedRequestedDuration
$coverage=$terminal -and $last -ge 86400 -and $rows.Count -gt 0 -and $rows[0].elapsedSeconds -le [double]$metadata.intervalSeconds+5 -and $invalid -eq 0 -and $summary.readFailures -eq 0 -and $maximumGap -le 2*[double]$metadata.intervalSeconds+5 -and $baseline.Count -ge $required -and $endpoint.Count -ge $required
$growth=if($null -ne $baselineBytes -and $null -ne $endpointBytes){$endpointBytes-$baselineBytes}else{$null}
$fraction=if($baselineBytes -gt 0 -and $null -ne $growth){$growth/$baselineBytes}else{$null}
$resourceChanges=[ordered]@{}
foreach($field in @('handles','threads','gdi','user','gpuDedicatedBytes','gpuSharedBytes')){
 $before=Median @($baseline|ForEach-Object {$_.totals.$field})
 $after=Median @($endpoint|ForEach-Object {$_.totals.$field})
 $resourceChanges[$field]=@{baseline=$before;endpoint=$after;delta=if($null -ne $before -and $null -ne $after){$after-$before}else{$null}}
}
$report=[ordered]@{schemaVersion=1;checkedAtUtc=[datetimeoffset]::UtcNow.ToString('O');version=$session.version;rootPid=$session.rootPid;
 validSamples=$rows.Count;invalidSamples=$invalid;lastElapsedSeconds=$last;maximumGapSeconds=$maximumGap;
 baselineWindowSeconds=@(3600,3660);endpointWindowSeconds=@(86340,86460);minimumWindowSamples=$required;
 baselineSamples=$baseline.Count;endpointSamples=$endpoint.Count;baselinePrivateBytes=$baselineBytes;endpointPrivateBytes=$endpointBytes;
 privateGrowthBytes=$growth;privateGrowthFraction=$fraction;complete24HourCoverage=[bool]$coverage;
 privateGrowthPassed=if($coverage){$growth -le 10MB -and $fraction -le .10}else{$null};
 resourceChanges=$resourceChanges;productBudgetPassed=$null;interactionCyclesVerified=$false;
 note='One-hour baseline and 24-hour endpoint medians from one identity. Private growth is only one requirement; GPU/DWM attribution, hardware and interaction cycles remain separate. Incomplete evidence is null, never passed.'}
$json=$report|ConvertTo-Json -Depth 5
if($OutputFile){$json|Set-Content -LiteralPath $OutputFile -Encoding utf8NoBOM}
$json
