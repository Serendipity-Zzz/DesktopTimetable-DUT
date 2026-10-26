param([switch]$Demo,[switch]$Workbench,[string]$DataDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $root '.tools/dotnet/dotnet.exe'
if (!(Test-Path $dotnet)) { $dotnet = 'dotnet' }
$runArgs = @('run','--project',(Join-Path $root 'src/DlutTimetable.Desktop'),'-c','Release','--no-build','--')
if ($Demo) {
    $runArgs += '--desktop-demo'
    if (!$DataDirectory) { $DataDirectory = Join-Path $root 'artifacts/demo' }
}
if ($Workbench) { $runArgs += '--workbench' }
if ($DataDirectory) { $runArgs += @('--data-directory',[IO.Path]::GetFullPath($DataDirectory)) }
& $dotnet @runArgs
if ($LASTEXITCODE -ne 0) { throw 'Application failed to start. See docs/BUILD.md.' }
