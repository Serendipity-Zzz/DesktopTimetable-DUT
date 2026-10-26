$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $root '.tools/dotnet/dotnet.exe'
Push-Location $root
try {
    & $dotnet build spikes/NativeHost.Spike/NativeHost.Spike.csproj -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Native spike build failed' }
    # Uses real mouse events only inside verified probe HWNDs and briefly toggles Win+D twice.
    & $dotnet spikes/NativeHost.Spike/bin/Release/net10.0-windows/NativeHost.Spike.dll --audit docs/validation/G2-native-audit.json
    if ($LASTEXITCODE -ne 0) { throw 'Native audit failed; inspect G2-native-audit.json' }
} finally { Pop-Location }
