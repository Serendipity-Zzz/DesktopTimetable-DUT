param([string]$SdkVersion = '10.0.402')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$toolsDir = Join-Path $root '.tools'
New-Item -ItemType Directory -Force $toolsDir | Out-Null
$installer = Join-Path $toolsDir 'dotnet-install.ps1'
Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
& $installer -Version $SdkVersion -InstallDir (Join-Path $toolsDir 'dotnet') -NoPath
if ($LASTEXITCODE -ne 0) { throw 'SDK installation failed' }
