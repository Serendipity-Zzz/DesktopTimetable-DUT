using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Interop;
using DlutTimetable.Application;
using DlutTimetable.Domain;
using DlutTimetable.Windows.Materials;

namespace DlutTimetable.Windows.IntegrationTests;

public sealed class RecoveryTests
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint SendMessageW(nint window,int message,nint code,nint session);
    [Fact]
    public void InitialNativeSnapshotReadsOnlyTheCurrentSessionState()
    {
        using var process=System.Diagnostics.Process.GetCurrentProcess();
        var snapshot=NativeSessionSnapshot.Read(process.SessionId);
        Assert.NotNull(snapshot);
        // Do not require an unlocked workstation: this test may also run in a
        // locked CI/session. Only the native ABI and bounded state contract matter.
        Assert.Equal(8,NativeSessionSnapshot.DataOffset);
        Assert.Equal(232,NativeSessionSnapshot.NativeSize);
    }
    [Fact]
    public void InitialSnapshotRejectsUnknownLayoutAndPreservesAnUnknownLockState()
    {
        var buffer=System.Runtime.InteropServices.Marshal.AllocHGlobal(NativeSessionSnapshot.NativeSize);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(new byte[NativeSessionSnapshot.NativeSize],0,buffer,NativeSessionSnapshot.NativeSize);
            var data=NativeSessionSnapshot.DataOffset;
            void Put(int offset,int value)=>System.Runtime.InteropServices.Marshal.WriteInt32(buffer,offset,value);
            Put(0,1);Put(data,42);Put(data+4,4);Put(data+8,0);
            var snapshot=NativeSessionSnapshot.Parse(buffer,(uint)NativeSessionSnapshot.NativeSize,42);
            Assert.Equal(new SessionSnapshot(true,true),snapshot);
            var state=new PresentationAvailability(42);state.ApplySnapshot(snapshot!.Value);Assert.True(state.Paused);
            Put(data+4,0);Put(data+8,-1);
            state.ApplySnapshot(NativeSessionSnapshot.Parse(buffer,(uint)NativeSessionSnapshot.NativeSize,42)!.Value);
            Assert.True(state.Paused); // An unknown lock flag cannot unlock a known lock.
            Put(data+8,1);state.ApplySnapshot(NativeSessionSnapshot.Parse(buffer,(uint)NativeSessionSnapshot.NativeSize,42)!.Value);
            Assert.False(state.Paused);
            Assert.Null(NativeSessionSnapshot.Parse(buffer,16,42));
            Assert.Null(NativeSessionSnapshot.Parse(buffer,(uint)NativeSessionSnapshot.NativeSize,43));
            Put(0,2);Assert.Null(NativeSessionSnapshot.Parse(buffer,(uint)NativeSessionSnapshot.NativeSize,42));
        }
        finally{System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);}
    }
    [Fact]
    public void SessionAndPowerReasonsRemainIndependent()
    {
        var state=new PresentationAvailability(42);
        Assert.False(state.Observe(0x2B1,7,41));Assert.False(state.Paused);
        Assert.True(state.Observe(0x2B1,7,42));Assert.True(state.Paused);
        Assert.False(state.Observe(0x218,4,0));
        Assert.False(state.Observe(0x218,0x12,0));Assert.True(state.Paused);
        Assert.False(state.Observe(0x218,7,0));Assert.True(state.Paused);
        Assert.False(state.Observe(0x2B1,4,42));
        Assert.False(state.Observe(0x2B1,8,42));Assert.True(state.Paused);
        Assert.True(state.Observe(0x2B1,3,42));Assert.False(state.Paused);
        Assert.True(state.Observe(0x218,4,0));Assert.True(state.Paused);
        Assert.False(state.Observe(0x218,0x12,0));Assert.True(state.Paused);
        Assert.True(state.Observe(0x218,7,0));Assert.False(state.Paused);
    }
    [Fact]
    public void OwnedNativeSessionMessagesHideAndRestoreWithoutExecutingCommands()=>Sta(()=>
    {
        var display=DisplayLayout.Select(null);var initial=new SceneSnapshot(TimetableState.Locked,EdgePhase.Expanded,
            new TimetableGeometry(new(display.WorkArea.X+50,display.WorkArea.Y+50),0.8),new(80,80),EdgeDirection.Right,0.5,WindowLevel.Desktop);
        using var scene=new WindowStateController(initial,TimeProvider.System,a=>a());
        var commands=0;using var host=new NativeHostGroup(scene,_=>commands++,display.Id);
        host.Material.Configure(new(MaterialKind.Solid));var failures=new List<Exception>();host.Failed+=failures.Add;
        host.Show();Assert.True(host.SessionNotificationsRegistered);
        var panel=Window.GetWindow(host.View);Assert.True(panel.IsVisible);
        using var process=System.Diagnostics.Process.GetCurrentProcess();var session=process.SessionId;
        SendMessageW(host.ObserverHandle,0x2B1,7,session);
        Assert.True(host.PresentationPaused);Assert.False(panel.IsVisible);Assert.False(host.View.HasLiveBackdrop);
        host.RetryMaterial();Assert.False(panel.IsVisible);
        SendMessageW(host.ObserverHandle,0x218,7,0);Assert.True(host.PresentationPaused);
        SendMessageW(host.ObserverHandle,0x2B1,8,session);
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.False(host.PresentationPaused);Assert.True(panel.IsVisible);
        Assert.Equal(initial.State,scene.Current.State);Assert.Equal(initial.Expanded,scene.Current.Expanded);
        Assert.Equal(0,commands);Assert.Empty(failures);
        host.Dispose();Assert.False(host.SessionNotificationsRegistered);
    });
    private static void Sta(Action action)
    {
        Exception? error=null;var thread=new Thread(()=>{try{action();}catch(Exception ex){error=ex;}});
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(error is not null)ExceptionDispatchInfo.Capture(error).Throw();
    }
    [Fact]
    public void DesktopRepairRestoresTamperedOwnershipAndRecreatesAClosedOwnedLayer()=>Sta(()=>
    {
        var display=DisplayLayout.Select(null);var initial=new SceneSnapshot(TimetableState.Locked,EdgePhase.Expanded,
            new TimetableGeometry(new(display.WorkArea.X+50,display.WorkArea.Y+50),0.8),new(80,80),EdgeDirection.Right,0.5,WindowLevel.Desktop);
        using var scene=new WindowStateController(initial,TimeProvider.System,action=>action());
        using var host=new NativeHostGroup(scene,_=>{},display.Id);host.Material.Configure(new(MaterialKind.Solid));
        var failures=new List<Exception>();host.Failed+=failures.Add;
        var focus=GetForegroundWindow();host.Show();
        var first=Window.GetWindow(host.View);var handle=new WindowInteropHelper(first).Handle;
        NativeMethods.SetLong(handle,-8,0);
        NativeMethods.Check(NativeMethods.SetWindowPos(handle,-1,0,0,0,0,NativeMethods.NoActivate|NativeMethods.NoMove|NativeMethods.NoSize),"Inject test layer fault");
        Assert.True(host.TryRecoverDesktop());
        Assert.NotEqual(0,NativeMethods.GetWindowLongPtr(handle,-8));
        Assert.Equal(0,NativeMethods.GetWindowLongPtr(handle,-20).ToInt64()&8);
        first.Close();Assert.True(host.TryRecoverDesktop());
        var replacement=Window.GetWindow(host.View);Assert.NotSame(first,replacement);Assert.True(replacement.IsVisible);
        Assert.Equal(initial.Expanded,scene.Current.Expanded);Assert.Equal(initial.State,scene.Current.State);
        Assert.Equal(focus,GetForegroundWindow());Assert.Empty(failures);
    });
    [Fact]
    public void ExplicitMaterialRetryRecoversAnUnavailableBackendWithoutChangingPreferences()=>Sta(()=>
    {
        var unavailable=true;var attempts=0;var releases=0;var view=new TimetableView();
        using var material=new MaterialController(view,transparencyAllowed:()=>true);
        material.UseLiveBackdrop((_,_)=>{attempts++;if(unavailable)throw new System.Runtime.InteropServices.COMException("PrivateErrorSecret");},()=>releases++);
        var display=DisplayLayout.Select(null);var bounds=new DipRect(80,80,780,468);
        material.Configure(new(MaterialKind.LiquidEdge,BackgroundTransparency:0.9,ForegroundTransparency:0.2));
        material.Request(display,bounds,1);Assert.False(view.HasLiveBackdrop);Assert.Contains("回退",material.Status);
        var configured=material.Parameters;unavailable=false;
        material.InvalidateSource();material.Request(display,bounds,1);
        Assert.True(view.HasLiveBackdrop);Assert.Equal(configured,material.Parameters);
        Assert.Equal(2,attempts);Assert.True(releases>=2);Assert.DoesNotContain("PrivateErrorSecret",material.Status);
    });
}
