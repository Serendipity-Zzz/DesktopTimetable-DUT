using System.Runtime.InteropServices;

namespace DlutTimetable.Windows;

internal readonly record struct SessionSnapshot(bool? Locked,bool Disconnected);

internal static class NativeSessionSnapshot
{
    // These structures describe offsets only. User/station/domain strings in the
    // native allocation are never marshalled, stored, logged or returned.
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    private struct Level1
    {
        public uint SessionId;
        public int State,Flags;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=33)]public string Station;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=21)]public string User;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=18)]public string Domain;
        public long Logon,Connect,Disconnect,LastInput,Current;
        public uint IncomingBytes,OutgoingBytes,IncomingFrames,OutgoingFrames,IncomingCompressed,OutgoingCompressed;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Info {public uint Level;public Level1 Data;}
    internal static int NativeSize=>Marshal.SizeOf<Info>();
    internal static int DataOffset=>checked((int)Marshal.OffsetOf<Info>(nameof(Info.Data)));
    internal static SessionSnapshot? Parse(nint buffer,uint bytes,int expectedSession)
    {
        if(buffer==0 || bytes<NativeSize || Marshal.ReadInt32(buffer)!=1)return null;
        var data=DataOffset;
        if(Marshal.ReadInt32(buffer,data)!=expectedSession)return null;
        var state=Marshal.ReadInt32(buffer,data+4);var flags=Marshal.ReadInt32(buffer,data+8);
        if(state is <0 or >9 || flags is not (-1 or 0 or 1))return null;
        return new(flags==-1?null:flags==0,state is not (0 or 1 or 3));
    }
    public static SessionSnapshot? Read(int session)
    {
        nint buffer=0;
        try
        {
            return WTSQuerySessionInformationW(0,session,25,out buffer,out var bytes)?Parse(buffer,bytes,session):null;
        }
        finally {if(buffer!=0)WTSFreeMemory(buffer);}
    }
    [DllImport("wtsapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(nint server,int session,int information,out nint buffer,out uint bytes);
    [DllImport("wtsapi32.dll")]private static extern void WTSFreeMemory(nint buffer);
}
