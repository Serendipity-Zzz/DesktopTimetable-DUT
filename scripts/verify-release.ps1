param([Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference='Stop'
$directoryPath=[IO.Path]::GetFullPath($Directory).TrimEnd([IO.Path]::DirectorySeparatorChar)
if((Get-Item -LiteralPath $directoryPath).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Release root cannot be a reparse point'}
$manifest=Get-Content (Join-Path $directoryPath 'release-manifest.json') -Raw|ConvertFrom-Json
if($manifest.signedRelease -isnot [bool]){throw 'Invalid signing declaration'}
if(!$manifest.signedRelease -and $manifest.version -notmatch '^\d+\.\d+\.\d+-(?:dev|preview)(?:[.-][a-z0-9.-]+)?$'){throw 'Unsigned stable release is not permitted'}
if(!$manifest.selfContained -or $manifest.rid -ne 'win-x64' -or $manifest.entryPoint -ne 'DlutTimetable.Desktop.exe' -or $manifest.worker -ne 'DlutTimetable.AuthWorker.exe' -or !$manifest.files){throw 'Invalid release manifest'}
$expected=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$total=0L
foreach($item in $manifest.files){
 if($item.path -eq 'release-manifest.p7s'){throw 'Manifest signature cannot include itself'}
 $parts=$item.path.Split('/')
 if(!$item.path -or [IO.Path]::IsPathRooted($item.path) -or $item.path.Contains(':') -or $item.path.Contains('\') -or ($parts|Where-Object {$_ -in @('','..','.')}) -or !$expected.Add($item.path)){throw 'Unsafe or duplicate manifest path'}
 $file=Join-Path $directoryPath $item.path
 if(!(Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -ne $item.bytes -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $item.sha256){throw ('Release file verification failed: '+$item.path)}
 $total+=[long]$item.bytes
}
foreach($required in @('DlutTimetable.Desktop.exe','DlutTimetable.AuthWorker.exe','DlutTimetable.Desktop.runtimeconfig.json','DlutTimetable.AuthWorker.runtimeconfig.json','hostpolicy.dll','hostfxr.dll','coreclr.dll')){
 if(!$expected.Contains($required)){throw ('Release manifest omits required component: '+$required)}
}
foreach($entry in Get-ChildItem -LiteralPath $directoryPath -Recurse -Force){
 if($entry.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Release cannot contain reparse points'}
 if(!$entry.PSIsContainer){$relative=[IO.Path]::GetRelativePath($directoryPath,$entry.FullName).Replace('\','/');if($relative -ne 'release-manifest.json' -and !($manifest.signedRelease -and $relative -eq 'release-manifest.p7s') -and !$expected.Contains($relative)){throw ('Unlisted release file: '+$relative)}}
}
if($manifest.signedRelease){
 Add-Type -AssemblyName System.Security.Cryptography.Pkcs
 $signaturePath=Join-Path $directoryPath 'release-manifest.p7s'
 if(!(Test-Path -LiteralPath $signaturePath)){throw 'Missing manifest signature'}
 $signatureSize=(Get-Item -LiteralPath $signaturePath).Length
 if($signatureSize -lt 1 -or $signatureSize -gt 65536){throw 'Invalid manifest signature size'}
 $raw=[IO.File]::ReadAllBytes((Join-Path $directoryPath 'release-manifest.json'))
 $cms=[Security.Cryptography.Pkcs.SignedCms]::new([Security.Cryptography.Pkcs.ContentInfo]::new($raw),$true)
 $cms.Decode([IO.File]::ReadAllBytes($signaturePath))
 if(!$cms.Detached -or $cms.SignerInfos.Count -ne 1 -or ![Security.Cryptography.CryptographicOperations]::FixedTimeEquals($cms.ContentInfo.Content,$raw)){throw 'Manifest signature does not bind this manifest'}
 $signer=$cms.SignerInfos[0]
 if($signer.DigestAlgorithm.Value -notin @('2.16.840.1.101.3.4.2.1','2.16.840.1.101.3.4.2.2','2.16.840.1.101.3.4.2.3')){throw 'Unsupported manifest digest algorithm'}
 $cms.CheckSignature($true)
 $chain=[Security.Cryptography.X509Certificates.X509Chain]::new()
 try{
  $chain.ChainPolicy.RevocationMode=[Security.Cryptography.X509Certificates.X509RevocationMode]::Offline
  $chain.ChainPolicy.DisableCertificateDownloads=$true
  $chain.ChainPolicy.ApplicationPolicy.Add([Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3'))
  if(!$chain.Build($signer.Certificate)){throw 'Manifest signer chain is not trusted'}
 }finally{$chain.Dispose()}
 $publisher=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($signer.Certificate.PublicKey.ExportSubjectPublicKeyInfo()))
 foreach($name in @($manifest.entryPoint,$manifest.worker)){
  $signed=Get-AuthenticodeSignature -LiteralPath (Join-Path $directoryPath $name)
  if($signed.Status -ne 'Valid' -or [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($signed.SignerCertificate.PublicKey.ExportSubjectPublicKeyInfo())) -ne $publisher){throw 'Executable and manifest publishers do not match'}
 }
}
[pscustomobject]@{verified=$true;files=$expected.Count;bytes=$total;version=$manifest.version;runtimeVersion=$manifest.runtimeVersion;signedRelease=$manifest.signedRelease}
