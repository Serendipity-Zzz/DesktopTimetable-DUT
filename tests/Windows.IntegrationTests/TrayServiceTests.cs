using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using DlutTimetable.Application;
using DlutTimetable.Contracts;
using DlutTimetable.Windows;

namespace DlutTimetable.Windows.IntegrationTests;

// Real Shell registration and Win32 menu objects; no Explorer restart.
public sealed class TrayServiceTests
{
    [Fact]
    public void ShellRegistrationCallbacksRecoveryAndDisposalPreserveIdentity()=>Sta(()=>
    {
        var commands=new List<AppCommandKind>();var tray=new TrayService(commands.Add);
        var handle=tray.Handle;
        try
        {
            Assert.True(tray.Registered);Assert.True(tray.TryGetRectangle(out _));Assert.True(NativeMethods.IsWindow(handle));
            SendMessageW(handle,TrayService.CallbackMessage,0,(nint)((1<<16)|0x400));
            SendMessageW(handle,TrayService.CallbackMessage,0,(nint)((1<<16)|0x401));
            SendMessageW(handle,TrayService.CallbackMessage,0,(nint)((2<<16)|0x400));
            Assert.Equal(new[]{AppCommandKind.RestoreTimetable,AppCommandKind.RestoreTimetable},commands);
            // Simulate the broadcast for this owned HWND, without restarting Shell.
            for(var i=0;i<3;i++)
            {
                SendMessageW(handle,(int)NativeMethods.RegisterWindowMessage("TaskbarCreated"),0,0);
                Assert.True(tray.Registered);Assert.Equal(handle,tray.Handle);Assert.True(tray.TryGetRectangle(out _));
            }
        }
        finally{tray.Dispose();tray.Dispose();}
        Assert.False(NativeMethods.IsWindow(handle));Assert.False(tray.TryGetRectangle(out _));Assert.False(tray.Registered);
        Assert.False(tray.RestoreRegistration());
    });

    [Theory]
    [InlineData(false,false,false,false)]
    [InlineData(true,true,false,false)]
    [InlineData(true,true,true,false)]
    [InlineData(true,false,false,true)]
    public void ActualMenuPreservesCommandsAndStartupAvailability(bool available,bool desired,bool busy,bool outOfSync)=>Sta(()=>
    {
        using var tray=new TrayService(_=>{});
        tray.UpdateStartup(new StartupState(desired,true,outOfSync,busy,"Fixed test status"),available);
        nint handle;
        using(var menu=tray.BuildMenu())
        {
            handle=menu.DangerousGetHandle();Assert.True(IsMenu(handle));
            Assert.Equal(outOfSync?8:7,GetMenuItemCount(handle));
            Assert.Equal(new uint[]{1,2,3,6,0,4},Enumerable.Range(0,6).Select(i=>GetMenuItemID(handle,i)));
            Assert.NotEqual(0u,GetMenuState(handle,4,0x400)&0x800); // A real separator, not an action named zero.
            Assert.Equal(5u,GetMenuItemID(handle,outOfSync?7:6));
            var flags=GetMenuState(handle,4,0);
            Assert.Equal(!available || busy,(flags&1)!=0);
            Assert.Equal(available && desired,(flags&8)!=0);
            var text=new StringBuilder(128);GetMenuStringW(handle,4,text,text.Capacity,0);
            Assert.Equal("登录 Windows 后启动"+(outOfSync?"（需检查）":""),text.ToString());
        }
        Assert.False(IsMenu(handle));
    });

    private static void Sta(Action action)
    {
        Exception? error=null;var thread=new Thread(()=>{try{action();}catch(Exception ex){error=ex;}});
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(error is not null)ExceptionDispatchInfo.Capture(error).Throw();
    }
    [DllImport("user32.dll")]private static extern nint SendMessageW(nint hwnd,int message,nint wparam,nint lparam);
    [DllImport("user32.dll")]private static extern int GetMenuItemCount(nint menu);
    [DllImport("user32.dll")]private static extern uint GetMenuItemID(nint menu,int position);
    [DllImport("user32.dll")]private static extern uint GetMenuState(nint menu,uint item,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetMenuStringW(nint menu,uint item,StringBuilder text,int maximum,uint flags);
    [DllImport("user32.dll")]private static extern bool IsMenu(nint menu);
}
