param([ValidateSet('Build','Core')][string]$Stage = 'Core')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $root '.tools/dotnet/dotnet.exe'
if (!(Test-Path $dotnet)) { $dotnet = 'dotnet' }
Push-Location $root
try {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    & $dotnet restore DlutTimetable.slnx --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
    if ($Stage -eq 'Build') {
        & $dotnet build DlutTimetable.slnx -c Release --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    } else {
        # Build the contract suites sequentially: their shared project outputs cannot
        # be compiled concurrently, and the live desktop may hold its own DLLs open.
        foreach ($suite in @('Domain.Tests','Application.Tests','Infrastructure.Tests')) {
            & $dotnet test "tests/$suite/$suite.csproj" -c Release --no-restore --logger trx
            if ($LASTEXITCODE -ne 0) { throw "$suite failed" }
        }
    }
} finally { Pop-Location }
