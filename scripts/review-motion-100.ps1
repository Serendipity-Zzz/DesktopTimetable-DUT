$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$report=Get-Content (Join-Path $root 'docs/validation/motion-100-first-run.json') -Raw|ConvertFrom-Json
$desktop=Get-Content (Join-Path $root 'docs/validation/desktop-motion-100-first-run.json') -Raw|ConvertFrom-Json
$dataPath=[IO.Path]::GetFullPath($desktop.dataDirectory)
$artifactRoot=[IO.Path]::GetFullPath((Join-Path $root 'artifacts'))+[IO.Path]::DirectorySeparatorChar
if(!$dataPath.StartsWith($artifactRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Motion evidence directory outside workspace artifacts'}
$settings=Get-Content (Join-Path $dataPath 'settings.json') -Raw|ConvertFrom-Json
$originalFailures=@($report.checks|Where-Object {!$_.passed})
if(@($originalFailures|Where-Object {$_.name -ne 'reduced-motion-and-stable-target-persisted'}).Count -ne 0){throw 'Native behavior checks failed'}
$samples=@($report.samples)
$first=$samples|Where-Object {$_.phase -eq 'before-motion-cycles'}|Select-Object -First 1
$last=$samples|Where-Object {$_.phase -eq 'after-motion-idle'}|Select-Object -Last 1
$checks=[ordered]@{
 exactly100Cycles=($report.cycles -eq 100)
 all200FullTransitions=($report.normalDurationsMs.Count -eq 200)
 fullMotionWithinBudget=(@($report.normalDurationsMs|Where-Object {$_ -lt 320 -or $_ -gt 420}).Count -eq 0)
 reducedMotionWithinBudget=($report.reducedDurationsMs.Count -eq 2 -and @($report.reducedDurationsMs|Where-Object {$_ -gt 120}).Count -eq 0)
 realSavedReducedMotionAndLockedState=($settings.reducedMotion -eq $true -and $settings.state -eq 0)
 cleanExit=($report.exitCode -eq 0)
 observedNativeChecksPassed=(@($report.checks|Where-Object {!$_.passed -and $_.name -ne 'reduced-motion-and-stable-target-persisted'}).Count -eq 0)
 gdiObjectsStable=($last.gdi -le $first.gdi)
 userObjectsWithinTwo=($last.user -le $first.user+2)
 handlesWithinSixteen=($last.handles -le $first.handles+16)
 noPrivateBytesGrowthAfterIdle=($last.privateBytes -le $first.privateBytes)
}
$review=@{
 capturedAtUtc=[DateTimeOffset]::UtcNow.ToString('O')
 sourceReport='motion-100-first-run.json'
 correction='Original harness expected the string Locked; actual SettingsStore serializes TimetableState numerically. Rechecked the original saved settings: state=0 and reducedMotion=true. Original failure and timing/resource records retained without modification.'
 checks=$checks
 minimumMilliseconds=($report.normalDurationsMs|Measure-Object -Minimum).Minimum
 maximumMilliseconds=($report.normalDurationsMs|Measure-Object -Maximum).Maximum
 before=$first
 after=$last
 note='100 cycles and short recovery only; not formal P95/GPU/24-hour budget acceptance.'
}
$review|ConvertTo-Json -Depth 7|Set-Content (Join-Path $root 'docs/validation/motion-100-review.json') -Encoding utf8
if(@($checks.Values|Where-Object {!$_}).Count -ne 0){throw '100-cycle evidence review failed'}
Write-Output '100-cycle original evidence reviewed: native behavior, timings, numeric saved state, object counts and short idle recovery passed.'
