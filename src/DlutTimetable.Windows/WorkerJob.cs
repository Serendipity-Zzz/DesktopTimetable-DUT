using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DlutTimetable.Windows;

/// <summary>Kernel ownership boundary for this worker and descendants that remain in its job.</summary>
internal sealed class WorkerJob : IDisposable
{
    private readonly SafeFileHandle handle;
    public WorkerJob(Process process)
    {
        handle=CreateJobObject(0,null);
        if(handle.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var limits=new ExtendedLimits {Basic=new(){Flags=0x2000}}; // KILL_ON_JOB_CLOSE
            if(!SetInformationJobObject(handle,9,ref limits,(uint)Marshal.SizeOf<ExtendedLimits>())
                || !AssignProcessToJobObject(handle,process.Handle))throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch {handle.Dispose();throw;}
    }
    public void Dispose()=>handle.Dispose();
    public int ActiveProcessCount()
    {
        var buffer=new byte[65536];
        if(!QueryInformationJobObject(handle,3,buffer,(uint)buffer.Length,out _))throw new Win32Exception(Marshal.GetLastWin32Error());
        return BitConverter.ToInt32(buffer,4);
    }
    [StructLayout(LayoutKind.Sequential)]private struct BasicLimits
    { public long ProcessTime,JobTime;public uint Flags;public nuint MinimumWorkingSet,MaximumWorkingSet;public uint ActiveProcesses;public nuint Affinity;public uint Priority,Scheduling; }
    [StructLayout(LayoutKind.Sequential)]private struct IoCounters {public ulong ReadOperations,WriteOperations,OtherOperations,ReadBytes,WriteBytes,OtherBytes;}
    [StructLayout(LayoutKind.Sequential)]private struct ExtendedLimits
    {public BasicLimits Basic;public IoCounters Io;public nuint ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateJobObject(nint attributes,string? name);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool SetInformationJobObject(SafeFileHandle job,int type,ref ExtendedLimits limits,uint length);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool AssignProcessToJobObject(SafeFileHandle job,nint process);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool QueryInformationJobObject(SafeFileHandle job,int type,[Out]byte[] information,uint length,out uint returned);
}
