param([Parameter(Mandatory=$true)][string]$PlanPath,[Parameter(Mandatory=$true)][int]$ParentProcessId,[Parameter(Mandatory=$true)][long]$ParentStartTicks,[switch]$Quiet)
# Windows PowerShell 5.1 compatible. Runs from a small private temp directory after the UI exits.
# All deletion stays in this shell and uses literal, canonical, checked paths.
$ErrorActionPreference='Stop'
$resultPath=Join-Path $PSScriptRoot 'result.json'
$warnings=New-Object 'Collections.Generic.List[string]'
$correlationId=[guid]::NewGuid().ToString('N');$failureCode='None'
$lease=$null;$session=$null;$profileLease=$null;$sessionHeld=$false;$profileHeld=$false;$success=$false
function Full([string]$path){return [IO.Path]::GetFullPath($path).TrimEnd([IO.Path]::DirectorySeparatorChar)}
function Same([string]$a,[string]$b){return [string]::Equals((Full $a),(Full $b),[StringComparison]::OrdinalIgnoreCase)}
function NoLinks([string]$path){
 $value=[IO.Path]::GetFullPath($path)
 while($value){
  $item=Get-Item -LiteralPath $value -Force -ErrorAction SilentlyContinue
  if($item -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw '目录包含链接，未执行清理。'}
  $value=[IO.Path]::GetDirectoryName($value)
 }
}
function Child([string]$base,[string]$relative){
 if(!$relative -or [IO.Path]::IsPathRooted($relative) -or $relative.Contains(':')){throw '无效清理路径。'}
 foreach($part in $relative.Replace('\','/').Split('/')){
  if(!$part -or $part -eq '.' -or $part -eq '..' -or $part.EndsWith('.') -or $part.EndsWith(' ') -or $part.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0){throw '无效清理路径。'}
 }
 $value=Full ([IO.Path]::Combine($base,$relative))
 if(!$value.StartsWith((Full $base)+'\',[StringComparison]::OrdinalIgnoreCase)){throw '清理路径越界。'}
 NoLinks $value;return $value
}
function Hash([string]$path){
 $stream=[IO.File]::OpenRead($path);$sha=[Security.Cryptography.SHA256]::Create()
 try{return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','')}finally{$stream.Dispose();$sha.Dispose()}
}
function TextHash([string]$value){$sha=[Security.Cryptography.SHA256]::Create();try{return [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($value))).Replace('-','')}finally{$sha.Dispose()}}
function Hold([Threading.Mutex]$mutex){try{return $mutex.WaitOne(0)}catch [Threading.AbandonedMutexException]{return $true}}
function AssertTree([string]$directory){
 $pending=New-Object 'Collections.Generic.Stack[string]';$pending.Push($directory)
 while($pending.Count -gt 0){
  $next=$pending.Pop();NoLinks $next
  foreach($item in Get-ChildItem -LiteralPath $next -Force){
   if(!$item.FullName.StartsWith($directory+'\',[StringComparison]::OrdinalIgnoreCase)){throw '清理目录越界。'}
   NoLinks $item.FullName;if($item.PSIsContainer){$pending.Push($item.FullName)}
  }
 }
}
function RemoveEmpty([string]$directory){
 NoLinks $directory
 if((Test-Path -LiteralPath $directory -PathType Container) -and !(Get-ChildItem -LiteralPath $directory -Force|Select-Object -First 1)){Remove-Item -LiteralPath $directory -Force}
}
try {
 if(!(Test-Path -LiteralPath $PlanPath -PathType Leaf) -or (Get-Item -LiteralPath $PlanPath).Length -gt 4194304){throw '清理计划无效。'}
 $plan=Get-Content -LiteralPath $PlanPath -Raw -Encoding UTF8|ConvertFrom-Json
 if($plan.schemaVersion -ne 1 -or $plan.productId -ne 'DLUTDesktopTimetable' -or $ParentProcessId -le 0){throw '清理计划不受支持。'}
 $application=Full $plan.installDirectory;$data=Full $plan.dataDirectory
 $badRoot=(Same $application ([IO.Path]::GetPathRoot($application))) -or (Same $data ([IO.Path]::GetPathRoot($data))) -or (Same $application $data)
 $badOverlap=$application.StartsWith($data+'\',[StringComparison]::OrdinalIgnoreCase) -or $data.StartsWith($application+'\',[StringComparison]::OrdinalIgnoreCase)
 if($badRoot -or $badOverlap){throw '程序与数据目录边界异常。'}
 NoLinks $application;NoLinks $data
 $parent=Get-Process -Id $ParentProcessId -ErrorAction SilentlyContinue
 if($parent -and $parent.StartTime.ToUniversalTime().Ticks -eq $ParentStartTicks){if(!$parent.WaitForExit(15000)){throw '卸载窗口尚未退出，未删除文件。'}}
 $lease=New-Object IO.FileStream((Child $application '.install-lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
 $statePath=Child $application 'install-state.json';$ownerPath=Child $application '.dlut-install-owner.json'
 if((Hash $statePath) -ne $plan.stateSha256 -or (Hash $ownerPath) -ne $plan.ownerSha256){throw '安装记录已改变，请重新打开卸载窗口。'}
 $state=Get-Content -LiteralPath $statePath -Raw -Encoding UTF8|ConvertFrom-Json
 if($state.productId -ne $plan.productId -or !(Same $state.dataDirectory $data) -or [bool]$state.userIntegration -ne [bool]$plan.userIntegration){throw '清理配置不一致。'}
 $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
 $session=New-Object Threading.Mutex($false,('Local\DlutTimetable-'+(TextHash ($sid+$data.ToUpperInvariant())).Substring(0,24)))
 $sessionHeld=Hold $session;if(!$sessionHeld){throw '这份课表已重新运行，请先正常退出再卸载。'}
 $executables=@();$prefixes=@()
 foreach($relative in $plan.executables){
  if($relative -notmatch '^versions/[^/]+/DlutTimetable\.Desktop\.exe$'){throw '程序入口列表无效。'}
  $executables+=Child $application $relative;$prefixes+=$relative.Substring(0,$relative.LastIndexOf('/')+1)
 }
 if($executables.Count -eq 0 -or $executables.Count -gt 256 -or $plan.files.Count -gt 131072){throw '清理列表数量无效。'}
 $seen=New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
 AssertTree $application
 foreach($file in $plan.files){
  $path=Child $application $file.path
  if(!$seen.Add($path) -or !($prefixes|Where-Object {$file.path.StartsWith($_,[StringComparison]::Ordinal)}) -or $file.sha256 -notmatch '^[A-Fa-f0-9]{64}$'){throw '清理文件列表无效。'}
  if(Test-Path -LiteralPath $path){if((Get-Item -LiteralPath $path).PSIsContainer -or (Get-Item -LiteralPath $path).Length -ne $file.bytes -or (Hash $path) -ne $file.sha256){throw '程序文件已改变，请重新准备卸载。'}}
 }
 $loginProfile=$null
 if(!$plan.keepData -and $plan.dedicatedProfile){
  $local=[Environment]::GetFolderPath('LocalApplicationData');$appData=Join-Path $local 'DLUTDesktopTimetable'
  if(Same $data $appData){$expected=Join-Path $appData 'auth-profile'}
  else{$expected=Join-Path (Join-Path (Join-Path $appData 'profiles') (TextHash $data.ToUpperInvariant())) 'auth-profile'}
  $loginProfile=Full $plan.dedicatedProfile
  if(!(Same $loginProfile $expected)){throw '专用登录资料目录无效。'}
  NoLinks $loginProfile
  $profileLease=New-Object Threading.Mutex($false,('Local\DlutAuthProfile-'+(TextHash $loginProfile.ToUpperInvariant()).Substring(0,24)))
  $profileHeld=Hold $profileLease;if(!$profileHeld){throw '学校登录资料仍在使用，未清理数据。'}
  if(Test-Path -LiteralPath $loginProfile){AssertTree $loginProfile}
 }
 foreach($warning in @($plan.warnings|Select-Object -First 16)){
  $safe=if($warning -in @('保留未通过校验的旧版本目录。','其它课表副本共用的学校登录状态将保留。')){[string]$warning}else{'部分清理项目无法验证，已保留。'}
  if(!$warnings.Contains($safe)){$warnings.Add($safe)}
 }
 if($plan.userIntegration){
  $local=[Environment]::GetFolderPath('LocalApplicationData')
  if(!(Same $application (Join-Path $local 'Programs\DLUTDesktopTimetable')) -or !(Same $data (Join-Path $local 'DLUTDesktopTimetable'))){throw '系统集成只允许默认用户安装目录。'}
  $runPath='Software\Microsoft\Windows\CurrentVersion\Run';$run=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runPath,$true)
  try{
   if($run){$value=$run.GetValue('DlutDesktopTimetable',$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    $commands=@($executables|ForEach-Object {'"'+$_+'" --background'})
    if($null -ne $value){if($commands -contains $value){$run.DeleteValue('DlutDesktopTimetable',$false)}else{$warnings.Add('启动项已指向其它安装，未修改。')}}
   }
  }finally{if($run){$run.Dispose()}}
 }
 if(!$plan.keepData){
  # This exact set is app-owned. Exports, unknown files, migration sources and other profiles remain.
  foreach($name in @('timetable.db','timetable.db-wal','timetable.db-shm','timetable.db-journal','settings.json')){
   $path=Child $data $name;if(Test-Path -LiteralPath $path){if((Get-Item -LiteralPath $path).PSIsContainer){throw '数据文件被替换成目录，未清理。'};Remove-Item -LiteralPath $path -Force}
  }
  if($loginProfile -and (Test-Path -LiteralPath $loginProfile)){NoLinks $loginProfile;AssertTree $loginProfile;Remove-Item -LiteralPath $loginProfile -Recurse -Force}
 }
 foreach($file in $plan.files){
  $path=Child $application $file.path
  for($attempt=0;$attempt -lt 5 -and (Test-Path -LiteralPath $path);$attempt++){
   try{Remove-Item -LiteralPath $path -Force}catch{if($attempt -eq 4){throw};Start-Sleep -Milliseconds 200}
  }
 }
 if($plan.userIntegration){
  $shortcuts=Join-Path ([Environment]::GetFolderPath('Programs')) '大工课表'
  NoLinks $shortcuts
  $shell=New-Object -ComObject WScript.Shell
  try{
   foreach($name in @('大工课表.lnk','安装与版本恢复.lnk','卸载大工课表.lnk')){
    $path=Child $shortcuts $name
    if(Test-Path -LiteralPath $path){$shortcut=$shell.CreateShortcut($path);try{if($executables -contains $shortcut.TargetPath){Remove-Item -LiteralPath $path -Force}}finally{[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)}}
   }
  }finally{[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)}
  RemoveEmpty $shortcuts
  $uninstallPath='Software\Microsoft\Windows\CurrentVersion\Uninstall\DLUTDesktopTimetable'
  $key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($uninstallPath)
  $ownKey=$false
  try{if($key -and $key.GetValue('InstallLocation')){$ownKey=Same ([string]$key.GetValue('InstallLocation')) $application}}finally{if($key){$key.Dispose()}}
  if($ownKey){[Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($uninstallPath,$false)}
 }
 $versions=Child $application 'versions'
 foreach($directory in @(Get-ChildItem -LiteralPath $versions -Directory -Recurse -Force|Sort-Object {$_.FullName.Length} -Descending)){[void](Child $application ([IO.Path]::GetFullPath($directory.FullName).Substring($application.Length+1)));RemoveEmpty $directory.FullName}
 RemoveEmpty $versions
 Remove-Item -LiteralPath $statePath -Force
 $lease.Dispose();$lease=$null
 $lockPath=Child $application '.install-lock';if(Test-Path -LiteralPath $lockPath){Remove-Item -LiteralPath $lockPath -Force}
 $remaining=@(Get-ChildItem -LiteralPath $application -Force|Where-Object Name -ne '.dlut-install-owner.json')
 if($remaining.Count -eq 0){Remove-Item -LiteralPath $ownerPath -Force;RemoveEmpty $application}
 else{$warnings.Add('发布清单以外的文件或未验证旧目录已保留，程序目录未全部移除。')}
 $success=$true
 $message=if($plan.keepData){'已卸载大工课表。课表、备注、设置和登录状态已保留。'}else{'已卸载大工课表，并删除此安装的课表、备注、设置及专用登录状态。'}
 if($warnings.Count -gt 0){$message+="`n"+($warnings -join "`n")}
}catch{
 $safeReason=switch($_.Exception.Message){
  '无效清理路径。'{$failureCode='CleanupUnsafePath';'清理路径校验失败。'}
  '清理路径越界。'{$failureCode='CleanupUnsafePath';'清理路径校验失败。'}
  '安装记录已改变，请重新打开卸载窗口。'{$failureCode='CleanupStateChanged';'安装记录已改变，请重新打开卸载窗口。'}
  '专用登录资料目录无效。'{$failureCode='CleanupProfileMismatch';'专用登录资料目录无效。'}
  default{$failureCode='CleanupFailed';'校验或文件操作未完成。'}
 }
 $message="卸载未完成（$failureCode · $correlationId）。$safeReason 请保留程序目录，使用完整发布包修复后重试。"
}
finally{
 if($lease){$lease.Dispose()}
 if($profileLease){if($profileHeld){$profileLease.ReleaseMutex()};$profileLease.Dispose()}
 if($session){if($sessionHeld){$session.ReleaseMutex()};$session.Dispose()}
}
[ordered]@{passed=$success;keepData=$plan.keepData;errorCode=$failureCode;correlationId=$correlationId;message=$message;warnings=@($warnings.ToArray());finishedUtc=[DateTimeOffset]::UtcNow.ToString('O')}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $resultPath -Encoding UTF8
if(!$Quiet){Add-Type -AssemblyName PresentationFramework;[void][System.Windows.MessageBox]::Show($message,'大工课表 · 卸载结果')}
if(!$success){exit 1}
