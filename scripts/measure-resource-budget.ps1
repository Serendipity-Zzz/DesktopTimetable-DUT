param(
 [int]$RootProcessId=0,[string]$ExpectedProcessName,
 [string]$SessionFile='docs/validation/current-g1-session.json',
 [ValidateRange(0,3600)][int]$WarmupSeconds=300,
 [ValidateRange(1,86400)][int]$SampleSeconds=600,
 [ValidateRange(1,60)][int]$IntervalSeconds=5,
 [string]$OutputDirectory
)
# External, read-only sampler. No input, UIA, GC, working-set trimming, process
# termination, school requests, credentials, command lines or course content in output.
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if($RootProcessId -eq 0){
 $session=Get-Content -LiteralPath $SessionFile -Raw|ConvertFrom-Json
 $RootProcessId=[int]$session.parentPid;$ExpectedProcessName='DlutTimetable.Desktop'
 $identity=Get-CimInstance Win32_Process -Filter "ProcessId=$RootProcessId" -Property CommandLine
 if(!$identity -or !$identity.CommandLine.Contains($session.dataDirectory)){throw 'Session identity mismatch; sampler did not attach'}
}elseif(!$ExpectedProcessName){throw 'An explicit process ID requires ExpectedProcessName'}
$target=Get-Process -Id $RootProcessId
if($target.ProcessName -cne $ExpectedProcessName){throw 'Root process name mismatch; sampler did not attach'}
$rootStarted=$target.StartTime.ToUniversalTime();$rootSession=$target.SessionId
if(!$OutputDirectory){$OutputDirectory=Join-Path $root ('artifacts/resource-budget-'+[Guid]::NewGuid().ToString('N'))}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $OutputDirectory){throw 'Use a new output directory to preserve prior evidence'}
New-Item -ItemType Directory -Path $OutputDirectory|Out-Null
Add-Type @'
using System;using System.Collections.Generic;using System.ComponentModel;using System.Runtime.InteropServices;
public static class ResourceRead {
 [StructLayout(LayoutKind.Sequential)] public struct Memory {
  public uint cb,PageFaultCount;
  public UIntPtr PeakWorkingSetSize,WorkingSetSize,QuotaPeakPagedPoolUsage,QuotaPagedPoolUsage,
   QuotaPeakNonPagedPoolUsage,QuotaNonPagedPoolUsage,PagefileUsage,PeakPagefileUsage,PrivateUsage,PrivateWorkingSetSize;
  public ulong SharedCommitUsage;
 }
 [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct Entry {
  public uint size,usage,pid;public UIntPtr heap;public uint module,threads,parent;public int priority;public uint flags;
  [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)] public string name;
 }
 public record Node(int Pid,int Parent);
 [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint pid);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool Process32FirstW(IntPtr snapshot,ref Entry entry);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool Process32NextW(IntPtr snapshot,ref Entry entry);
 [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
 [DllImport("psapi.dll",SetLastError=true)] static extern bool GetProcessMemoryInfo(IntPtr process,ref Memory memory,uint size);
 [DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr process,uint flags);
 public static Memory ReadMemory(IntPtr handle) {
  var m=new Memory{cb=(uint)Marshal.SizeOf<Memory>()};
  if(!GetProcessMemoryInfo(handle,ref m,m.cb))throw new Win32Exception(Marshal.GetLastWin32Error());return m;
 }
 public static Node[] Snapshot() {
  var h=CreateToolhelp32Snapshot(2,0);if(h==new IntPtr(-1))throw new Win32Exception(Marshal.GetLastWin32Error());
  try {var result=new List<Node>();var e=new Entry{size=(uint)Marshal.SizeOf<Entry>()};
   if(!Process32FirstW(h,ref e))throw new Win32Exception(Marshal.GetLastWin32Error());
   do{result.Add(new((int)e.pid,(int)e.parent));}while(Process32NextW(h,ref e));return result.ToArray();
  }finally{CloseHandle(h);}
 }
 [StructLayout(LayoutKind.Sequential)] public struct Rect {public int Left,Top,Right,Bottom;}
 delegate bool Callback(IntPtr window,IntPtr data);
 [DllImport("user32.dll")] static extern bool EnumWindows(Callback callback,IntPtr data);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
 [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
 [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window,out Rect rect);
 [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr window);
 [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
 public static object WindowGeometry(int pid) {
  var area=0L;var r=new Rect();uint dpi=0;
  EnumWindows((h,d)=>{GetWindowThreadProcessId(h,out var p);if(p==pid&&IsWindowVisible(h)&&GetWindowRect(h,out var v)) {
   var a=(long)(v.Right-v.Left)*(v.Bottom-v.Top);if(a>area){area=a;r=v;dpi=GetDpiForWindow(h);}}return true;},IntPtr.Zero);
  return new{Width=r.Right-r.Left,Height=r.Bottom-r.Top,Dpi=dpi};
 }
}
'@
$started=[DateTimeOffset]::UtcNow
$clock=[Diagnostics.Stopwatch]::StartNew()
$known=@{};$active=@{};$stats=[Collections.Generic.List[object]]::new()
$writer=[IO.StreamWriter]::new((Join-Path $OutputDirectory 'samples.jsonl'),$false,[Text.UTF8Encoding]::new($false));$writer.AutoFlush=$true
$endReason='Completed';$samplerError=$null;$gpuFailures=0;$readFailures=0;$rootEnded=$false;$maxGap=0.0;$lastElapsed=$null;$steadyStarted=$null
$initialSamplerCpu=(Get-Process -Id $PID).TotalProcessorTime.TotalMilliseconds
$logical=[Environment]::ProcessorCount
$metadata=@{
 schemaVersion=1;startedUtc=$started.ToString('O');rootPid=$RootProcessId;rootStartedUtc=$rootStarted.ToString('O');
 expectedProcessName=$ExpectedProcessName;logicalProcessors=$logical;osVersion=[Environment]::OSVersion.Version.ToString();
 executableVersion=$target.MainModule.FileVersionInfo.ProductVersion;
 executableSha256=(Get-FileHash -LiteralPath $target.MainModule.FileName -Algorithm SHA256).Hash;
 warmupSeconds=$WarmupSeconds;sampleSeconds=$SampleSeconds;intervalSeconds=$IntervalSeconds;
 window=[ResourceRead]::WindowGeometry($RootProcessId);
 primaryWidth=[ResourceRead]::GetSystemMetrics(0);primaryHeight=[ResourceRead]::GetSystemMetrics(1);
 gpuAdapters=@(Get-CimInstance Win32_VideoController -Property Name,DriverVersion|Select-Object Name,DriverVersion);
 scope='Observed live descendants by PID plus creation time. Polling cannot prove peaks or include children that start and exit wholly between samples. Working-set sums may double-count shared pages. DWM counters are context, not application attribution. Missing counters are null, never zero.'
}
$metadata|ConvertTo-Json -Depth 6|Set-Content (Join-Path $OutputDirectory 'metadata.json') -Encoding utf8NoBOM
Write-Output ('Resource sampler attached: '+$RootProcessId+'; output '+$OutputDirectory)
function Read-Owned([Diagnostics.Process]$p,[double]$elapsed){
 $begin=$p.StartTime.ToUniversalTime();$key=([string]$p.Id)+':'+$begin.Ticks
 if(!$known.ContainsKey($key)){
  if($known.Count -ge 4096){throw 'Observed process identity limit exceeded'}
  $role=if($p.Id -eq $RootProcessId){'main'}elseif($p.ProcessName -eq 'DlutTimetable.AuthWorker'){'worker'}elseif($p.ProcessName -eq 'msedgewebview2'){'browser-runtime'}else{'owned-child'}
  $known[$key]=@{pid=$p.Id;startedUtc=$begin.ToString('O');role=$role;firstObservedSeconds=$elapsed;lastObservedSeconds=$elapsed}
 }
 $known[$key].lastObservedSeconds=$elapsed
 $m=[ResourceRead]::ReadMemory($p.Handle);$cpu=$p.TotalProcessorTime.TotalMilliseconds
 $cpuDelta=$null;$faultDelta=$null
 if($active.ContainsKey($key)){
  $cpuDelta=[Math]::Max(0,$cpu-$active[$key].cpu)
  $faultDelta=([long]$m.PageFaultCount-[long]$active[$key].faults+4294967296L)%4294967296L
 }
 $active[$key]=@{cpu=$cpu;faults=$m.PageFaultCount}
 return @{pid=$p.Id;startedUtc=$begin.ToString('O');role=$known[$key].role;
  privateBytes=$m.PrivateUsage.ToUInt64();workingSetBytes=$m.WorkingSetSize.ToUInt64();privateWorkingSetBytes=$m.PrivateWorkingSetSize.ToUInt64();
  cpuMilliseconds=$cpu;cpuDeltaMilliseconds=$cpuDelta;pageFaultCount=$m.PageFaultCount;pageFaultDelta=$faultDelta;
  handles=$p.HandleCount;threads=$p.Threads.Count;gdi=[ResourceRead]::GetGuiResources($p.Handle,0);user=[ResourceRead]::GetGuiResources($p.Handle,1)}
}
try{
 while($true){
  $sampleStart=$clock.Elapsed.TotalSeconds;$phase=if($sampleStart -lt $WarmupSeconds){'warmup'}else{'steady'}
  if($phase -eq 'steady' -and $null -eq $steadyStarted){$steadyStarted=$sampleStart}
  $snapshot=@([ResourceRead]::Snapshot());$owned=@{};$seenThisSample=@{};$processRows=[Collections.Generic.List[object]]::new()
  # Retain already observed descendants after their parent exits, while checking
  # creation time to reject PID reuse. No unrelated processes are adopted.
  foreach($entry in $known.Values){
   try{$p=Get-Process -Id $entry.pid -ErrorAction Stop
    if($p.StartTime.ToUniversalTime().ToString('O') -eq $entry.startedUtc){$owned[$p.Id]=$p}else{$p.Dispose()}
   }catch [Microsoft.PowerShell.Commands.ProcessCommandException]{}
  }
  if(!$owned.ContainsKey($RootProcessId)){
   try{$p=Get-Process -Id $RootProcessId -ErrorAction Stop
    if($p.StartTime.ToUniversalTime() -eq $rootStarted){$owned[$p.Id]=$p}else{$p.Dispose()}
   }catch [Microsoft.PowerShell.Commands.ProcessCommandException]{}
  }
  if(!$owned.ContainsKey($RootProcessId)){$rootEnded=$true;$endReason='RootExited'}
  do{
   $added=$false
   foreach($node in $snapshot){
    if($owned.ContainsKey($node.Pid) -or !$owned.ContainsKey($node.Parent)){continue}
    if($owned.Count -ge 128){throw 'Live process tree limit exceeded'}
    try{$p=Get-Process -Id $node.Pid -ErrorAction Stop
     if($p.StartTime.ToUniversalTime() -ge $owned[$node.Parent].StartTime.ToUniversalTime()){$owned[$p.Id]=$p;$added=$true}else{$p.Dispose()}
    }catch [Microsoft.PowerShell.Commands.ProcessCommandException]{}
   }
  }while($added)
  foreach($p in $owned.Values){
   try{$row=Read-Owned $p $sampleStart;$processRows.Add($row);$seenThisSample[([string]$p.Id)+':'+$p.StartTime.ToUniversalTime().Ticks]=$true}
   catch [ComponentModel.Win32Exception]{$readFailures++}
   catch [InvalidOperationException]{$readFailures++}
   finally{$p.Dispose()}
  }
  foreach($key in @($active.Keys)){if(!$seenThisSample.ContainsKey($key)){$active.Remove($key)}}
  $gpu=@();$gpuStatus='available'
  try{
   $gpu=@(Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUProcessMemory -Property Name,DedicatedUsage,SharedUsage,TotalCommitted)
  }catch{$gpuStatus='unavailable';$gpuFailures++}
  foreach($row in $processRows){
   $matches=@($gpu|Where-Object {$_.Name -match ('^pid_'+$row.pid+'_')})
   $row.gpuStatus=if($gpuStatus -ne 'available'){$gpuStatus}elseif(!$matches.Count){'missing-instance'}else{'available'}
   $row.gpuDedicatedBytes=$null;$row.gpuSharedBytes=$null
   if($row.gpuStatus -eq 'available'){
    $valid=@($matches|Where-Object {$null -ne $_.DedicatedUsage -and $null -ne $_.SharedUsage})
    if($valid.Count -eq $matches.Count){
     $row.gpuDedicatedBytes=[long](($matches|Measure-Object DedicatedUsage -Sum).Sum)
     $row.gpuSharedBytes=[long](($matches|Measure-Object SharedUsage -Sum).Sum)
    }else{$row.gpuStatus='invalid-counter'}
   }
  }
  $dwm=@(Get-Process -Name dwm -ErrorAction SilentlyContinue|Where-Object {$_.SessionId -eq $rootSession})
  $dwmRows=@(foreach($p in $dwm){
   try{$matches=@($gpu|Where-Object {$_.Name -match ('^pid_'+$p.Id+'_')})
    @{pid=$p.Id;gpuDedicatedBytes=if($matches.Count){($matches|Measure-Object DedicatedUsage -Sum).Sum}else{$null};
     gpuSharedBytes=if($matches.Count){($matches|Measure-Object SharedUsage -Sum).Sum}else{$null}}
   }finally{$p.Dispose()}
  })
  $deltaSeconds=if($null -ne $lastElapsed){$sampleStart-$lastElapsed}else{$null}
  if($null -ne $deltaSeconds){$maxGap=[Math]::Max($maxGap,$deltaSeconds)}
  $sum=@{privateBytes=0L;workingSetBytes=0L;privateWorkingSetBytes=0L;handles=0L;threads=0L;gdi=0L;user=0L;cpuDeltaMilliseconds=0.0;pageFaultDelta=0L;gpuDedicatedBytes=0L;gpuSharedBytes=0L}
  foreach($row in $processRows){foreach($key in @($sum.Keys)){
   if($null -ne $row[$key]){$sum[$key]+=$row[$key]}elseif($key -like 'gpu*'){$sum[$key]=$null}
  }}
  # Do not turn a partial aggregate back into a numeric GPU total.
  if(@($processRows|Where-Object {$_.gpuStatus -ne 'available'}).Count -gt 0 -or !$processRows.Count){$sum.gpuDedicatedBytes=$null;$sum.gpuSharedBytes=$null}
  $cpuPercent=if($deltaSeconds -gt 0){100*$sum.cpuDeltaMilliseconds/1000/$deltaSeconds/$logical}else{$null}
  $row=@{utc=[DateTimeOffset]::UtcNow.ToString('O');elapsedSeconds=$sampleStart;phase=$phase;sampleCostMilliseconds=1000*($clock.Elapsed.TotalSeconds-$sampleStart);
   gapSeconds=$deltaSeconds;processes=@($processRows);dwmContext=$dwmRows;totals=$sum;cpuPercent=$cpuPercent}
  $line=$row|ConvertTo-Json -Depth 7 -Compress
  if($writer.BaseStream.Position+[Text.Encoding]::UTF8.GetByteCount($line)+2 -gt 64MB){$endReason='OutputLimit';break}
  $writer.WriteLine($line)
  $stats.Add(@{phase=$phase;elapsedSeconds=$sampleStart;privateBytes=$sum.privateBytes;workingSetBytes=$sum.workingSetBytes;privateWorkingSetBytes=$sum.privateWorkingSetBytes;
   gpuDedicatedBytes=$sum.gpuDedicatedBytes;gpuSharedBytes=$sum.gpuSharedBytes;cpuPercent=$cpuPercent;cpuDeltaMilliseconds=$sum.cpuDeltaMilliseconds;gapSeconds=$deltaSeconds;handles=$sum.handles;threads=$sum.threads;gdi=$sum.gdi;user=$sum.user;processCount=$processRows.Count})
  $lastElapsed=$sampleStart
  if($rootEnded -or ($null -ne $steadyStarted -and $sampleStart-$steadyStarted -ge $SampleSeconds)){break}
  # A sampler wait only; the application has no new polling loop.
  $boundary=if($null -eq $steadyStarted){$WarmupSeconds}else{$steadyStarted+$SampleSeconds}
  $due=[Math]::Min($sampleStart+$IntervalSeconds,$boundary)
  $pause=[Math]::Max(0,[int][Math]::Ceiling(1000*($due-$clock.Elapsed.TotalSeconds)))
  if($pause -gt 0){Start-Sleep -Milliseconds $pause}
 }
}catch{$endReason='SamplerFailed';$samplerError=$_.Exception.GetType().Name}
finally{$writer.Dispose();$target.Dispose()}
function Distribution($rows,[string]$field){
 $values=@($rows|ForEach-Object {$_[$field]}|Where-Object {$null -ne $_}|Sort-Object)
 if(!$values.Count){return @{count=0;p95=$null;maximum=$null;minimum=$null}}
 return @{count=$values.Count;minimum=$values[0];maximum=$values[-1];p95=$values[[Math]::Max(0,[int][Math]::Ceiling(.95*$values.Count)-1)]}
}
$steady=@($stats|Where-Object {$_.phase -eq 'steady'})
$summary=@{schemaVersion=1;rootPid=$RootProcessId;rootStartedUtc=$rootStarted.ToString('O');endReason=$endReason;samplerError=$samplerError;
 startedUtc=$started.ToString('O');endedUtc=[DateTimeOffset]::UtcNow.ToString('O');elapsedSeconds=$clock.Elapsed.TotalSeconds;
 sampleCount=$stats.Count;steadySampleCount=$steady.Count;maximumGapSeconds=$maxGap;readFailures=$readFailures;gpuQueryFailures=$gpuFailures;
 observedProcessIdentities=@($known.Values);rootEnded=$rootEnded;samplerCpuMilliseconds=(Get-Process -Id $PID).TotalProcessorTime.TotalMilliseconds-$initialSamplerCpu;
 fullyObservedLifetimePeaks=$false;dwmAttribution='Not determined; raw same-session context only';
 childProcessObserved=(@($known.Values|Where-Object {$_.pid -ne $RootProcessId}).Count -gt 0);
 steadyWindowSeconds=if($steady.Count -gt 1){$steady[-1].elapsedSeconds-$steady[0].elapsedSeconds}else{0};
 completedRequestedDuration=($endReason -eq 'Completed' -and $steady.Count -gt 1 -and $steady[-1].elapsedSeconds-$steady[0].elapsedSeconds -ge $SampleSeconds)}
foreach($field in @('privateBytes','workingSetBytes','privateWorkingSetBytes','gpuDedicatedBytes','gpuSharedBytes','cpuPercent','handles','threads','gdi','user','processCount')){
 $summary[$field]=Distribution $steady $field
}
$timed=@($steady|Where-Object {$_.gapSeconds -gt 0 -and $_.elapsedSeconds-$_.gapSeconds -ge $steadyStarted})
$totalTime=($timed|Measure-Object gapSeconds -Sum).Sum
$summary.cpuMeanPercent=if($totalTime -gt 0){100*(($timed|Measure-Object cpuDeltaMilliseconds -Sum).Sum)/1000/$totalTime/$logical}else{$null}
$summary|ConvertTo-Json -Depth 7|Set-Content (Join-Path $OutputDirectory 'summary.json') -Encoding utf8NoBOM
Write-Output ('Resource sampler ended: '+$endReason+'; samples '+$stats.Count+'; summary '+(Join-Path $OutputDirectory 'summary.json'))
if($endReason -ne 'Completed'){exit 1}
