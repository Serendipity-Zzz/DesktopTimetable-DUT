param([switch]$RestartExplorer)
$ErrorActionPreference = 'Stop'
if (!$RestartExplorer) { throw 'This check restarts the current-session Windows shell. Supply -RestartExplorer only after saving your Explorer work.' }
$root = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $root '.tools/dotnet/dotnet.exe'
$previousApproval = $env:DLUT_ALLOW_EXPLORER_RESTART
Push-Location $root
try {
    & $dotnet build spikes/NativeHost.Spike/NativeHost.Spike.csproj -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Native spike build failed' }
    $env:DLUT_ALLOW_EXPLORER_RESTART = '1'
    & $dotnet spikes/NativeHost.Spike/bin/Release/net10.0-windows/NativeHost.Spike.dll --restart-explorer docs/validation/G2-explorer-recovery.json
    if ($LASTEXITCODE -ne 0) { throw 'Explorer recovery check failed; inspect report' }
} finally {
    $env:DLUT_ALLOW_EXPLORER_RESTART = $previousApproval
    Pop-Location
}
