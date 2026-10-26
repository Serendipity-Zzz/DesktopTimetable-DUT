param([string]$Version='0.26.0-dev',[string]$RuntimeVersion='10.0.13',
 [string]$SigningCertificateThumbprint,[string]$TimestampServer)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if($Version -notmatch '^\d+\.\d+\.\d+(?:-[a-z0-9.-]+)?$' -or $RuntimeVersion -notmatch '^\d+\.\d+\.\d+$'){throw 'Invalid publish version'}
$signingCertificate=$null
if($SigningCertificateThumbprint){
 if($SigningCertificateThumbprint -notmatch '^[a-fA-F0-9]{40,64}$'){throw 'Invalid certificate thumbprint'}
 $signingCertificate=Get-Item -LiteralPath ('Cert:\CurrentUser\My\'+$SigningCertificateThumbprint)
 $codeSigningUsage=@($signingCertificate.Extensions|Where-Object {$_.Oid.Value -eq '2.5.29.37'}|ForEach-Object {$_.EnhancedKeyUsages}|Where-Object {$_.Value -eq '1.3.6.1.5.5.7.3.3'})
 if(!$signingCertificate.HasPrivateKey -or !$codeSigningUsage.Count){throw 'A private code-signing certificate is required'}
}elseif($Version -notmatch '^\d+\.\d+\.\d+-(?:dev|preview)(?:[.-][a-z0-9.-]+)?$'){
 throw 'Stable releases require a publisher signing certificate'
}
if($TimestampServer){
 $timestampUri=$null
 if(!$signingCertificate -or ![uri]::TryCreate($TimestampServer,[UriKind]::Absolute,[ref]$timestampUri) -or $timestampUri.Scheme -ne 'https'){throw 'Timestamp server requires signing and an absolute HTTPS URI'}
}
$dotnet=Join-Path $root '.tools/dotnet/dotnet.exe'
if(!(Test-Path -LiteralPath $dotnet)){throw 'Run scripts/bootstrap.ps1 to install the pinned SDK first'}
$stamp=[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
$release=Join-Path $root ('artifacts/releases/DlutTimetable-'+$Version+'-win-x64-'+$stamp)
$worker=Join-Path $root ('artifacts/release-staging/worker-'+$stamp)
New-Item -ItemType Directory -Path $release,$worker -Force|Out-Null
$common=@('-c','Release','-r','win-x64','--self-contained','true','-p:PublishTrimmed=false','-p:PublishSingleFile=false','-p:TargetLatestRuntimePatch=true',('-p:Version='+$Version),'-v','minimal')
Push-Location $root
try {
$sdkVersion=(Get-Content (Join-Path $root 'global.json') -Raw|ConvertFrom-Json).sdk.version
if((& $dotnet --version) -ne $sdkVersion){throw 'Publish requires the repository-pinned SDK'}
foreach($item in @(
 @{project='src/DlutTimetable.Desktop/DlutTimetable.Desktop.csproj';output=$release},
 @{project='src/DlutTimetable.AuthWorker/DlutTimetable.AuthWorker.csproj';output=$worker}
)){
 & $dotnet publish (Join-Path $root $item.project) @common '-o' $item.output
 if($LASTEXITCODE -ne 0){throw ('Publish failed: '+$item.project)}
}
} finally {Pop-Location}
# Flatten identical runtime files only. A differing same-name file is a build
# error, never an implicit overwrite of one application's dependencies.
foreach($file in Get-ChildItem -LiteralPath $worker -File -Recurse){
 $relative=[IO.Path]::GetRelativePath($worker,$file.FullName)
 $destination=Join-Path $release $relative
 if(Test-Path -LiteralPath $destination){
  if((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $destination).Hash){throw ('Conflicting published dependency: '+$relative)}
 }else{
  New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force|Out-Null
  Copy-Item -LiteralPath $file.FullName -Destination $destination
 }
}
foreach($name in @('DlutTimetable.Desktop.exe','DlutTimetable.AuthWorker.exe','hostfxr.dll','hostpolicy.dll','coreclr.dll','e_sqlite3.dll')){
 if(!(Get-ChildItem -LiteralPath $release -File -Recurse|Where-Object Name -eq $name)){throw ('Missing runtime dependency: '+$name)}
}
foreach($name in @('DlutTimetable.Desktop.runtimeconfig.json','DlutTimetable.AuthWorker.runtimeconfig.json')){
 $config=Get-Content (Join-Path $release $name) -Raw|ConvertFrom-Json
 if(!$config.runtimeOptions.includedFrameworks -or $config.runtimeOptions.framework -or $config.runtimeOptions.frameworks){throw 'Publish output is not self-contained'}
 if($config.runtimeOptions.includedFrameworks|Where-Object version -ne $RuntimeVersion){throw 'Bundled runtime does not match the pinned expected patch'}
}
$licenses=Join-Path $release 'licenses';New-Item -ItemType Directory -Path $licenses|Out-Null
Copy-Item -LiteralPath (Join-Path $root 'docs/licenses/WebView2-LICENSE.txt') -Destination $licenses
foreach($notice in @('Vortice-LICENSE.txt','Vortice-Mathematics-LICENSE.txt','SharpGen-LICENSE.txt')){
 Copy-Item -LiteralPath (Join-Path $root ('docs/licenses/'+$notice)) -Destination $licenses
}
Copy-Item -LiteralPath (Join-Path $root 'docs/dependencies.json') -Destination $licenses
# NuGet packages include the exact runtime license/notices shipped with this patch.
$assets=Get-Content (Join-Path $root 'src/DlutTimetable.Desktop/obj/project.assets.json') -Raw|ConvertFrom-Json
$packageRoots=@($assets.packageFolders.PSObject.Properties.Name)
foreach($package in @('microsoft.netcore.app.runtime.win-x64','microsoft.windowsdesktop.app.runtime.win-x64')){
 $folder=$packageRoots|ForEach-Object {Join-Path $_ ($package+'/'+$RuntimeVersion)}|Where-Object {Test-Path -LiteralPath $_}|Select-Object -First 1
 if(!$folder){throw ('Runtime package provenance missing: '+$package)}
 $target=Join-Path $licenses $package;New-Item -ItemType Directory -Path $target|Out-Null
 $texts=Get-ChildItem -LiteralPath $folder -File|Where-Object Name -match 'LICENSE|NOTICES'
 if(!$texts){throw ('Runtime license missing: '+$package)}
 foreach($text in $texts){Copy-Item -LiteralPath $text.FullName -Destination $target}
}
Copy-Item -LiteralPath (Join-Path $root 'docs/PORTABLE.md') -Destination (Join-Path $release '使用说明.md')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install.cmd') -Destination $release
if($signingCertificate){
 foreach($name in @('DlutTimetable.Desktop.exe','DlutTimetable.AuthWorker.exe')){
  $options=@{FilePath=(Join-Path $release $name);Certificate=$signingCertificate;HashAlgorithm='SHA256'}
  if($TimestampServer){$options.TimestampServer=$TimestampServer}
  $signature=Set-AuthenticodeSignature @options
  if($signature.Status -ne 'Valid'){throw 'Executable signing did not produce a trusted signature'}
 }
}
$files=@(Get-ChildItem -LiteralPath $release -File -Recurse|Sort-Object FullName|ForEach-Object {
 [ordered]@{path=[IO.Path]::GetRelativePath($release,$_.FullName).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
})
$manifest=[ordered]@{product='DLUT Desktop Timetable';version=$Version;rid='win-x64';selfContained=$true;runtimeVersion=$RuntimeVersion;createdUtc=[DateTimeOffset]::UtcNow.ToString('O');entryPoint='DlutTimetable.Desktop.exe';worker='DlutTimetable.AuthWorker.exe';webView2Runtime='Required only for manual school refresh; not bundled';signedRelease=[bool]$signingCertificate;files=$files}
$manifest|ConvertTo-Json -Depth 6|Set-Content (Join-Path $release 'release-manifest.json') -Encoding utf8NoBOM
if($signingCertificate){
 Add-Type -AssemblyName System.Security.Cryptography.Pkcs
 $content=[Security.Cryptography.Pkcs.ContentInfo]::new([IO.File]::ReadAllBytes((Join-Path $release 'release-manifest.json')))
 $cms=[Security.Cryptography.Pkcs.SignedCms]::new($content,$true)
 $signer=[Security.Cryptography.Pkcs.CmsSigner]::new($signingCertificate)
 $signer.DigestAlgorithm=[Security.Cryptography.Oid]::new('2.16.840.1.101.3.4.2.1')
 $signer.IncludeOption=[Security.Cryptography.X509Certificates.X509IncludeOption]::EndCertOnly
 $cms.ComputeSignature($signer,$true)
 $encoded=$cms.Encode();if($encoded.Length -gt 65536){throw 'Manifest signature exceeds its bounded size'}
 [IO.File]::WriteAllBytes((Join-Path $release 'release-manifest.p7s'),$encoded)
}
& (Join-Path $PSScriptRoot 'verify-release.ps1') -Directory $release|Out-Null
$zip=$release+'.zip';Compress-Archive -LiteralPath $release -DestinationPath $zip -CompressionLevel Optimal
$hash=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
($hash+'  '+[IO.Path]::GetFileName($zip))|Set-Content ($zip+'.sha256') -Encoding ascii
$totalBytes=0L;foreach($item in $files){$totalBytes+=[long]$item['bytes']}
$validationPath=Join-Path $root 'docs/validation';New-Item -ItemType Directory -Path $validationPath -Force|Out-Null
[ordered]@{directory=$release;archive=$zip;sha256=$hash;version=$Version;runtimeVersion=$RuntimeVersion;fileCount=$files.Count;unpackedBytes=$totalBytes}|ConvertTo-Json|Set-Content (Join-Path $validationPath 'current-release.json') -Encoding utf8NoBOM
Get-Content (Join-Path $root 'docs/validation/current-release.json')
