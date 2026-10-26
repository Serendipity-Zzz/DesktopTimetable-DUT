param([Parameter(Mandatory)][int]$RootProcessId,[string]$ExpectedProcessName='DlutTimetable.Desktop',[Parameter(Mandatory)][string]$OutputFile,[switch]$Regions)
$ErrorActionPreference='Stop'
$process=Get-Process -Id $RootProcessId
if($process.ProcessName -cne $ExpectedProcessName){$process.Dispose();throw 'Module inspection identity mismatch'}
Add-Type @'
using System;using System.ComponentModel;using System.Runtime.InteropServices;
public static class ResidentModuleRead {
 [StructLayout(LayoutKind.Sequential)] struct Page {public IntPtr Address;public UIntPtr Flags;}
 [DllImport("psapi.dll",SetLastError=true)] static extern bool QueryWorkingSetEx(IntPtr process,[In,Out] Page[] pages,uint bytes);
 public record Counters(long ResidentBytes,long ShareableBytes,long SharedWithOtherProcessesBytes,long PrivateBytes);
 [StructLayout(LayoutKind.Sequential)] struct Region {
  public IntPtr Base,AllocationBase;public uint AllocationProtect;public ushort Partition,Reserved;
  public UIntPtr Size;public uint State,Protect,Type;
 }
 [DllImport("kernel32.dll",SetLastError=true)] static extern UIntPtr VirtualQueryEx(IntPtr process,IntPtr address,out Region region,UIntPtr bytes);
 [DllImport("psapi.dll",CharSet=CharSet.Unicode)] static extern uint GetMappedFileNameW(IntPtr process,IntPtr address,System.Text.StringBuilder name,uint size);
 public record Category(string Kind,long CommittedBytes,long ResidentBytes,long ShareableBytes,long PrivateBytes);
 public static Category[] Regions(IntPtr process){
  var totals=new System.Collections.Generic.Dictionary<string,Category>();long address=0,scanned=0;
  while(true){
   if(VirtualQueryEx(process,new IntPtr(address),out var r,(UIntPtr)Marshal.SizeOf<Region>())==UIntPtr.Zero){
    if(Marshal.GetLastWin32Error()!=87)throw new Win32Exception(Marshal.GetLastWin32Error());break;
   }
   long length=checked((long)r.Size.ToUInt64()),next=checked(r.Base.ToInt64()+length);
   if(next<=address)throw new InvalidOperationException("Memory region did not advance");address=next;
   if(r.State!=0x1000)continue;
   scanned+=length;if(scanned>4L*1024*1024*1024)throw new InvalidOperationException("Inspection size limit exceeded");
   string kind=r.Type==0x1000000?"image":r.Type==0x20000?"private-allocation":"other-mapping";
   if(r.Type==0x40000){
    var path=new System.Text.StringBuilder(2048);
    if(GetMappedFileNameW(process,r.Base,path,2048)>0){
     var ext=System.IO.Path.GetExtension(path.ToString()).ToLowerInvariant();
     if(ext==".ttc"||ext==".ttf"||ext==".otf")kind="font-mapping";
    }
   }
   if(!totals.TryGetValue(kind,out var total))total=new(kind,0,0,0,0);
   long resident=0,shareable=0,privateBytes=0;
   for(long offset=0;offset<length;offset+=16*1024*1024){
    var value=Read(process,r.Base+checked((int)offset),(int)Math.Min(16*1024*1024,length-offset));
    resident+=value.ResidentBytes;shareable+=value.ShareableBytes;privateBytes+=value.PrivateBytes;
   }
   totals[kind]=new(kind,total.CommittedBytes+length,total.ResidentBytes+resident,total.ShareableBytes+shareable,total.PrivateBytes+privateBytes);
  }
  var values=new Category[totals.Count];totals.Values.CopyTo(values,0);return values;
 }
 public static Counters Read(IntPtr process,IntPtr address,int bytes){
  int size=Environment.SystemPageSize,count=(bytes+size-1)/size;var pages=new Page[count];
  for(var i=0;i<count;i++)pages[i].Address=address+i*size;
  if(!QueryWorkingSetEx(process,pages,(uint)(Marshal.SizeOf<Page>()*count)))throw new Win32Exception(Marshal.GetLastWin32Error());
  long resident=0,shareable=0,shared=0;
  foreach(var page in pages){var flags=page.Flags.ToUInt64();if((flags&1)==0)continue;
   resident+=size;if((flags&32768)!=0){shareable+=size;if(((flags>>1)&7)>1)shared+=size;}}
  return new(resident,shareable,shared,resident-shareable);
 }
}
'@
try{
 $rows=@(foreach($module in $process.Modules){
  $memory=[ResidentModuleRead]::Read($process.Handle,$module.BaseAddress,$module.ModuleMemorySize)
  @{name=$module.ModuleName;virtualBytes=$module.ModuleMemorySize;residentBytes=$memory.ResidentBytes;shareableBytes=$memory.ShareableBytes;
   sharedWithOtherProcessesBytes=$memory.SharedWithOtherProcessesBytes;privateResidentBytes=$memory.PrivateBytes}
 })
 $report=@{capturedAtUtc=[DateTimeOffset]::UtcNow.ToString('O');rootPid=$RootProcessId;rootStartedUtc=$process.StartTime.ToUniversalTime().ToString('O');
  modules=@($rows|Sort-Object residentBytes -Descending);scope='Read-only QueryWorkingSetEx over module address ranges. Counts resident module pages only, not heaps or all mappings; virtual size is not resident size. Shareable pages need not be shared right now. Names only, no paths, addresses or memory content. No target pages are read or touched.'}
 if($Regions){
  $process.Refresh();$report.workingSetBeforeRegionScan=$process.WorkingSet64
  $report.residentCategories=@([ResidentModuleRead]::Regions($process.Handle));$process.Refresh();$report.workingSetAfterRegionScan=$process.WorkingSet64
  $report.regionScope='Committed regions queried in 16 MiB chunks, 4 GiB inspection bound. Font names/paths are classified locally and discarded. Categories describe mapped region type; copy-on-write private pages are counted by the actual Shared flag. Compare totals to contemporaneous working set; no process memory content read, allocation, trimming or locking in target.'
 }
 $report|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $OutputFile -Encoding utf8NoBOM
 $report.modules|Select-Object -First 12 name,@{n='residentMiB';e={[Math]::Round($_.residentBytes/1MB,2)}},@{n='privateResidentMiB';e={[Math]::Round($_.privateResidentBytes/1MB,2)}}|Format-Table
 if($Regions){$report.residentCategories|Format-Table Kind,@{n='residentMiB';e={[Math]::Round($_.ResidentBytes/1MB,2)}},@{n='privateResidentMiB';e={[Math]::Round($_.PrivateBytes/1MB,2)}}}
}finally{$process.Dispose()}
