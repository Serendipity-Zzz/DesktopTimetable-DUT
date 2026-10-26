using System.Runtime.ExceptionServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DlutTimetable.Domain;

namespace DlutTimetable.Windows.IntegrationTests;

public sealed class BallTransitionTests
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint SendMessage(nint hwnd,int message,nint wparam,nint lparam);
    private sealed class Clock : TimeProvider
    {
        public long Timestamp;
        public override long GetTimestamp()=>Timestamp;
        public override long TimestampFrequency=>1000;
    }
    private static void Sta(Action action)
    {
        Exception? error=null;
        var thread=new Thread(()=>{try{action();}catch(Exception ex){error=ex;}});
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(error is not null)ExceptionDispatchInfo.Capture(error).Throw();
    }
    private static BitmapSource Texture()
    { var pixels=new byte[64*40*4];Array.Fill(pixels,(byte)255);var bitmap=BitmapSource.Create(64,40,96,96,PixelFormats.Pbgra32,null,pixels,64*4);bitmap.Freeze();return bitmap; }
    [Fact]
    public void RetainedTransitionDrawingPresentsTextureAndClearsItAfterRelease()=>Sta(()=>
    {
        var view=new BallTransitionView();
        view.SetTexture(Texture());
        view.SetPose(new(new(0,0,64,40),1,4,1,0));
        view.Measure(new(64,40));view.Arrange(new(0,0,64,40));view.UpdateLayout();
        byte Alpha()
        {
            var bitmap=new RenderTargetBitmap(64,40,96,96,PixelFormats.Pbgra32);bitmap.Render(view);
            var pixel=new byte[4];bitmap.CopyPixels(new System.Windows.Int32Rect(32,20,1,1),pixel,4,0);return pixel[3];
        }
        Assert.Equal(255,Alpha());
        view.SetPose(new(new(0,0,64,40),0.5,4,0.5,0));
        Assert.InRange(Alpha(),126,129);
        view.Clear();Assert.Equal(0,Alpha());Assert.Equal(0,view.TextureBytes);
    });
    [Fact]
    public void StableRenderTargetClipsShrinkingContentAndConvertsPhysicalPixelsAtHighDpi()=>Sta(()=>
    {
        var view=new BallTransitionView();view.SetTexture(Texture());
        view.SetHostSize(new(128,80));
        view.SetPose(new(new(-500,200,64,40),1,8,1,0));
        // A 128x80 physical host arranged as 64x40 DIP models 200% scale.
        view.Measure(new(64,40));view.Arrange(new(0,0,64,40));view.UpdateLayout();
        var bitmap=new RenderTargetBitmap(64,40,96,96,PixelFormats.Pbgra32);bitmap.Render(view);
        byte Alpha(int x,int y)
        {var pixel=new byte[4];bitmap.CopyPixels(new System.Windows.Int32Rect(x,y,1,1),pixel,4,0);return pixel[3];}
        Assert.Equal(255,Alpha(16,10));
        Assert.Equal(0,Alpha(48,10));Assert.Equal(0,Alpha(16,30));
        Assert.Equal(0,Alpha(0,0));
        view.Clear();Assert.Equal(0,view.TextureBytes);
    });
    [Fact]
    public void TransitionHostSizeStaysConstantAcrossFramesAndReversal()=>Sta(()=>
    {
        var clock=new Clock();var surface=new BallTransitionSurface(clock);
        try
        {
            var panel=new DipRect(-800,60,780,468);var orb=new DipRect(100,600,44,44);
            surface.Prepare(Texture,panel,orb,true,1,false);surface.Activate();
            clock.Timestamp=200;surface.Refresh();
            Assert.Equal(780,surface.HostBounds.Width);Assert.Equal(468,surface.HostBounds.Height);
            Assert.True(surface.Pose.Bounds.Width<surface.HostBounds.Width);
            var before=surface.Pose;
            surface.Prepare(Texture,panel,orb,false,2,false);surface.Activate();
            Assert.Equal(before,surface.Pose);
            Assert.Equal(780,surface.HostBounds.Width);Assert.Equal(468,surface.HostBounds.Height);
        }
        finally{surface.Stop();surface.Layer.Close();}
    });
    [Theory]
    [InlineData(180)]
    [InlineData(200)]
    [InlineData(220)]
    [InlineData(240)]
    [InlineData(260)]
    public void ReversalKeepsOrbSettlementInsideItsFourPercentEnvelope(int reverseAt)=>Sta(()=>
    {
        var clock=new Clock();var surface=new BallTransitionSurface(clock);
        try
        {
            var panel=new DipRect(0,0,780,468);var orb=new DipRect(900,600,44,44);
            surface.Prepare(Texture,panel,orb,true,1,false);surface.Activate();
            clock.Timestamp=reverseAt;surface.Refresh();var before=surface.Pose;
            surface.Prepare(Texture,panel,orb,false,2,false);surface.Activate();
            Assert.Equal(before,surface.Pose);
            for(var elapsed=1;elapsed<=320;elapsed++)
            {
                clock.Timestamp=reverseAt+elapsed;surface.Refresh();
                Assert.InRange(surface.Pose.BallScale,0.96,1);
            }
        }
        finally{surface.Stop();surface.Layer.Close();}
    });
    [Fact]
    public void NativeRapidClickSequenceDispatchesBothOrbIntentsWithoutWaiting()=>Sta(()=>
    {
        var orb=new DesktopLayer("双击消息验证",new System.Windows.Controls.Border()){IsBall=true};
        var clicks=0;orb.BallClicked+=()=>clicks++;
        try
        {
            orb.Ensure();
            var point=(nint)((10<<16)|10);
            SendMessage(orb.Handle,0x201,1,point);SendMessage(orb.Handle,0x202,0,point);
            SendMessage(orb.Handle,0x203,1,point);SendMessage(orb.Handle,0x202,0,point);
            Assert.Equal(2,clicks);
        }
        finally{orb.Close();}
    });
    [Fact]
    public void CommandButtonsHandleQueuedNativePressesWithoutConsultingLaterPhysicalButtonState()=>Sta(()=>
    {
        var button=new NativeCommandButton();var clicks=0;button.Click+=(_,_)=>clicks++;
        var layer=new DesktopLayer("排队按钮验证",button){Width=24,Height=24};
        try
        {
            layer.Ensure();var point=(nint)((10<<16)|10);
            SendMessage(layer.Handle,0x201,1,point);Assert.True(button.IsPressed);SendMessage(layer.Handle,0x202,0,point);
            SendMessage(layer.Handle,0x203,1,point);SendMessage(layer.Handle,0x202,0,point);
            Assert.Equal(2,clicks);Assert.False(button.IsPressed);
        }
        finally{layer.Close();}
    });
    [Fact]
    public void NativePinPreservesToggleSemanticsAndCancelsOutsideDisabledOrHiddenClicks()=>Sta(()=>
    {
        var button=new NativePinButton();var layer=new DesktopLayer("原生固定验证",button){Width=24,Height=24};
        try
        {
            layer.Ensure();var point=(nint)((10<<16)|10);var outside=(nint)((100<<16)|100);
            SendMessage(layer.Handle,0x201,1,point);SendMessage(layer.Handle,0x202,0,point);Assert.True(button.IsChecked);
            SendMessage(layer.Handle,0x201,1,point);SendMessage(layer.Handle,0x202,0,outside);Assert.True(button.IsChecked);
            SendMessage(layer.Handle,0x201,1,point);layer.Hide();Assert.False(button.IsPressed);
            SendMessage(layer.Handle,0x202,0,point);Assert.True(button.IsChecked);
            button.IsEnabled=false;SendMessage(layer.Handle,0x201,1,point);SendMessage(layer.Handle,0x202,0,point);Assert.True(button.IsChecked);
        }
        finally{layer.Close();}
    });
    [Fact]
    public void LayeredStyleRewritesCannotRestoreAnOldInputPolicyOrActivation()=>Sta(()=>
    {
        var layer=new DesktopLayer("输入样式验证",new System.Windows.Controls.Border());
        try
        {
            layer.Ensure();layer.SetTransparent(false);
            var style=(long)NativeMethods.GetWindowLongPtr(layer.Handle,-20);
            NativeMethods.SetLong(layer.Handle,-20,(nint)((style|0x20)&~0x08000080));
            var actual=(long)NativeMethods.GetWindowLongPtr(layer.Handle,-20);
            Assert.Equal(0,actual&0x20);Assert.Equal(0x08000080,actual&0x08000080);
            layer.SetTransparent(true);
            NativeMethods.SetLong(layer.Handle,-20,(nint)(actual&~0x20));
            Assert.NotEqual(0,(long)NativeMethods.GetWindowLongPtr(layer.Handle,-20)&0x20);
        }
        finally{layer.Close();}
    });
    [Fact]
    public void PrepareHasNoFramesBeforeNativeProjectionAndReentrantStopDoesNotResubscribe()=>Sta(()=>
    {
        var surface=new BallTransitionSurface(new Clock());
        try
        {
            surface.Prepare(Texture,new(0,0,780,468),new(80,80,44,44),true,1,false);
            Assert.False(surface.FrameSubscribed);Assert.True(surface.TextureBytes>0);
            surface.Frame+=_=>surface.Stop();surface.Activate();
            Assert.False(surface.Running);Assert.False(surface.FrameSubscribed);Assert.Equal(0,surface.TextureBytes);
        }
        finally{surface.Stop();surface.Layer.Close();}
    });
    [Fact]
    public void ReverseReusesOwnTextureAndPresentationAndStaleFrameCannotCompleteAfterStop()=>Sta(()=>
    {
        var clock=new Clock();var surface=new BallTransitionSurface(clock);var captures=0;var completions=0;
        try
        {
            var panel=new DipRect(0,0,780,468);var orb=new DipRect(900,600,44,44);
            BitmapSource Capture(){captures++;return Texture();}
            surface.Completed+=_=>completions++;
            surface.Prepare(Capture,panel,orb,true,1,false);surface.Activate();clock.Timestamp=160;surface.Refresh();
            var pose=surface.Pose;
            surface.Prepare(Capture,panel with {X=200},orb,false,2,false);surface.Activate();
            Assert.Equal(1,captures);Assert.Equal(pose.Bounds.X,surface.Pose.Bounds.X,6);Assert.Equal(pose.Bounds.Width,surface.Pose.Bounds.Width,6);
            // Simulate a message-pumped cancellation during the native position callback.
            surface.Frame+=_=>surface.Stop();clock.Timestamp=300;
            var frame=typeof(BallTransitionSurface).GetMethod("RenderFrame",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
            frame.Invoke(surface,[null,EventArgs.Empty]);
            Assert.Equal(0,completions);Assert.False(surface.FrameSubscribed);Assert.Equal(0,surface.TextureBytes);
        }
        finally{surface.Stop();surface.Layer.Close();}
    });
    [Fact]
    public void EndpointKeepsItsControlAliveUntilQueuedInputCanCancelTheOldCompletion()=>Sta(()=>
    {
        var clock=new Clock();var callbacks=new Queue<Action>();var completed=new List<long>();var captures=0;
        var surface=new BallTransitionSurface(clock,callbacks.Enqueue);
        var frame=typeof(BallTransitionSurface).GetMethod("RenderFrame",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
        try
        {
            var panel=new DipRect(0,0,780,468);var orb=new DipRect(900,600,44,44);
            BitmapSource Capture(){captures++;return Texture();}
            surface.Completed+=completed.Add;
            surface.Prepare(Capture,panel,orb,false,1,false);surface.Activate();clock.Timestamp=360;
            frame.Invoke(surface,[null,EventArgs.Empty]);
            Assert.True(surface.Running);Assert.True(surface.ControlIsBall);Assert.False(surface.FrameSubscribed);
            Assert.Empty(completed);Assert.Single(callbacks);
            surface.Prepare(Capture,panel,orb,true,2,false);surface.Activate();
            callbacks.Dequeue()();Assert.Empty(completed);Assert.Equal(1,captures);
            clock.Timestamp=720;frame.Invoke(surface,[null,EventArgs.Empty]);callbacks.Dequeue()();
            Assert.Equal(new long[]{2},completed);Assert.False(surface.Running);
        }
        finally{surface.Stop();surface.Layer.Close();}
    });
    [Fact]
    public void PendingCompletionAcceptsEquivalentNewGenerationAndStopInvalidatesQueuedCallbacks()=>Sta(()=>
    {
        var clock=new Clock();var callbacks=new Queue<Action>();var completed=new List<long>();
        var surface=new BallTransitionSurface(clock,callbacks.Enqueue);
        var frame=typeof(BallTransitionSurface).GetMethod("RenderFrame",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
        try
        {
            var panel=new DipRect(0,0,780,468);var orb=new DipRect(900,600,44,44);
            surface.Completed+=completed.Add;
            surface.Prepare(Texture,panel,orb,true,1,false);surface.Activate();clock.Timestamp=360;
            frame.Invoke(surface,[null,EventArgs.Empty]);
            surface.Prepare(Texture,panel,orb,true,9,false);surface.Activate();callbacks.Dequeue()();
            Assert.Equal(new long[]{9},completed);Assert.False(surface.FrameSubscribed);
            surface.Prepare(Texture,panel,orb,false,10,false);surface.Activate();clock.Timestamp=720;
            frame.Invoke(surface,[null,EventArgs.Empty]);surface.Stop();callbacks.Dequeue()();
            Assert.Single(completed);Assert.Equal(0,surface.TextureBytes);Assert.False(surface.Running);
        }
        finally{surface.Stop();surface.Layer.Close();}
    });
    [Fact]
    public void HeldRetargetControlSurvivesEndpointAndReleaseCanSupersedeOldCompletion()=>Sta(()=>
    {
        var clock=new Clock();var callbacks=new Queue<Action>();var completed=new List<long>();
        var surface=new BallTransitionSurface(clock,callbacks.Enqueue);
        var frame=typeof(BallTransitionSurface).GetMethod("RenderFrame",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
        try
        {
            var panel=new DipRect(0,0,780,468);var orb=new DipRect(900,600,44,44);
            surface.Completed+=completed.Add;
            surface.Prepare(Texture,panel,orb,false,1,false);surface.Activate();surface.SetControlCaptured(true);
            clock.Timestamp=400;frame.Invoke(surface,[null,EventArgs.Empty]);callbacks.Dequeue()();
            Assert.Empty(completed);Assert.True(surface.Running);Assert.False(surface.FrameSubscribed);
            surface.SetControlCaptured(false);Assert.Single(callbacks);
            surface.Prepare(Texture,panel,orb,true,2,false);surface.Activate();callbacks.Dequeue()();
            Assert.Empty(completed);
            clock.Timestamp=800;frame.Invoke(surface,[null,EventArgs.Empty]);callbacks.Dequeue()();
            Assert.Equal(new long[]{2},completed);
        }
        finally{surface.Stop();surface.Layer.Close();}
    });
    [Fact]
    public void StopCancelsTheActualOneShotCompletionTimer()=>Sta(()=>
    {
        var clock=new Clock();var surface=new BallTransitionSurface(clock);
        var frame=typeof(BallTransitionSurface).GetMethod("RenderFrame",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
        try
        {
            surface.Prepare(Texture,new(0,0,780,468),new(900,600,44,44),true,1,false);surface.Activate();
            clock.Timestamp=400;frame.Invoke(surface,[null,EventArgs.Empty]);
            Assert.True(surface.CompletionTimerActive);Assert.False(surface.FrameSubscribed);
            surface.Stop();Assert.False(surface.CompletionTimerActive);Assert.Equal(0,surface.TextureBytes);Assert.False(surface.Running);
        }
        finally{surface.Stop();surface.Layer.Close();}
    });
}
