param([string]$SessionFile='docs/validation/current-endurance-session.json')
# Read-only status; a live process with stale samples is not considered terminal.
$ErrorActionPreference='Stop'
$record=Get-Content -LiteralPath $SessionFile -Raw|ConvertFrom-Json
function Identity([int]$ProcessId,$ExpectedStarted){
 $found=Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
 if(!$found){return 'Missing'}
 try{
  # ConvertFrom-Json can return either a string or DateTime depending on the
  # installed PowerShell version; compare UTC ticks, never formatted strings.
  if($found.StartTime.ToUniversalTime().Ticks -ne ([datetimeoffset]$ExpectedStarted).UtcTicks){return 'DifferentProcess'}
  return 'Matching'
 }finally{$found.Dispose()}
}
$rootIdentity=Identity $record.rootPid $record.rootStartedUtc
$observerIdentity=Identity $record.samplerPid $record.samplerStartedUtc
$sampleFile=Join-Path $record.measurementDirectory 'samples.jsonl'
$summaryFile=Join-Path $record.measurementDirectory 'summary.json'
$last=$null;$sampleReadable=$true
if(Test-Path -LiteralPath $sampleFile){
 try{$last=Get-Content -LiteralPath $sampleFile -Tail 1|ConvertFrom-Json}
 catch{$sampleReadable=$false}
}
$age=if($last){([datetimeoffset]::UtcNow-([datetimeoffset]$last.utc)).TotalSeconds}else{$null}
$summary=if(Test-Path -LiteralPath $summaryFile){Get-Content -LiteralPath $summaryFile -Raw|ConvertFrom-Json}else{$null}
$metadataFile=Join-Path $record.measurementDirectory 'metadata.json'
$metadata=if(Test-Path -LiteralPath $metadataFile){Get-Content -LiteralPath $metadataFile -Raw|ConvertFrom-Json}else{$null}
$configurationMatches=$metadata -and $metadata.rootPid -eq $record.rootPid -and ([datetimeoffset]$metadata.rootStartedUtc).UtcTicks -eq ([datetimeoffset]$record.rootStartedUtc).UtcTicks -and $metadata.warmupSeconds -eq $record.plannedWarmupSeconds -and $metadata.sampleSeconds -eq $record.plannedSteadySeconds -and $metadata.intervalSeconds -eq $record.intervalSeconds
$summaryIdentity=$summary -and $summary.rootPid -eq $record.rootPid -and ([datetimeoffset]$summary.rootStartedUtc).UtcTicks -eq ([datetimeoffset]$record.rootStartedUtc).UtcTicks
$completed=$configurationMatches -and $summaryIdentity -and $summary.completedRequestedDuration -and $summary.endReason -eq 'Completed' -and $summary.steadyWindowSeconds -ge $record.plannedSteadySeconds -and $summary.elapsedSeconds -ge ($record.plannedWarmupSeconds+$record.plannedSteadySeconds)
$status=if($observerIdentity -eq 'Matching'){
 if($summary){'ObserverFinalizing'}
 elseif($rootIdentity -ne 'Matching'){'RootEndedObserverFinalizing'}
 elseif(!$sampleReadable){'ObserverAliveSampleReadRetryRequired'}
 elseif($null -eq $age -or $age -gt [Math]::Max(120,2*$record.intervalSeconds+15)){'ObserverAliveSamplesStale'}
 else{'Running'}
}elseif($completed){'CompletedObservation'}
elseif($observerIdentity -eq 'DifferentProcess'){'ObserverIdentityChanged'}
elseif($summary){'ObservationIncomplete'}else{'ObserverStoppedWithoutSummary'}
[ordered]@{checkedAtUtc=[datetimeoffset]::UtcNow.ToString('O');version=$record.version;status=$status;
 rootPid=$record.rootPid;rootIdentity=$rootIdentity;observerPid=$record.samplerPid;observerIdentity=$observerIdentity;configurationMatches=[bool]$configurationMatches;
 lastSampleUtc=if($last){([datetimeoffset]$last.utc).ToUniversalTime().ToString('O')}else{$null};sampleAgeSeconds=$age;
 elapsedSampleSeconds=if($last){$last.elapsedSeconds}else{$null};phase=if($last){$last.phase}else{$null};
 completedRequestedObservation=[bool]$completed;productBudgetPassed=$null;
 note='Observation completion does not establish budget, canonical hardware, controlled interaction cycles or exact DWM/GPU attribution.'}|ConvertTo-Json -Depth 4
