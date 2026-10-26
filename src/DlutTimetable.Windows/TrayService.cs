using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Win32.SafeHandles;
using DlutTimetable.Contracts;
using DlutTimetable.Application;

namespace DlutTimetable.Windows;

// Shell owns placement. One hidden top-level HWND receives callbacks and the
// TaskbarCreated broadcast; no Forms controls, permanent popup or frame timer.
public sealed class TrayService : IDisposable
{
    internal const int CallbackMessage=0x8001;
    private const uint IconId=1;
    private readonly HwndSource source;
    private readonly Action<AppCommandKind> route;
    private readonly nint icon;
    private readonly bool ownsIcon;
    private readonly uint taskbar=NativeMethods.RegisterWindowMessage("TaskbarCreated");
    private StartupState startup=new(false,null,false,false,"");
    private bool startupAvailable,version4,menuOpen,disposed;
    internal nint Handle=>disposed?0:source.Handle;
    internal bool Registered {get;private set;}
    public TrayService(Action<AppCommandKind> route)
    {
        this.route=route;
        source=new(new HwndSourceParameters("大工课表 · 托盘消息")
        {WindowStyle=unchecked((int)0x80000000),ExtendedWindowStyle=0x80,PositionX=-32000,PositionY=-32000,Width=0,Height=0});
        var loaded=Native.LoadImageW(0,Path.Combine(AppContext.BaseDirectory,"Assets","DlutTimetable.ico"),1,0,0,0x10);
        icon=loaded!=0?loaded:Native.LoadIconW(0,(nint)32512);ownsIcon=loaded!=0;
        source.AddHook(Hook);RestoreRegistration();
    }
    private Native.IconData Data()=>new()
    {
        Size=(uint)Marshal.SizeOf<Native.IconData>(),Window=source.Handle,Id=IconId,Flags=0x87,
        Callback=CallbackMessage,Icon=icon,Tip="大工桌面课表",Info="",Title=""
    };
    public bool RestoreRegistration()
    {
        if(disposed)return false;
        var data=Data();
        if(Registered && Native.Shell_NotifyIconW(1,ref data))return true;
        Registered=Native.Shell_NotifyIconW(0,ref data) || Native.Shell_NotifyIconW(1,ref data);
        if(Registered){data.Version=4;version4=Native.Shell_NotifyIconW(4,ref data);}
        return Registered;
    }
    public void UpdateStartup(StartupState state,bool available)
    {startup=state;startupAvailable=available;}
    internal bool TryGetRectangle(out NativeMethods.Rect rectangle)
    {
        rectangle=default;if(disposed || !Registered)return false;
        var identity=new Native.IconIdentity{Size=(uint)Marshal.SizeOf<Native.IconIdentity>(),Window=source.Handle,Id=IconId};
        return Native.Shell_NotifyIconGetRect(ref identity,out rectangle)==0;
    }
    private nint Hook(nint window,int message,nint wparam,nint lparam,ref bool handled)
    {
        if(disposed)return 0;
        if(message==taskbar){Registered=false;RestoreRegistration();return 0;}
        if(message!=CallbackMessage)return 0;
        handled=true;
        var code=(int)(lparam.ToInt64()&0xffff);
        if(version4 && ((lparam.ToInt64()>>16)&0xffff)!=IconId)return 0;
        if((version4 && code is 0x400 or 0x401) || (!version4 && code==0x202))Dispatch(1);
        else if((version4 && code==0x7b) || (!version4 && code==0x205))
        {
            var x=version4?(short)(wparam.ToInt64()&0xffff):-1;
            var y=version4?(short)((wparam.ToInt64()>>16)&0xffff):-1;
            if(x==-1 && y==-1)
            {
                if(TryGetRectangle(out var rect)){x=rect.Left+(rect.Right-rect.Left)/2;y=rect.Bottom;}
                else{NativeMethods.GetCursorPos(out var point);x=point.X;y=point.Y;}
            }
            ShowMenu(x,y);
        }
        return 0;
    }
    internal MenuHandle BuildMenu()
    {
        var menu=new MenuHandle(Native.CreatePopupMenu());
        if(menu.IsInvalid){menu.Dispose();throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Create tray menu");}
        try
        {
            void Item(uint id,string label,uint flags=0)=>NativeMethods.Check(Native.AppendMenuW(menu.DangerousGetHandle(),flags,id,label),"Append tray command");
            Item(1,"恢复课表");Item(2,"设置");Item(3,"从学校刷新课表");Item(6,"登录学校");Item(0,"",0x800);
            var flags=(!startupAvailable || startup.Busy?1u:0u)|(startupAvailable && startup.Desired?8u:0u);
            Item(4,"登录 Windows 后启动"+(startup.OutOfSync?"（需检查）":""),flags);
            if(startup.OutOfSync)Item(0,"启动项状态需检查，请打开设置",1);
            Item(5,"退出程序");Native.SetMenuDefaultItem(menu.DangerousGetHandle(),1,false);return menu;
        }
        catch{menu.Dispose();throw;}
    }
    private void ShowMenu(int x,int y)
    {
        if(menuOpen || disposed)return;
        uint command=0;menuOpen=true;
        try
        {
            using var menu=BuildMenu();Native.SetForegroundWindow(source.Handle);
            command=Native.TrackPopupMenuEx(menu.DangerousGetHandle(),0x102,x,y,source.Handle,0);
        }
        finally
        {
            menuOpen=false;
            if(!disposed)
            {
                Native.PostMessageW(source.Handle,0,0,0);
                if(command==0){var data=Data();Native.Shell_NotifyIconW(3,ref data);}
            }
        }
        if(!disposed)Dispatch(command);
    }
    internal void Dispatch(uint command)
    {
        if(disposed)return;
        var kind=command switch
        {
            1=>AppCommandKind.RestoreTimetable,2=>AppCommandKind.OpenSettings,3=>AppCommandKind.ManualRefresh,
            4 when startupAvailable && !startup.Busy=>AppCommandKind.SetStartup,5=>AppCommandKind.ExitApplication,
            6=>AppCommandKind.LoginAndRetry,
            _=>(AppCommandKind?)null
        };
        if(kind is {} value)route(value);
    }
    public void Dispose()
    {
        if(disposed)return;
        if(menuOpen)Native.EndMenu();
        var data=Data();Native.Shell_NotifyIconW(2,ref data);Registered=false;
        if(ownsIcon)Native.DestroyIcon(icon);
        disposed=true;source.RemoveHook(Hook);source.Dispose();
    }
    internal sealed class MenuHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public MenuHandle(nint value):base(true)=>SetHandle(value);
        protected override bool ReleaseHandle()=>Native.DestroyMenu(handle);
    }
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] internal struct IconData
        {
            internal uint Size;internal nint Window;internal uint Id,Flags,Callback;internal nint Icon;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]internal string Tip;
            internal uint State,StateMask;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)]internal string Info;
            internal uint Version;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)]internal string Title;
            internal uint InfoFlags;internal Guid Guid;internal nint BalloonIcon;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct IconIdentity
        {internal uint Size;internal nint Window;internal uint Id;internal Guid Guid;}
        [DllImport("shell32.dll",CharSet=CharSet.Unicode)]internal static extern bool Shell_NotifyIconW(uint operation,ref IconData data);
        [DllImport("shell32.dll")]internal static extern int Shell_NotifyIconGetRect(ref IconIdentity identity,out NativeMethods.Rect rectangle);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)]internal static extern nint LoadIconW(nint instance,nint name);
        [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]internal static extern nint LoadImageW(nint instance,string name,uint type,int width,int height,uint flags);
        [DllImport("user32.dll")]internal static extern bool DestroyIcon(nint icon);
        [DllImport("user32.dll",SetLastError=true)]internal static extern nint CreatePopupMenu();
        [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]internal static extern bool AppendMenuW(nint menu,uint flags,nuint command,string label);
        [DllImport("user32.dll")]internal static extern bool DestroyMenu(nint menu);
        [DllImport("user32.dll")]internal static extern uint TrackPopupMenuEx(nint menu,uint flags,int x,int y,nint owner,nint parameters);
        [DllImport("user32.dll")]internal static extern bool SetMenuDefaultItem(nint menu,uint item,bool byPosition);
        [DllImport("user32.dll")]internal static extern bool SetForegroundWindow(nint window);
        [DllImport("user32.dll")]internal static extern bool EndMenu();
        [DllImport("user32.dll")]internal static extern bool PostMessageW(nint window,uint message,nint wparam,nint lparam);
    }
}
