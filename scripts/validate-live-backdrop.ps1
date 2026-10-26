param([switch]$NoBuild)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$dotnet=Join-Path $root '.tools/dotnet/dotnet.exe'
Push-Location $root
try {
 if(!$NoBuild){
  & $dotnet build spikes/LiveBackdrop.Spike/LiveBackdrop.Spike.csproj -c Release -v minimal
  if($LASTEXITCODE -ne 0){throw 'Live backdrop probe build failed'}
 }
 $output=Join-Path $root ('artifacts/live-backdrop-'+[Guid]::NewGuid().ToString('N'))
 & $dotnet spikes/LiveBackdrop.Spike/bin/Release/net10.0-windows10.0.19041.0/LiveBackdrop.Spike.dll $output --product
 if($LASTEXITCODE -ne 0 -or (Test-Path (Join-Path $output 'failure.txt'))){throw "Live backdrop probe failed: $output"}
 $result=Get-Content (Join-Path $output 'product-result.json') -Raw | ConvertFrom-Json
 if(@($result.checks.PSObject.Properties | Where-Object {!$_.Value}).Count -gt 0){throw 'Live backdrop acceptance failed'}
 Copy-Item -LiteralPath (Join-Path $output 'product-result.json') -Destination docs/validation/live-backdrop.json
 Copy-Item -LiteralPath (Join-Path $output 'product-0.png') -Destination docs/validation/live-backdrop-red.png
 Copy-Item -LiteralPath (Join-Path $output 'product-1.png') -Destination docs/validation/live-backdrop-blue.png
 Copy-Item -LiteralPath (Join-Path $output 'product-0.png') -Destination docs/validation/desktop-timetable.png
 Write-Output 'Live backdrop: dynamic background, blur boundary, stable foreground, no activation and clean exit passed.'
} finally {Pop-Location}
