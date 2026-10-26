using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using DlutTimetable.Windows.Materials;

namespace DlutTimetable.Windows.IntegrationTests;

public sealed class RefractionTests
{
    [Theory]
    [InlineData(0.8)]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(3.6)]
    [InlineData(4.8)]
    public void LiveSourceViewportStaysInsideThePanelWithBoundedDisplacementAtEachDpi(double scale)
    {
        var plan=RefractionLayout.Create((int)Math.Round(780*scale),(int)Math.Round(468*scale),scale,new(MaterialKind.LiquidEdge,Displacement:12));
        Assert.InRange(plan.MaximumDisplacement,0.1,12*scale);
        Assert.True(plan.SourceViewport.Width>0 && plan.SourceViewport.Height>0);
        Assert.InRange(plan.SourceViewport.X,0,plan.Width-plan.SourceViewport.Width);
        Assert.InRange(plan.SourceViewport.Y,0,plan.Height-plan.SourceViewport.Height);
    }
    [Fact]
    public void ZeroAndChangedStrengthAlterSourceCoordinatesWithoutChangingTheInkViewport()
    {
        var zero=RefractionLayout.Create(780,468,1,new(MaterialKind.LiquidEdge,Displacement:0));
        var stronger=RefractionLayout.Create(780,468,1,new(MaterialKind.LiquidEdge,Displacement:12));
        Assert.Equal(0,zero.MaximumDisplacement);
        Assert.True(stronger.MaximumDisplacement>0);
        Assert.Equal(zero.Width,stronger.Width);Assert.Equal(zero.Height,stronger.Height);
        Assert.True(stronger.SourceViewport.Width<zero.SourceViewport.Width);
        Assert.True(stronger.AtDepth(0.1).MaximumDisplacement>stronger.AtDepth(0.9).MaximumDisplacement);
        Assert.Equal(0,stronger.AtDepth(1).MaximumDisplacement);
        var whole=stronger.Map(-900,-200,new(-1000,-300,2000,1200),new(0,0,2000,1200))!.Value;
        Assert.Equal(new RefractionBox(0,0,780,468),whole.Destination);
        Assert.Null(stronger.Map(-900,-200,new(1000,1000,500,500),new(0,0,500,500)));
    }
    [Fact]
    public void QueuedGeometryUpdateCannotRecreateNativeResourcesAfterRelease()=>Sta(()=>
    {
        using var edge=new LiquidEdgeSurface();
        Assert.True(edge.TryShow(new(80,80,780,468),new(MaterialKind.LiquidEdge),0,false),edge.Failure);
        edge.PlaceBehind(NativeMethods.FindWindow("Progman",null));var handle=edge.Handle;
        edge.Dispose();
        var frame=new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,()=>frame.Continue=false);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        Assert.False(NativeMethods.IsWindow(handle));Assert.Equal(0,edge.Handle);
        Assert.Equal(0,edge.Statistics.LiveSources);Assert.Equal(0,edge.Statistics.Relationships);
        Assert.Equal(0,edge.Statistics.EventSubscriptions);Assert.False(edge.Statistics.TimerActive);
    });
    [Fact]
    public void NativeRingExcludesCenterAndCornersAndZeroStrengthReleasesItsWindowsAndTimer()=>Sta(()=>
    {
        using var edge=new LiquidEdgeSurface();
        Assert.True(edge.TryShow(new(80,80,780,468),new(MaterialKind.LiquidEdge),0,false),edge.Failure);
        Assert.False(edge.Statistics.TimerActive);Assert.Equal(1,edge.Statistics.NativeWindows);
        var region=CreateRectRgn(0,0,0,0);
        try
        {
            Assert.True(GetWindowRgn(edge.Handle,region)>0);
            Assert.False(PtInRegion(region,0,0));Assert.False(PtInRegion(region,779,0));
            Assert.False(PtInRegion(region,0,467));Assert.False(PtInRegion(region,779,467));
            Assert.False(PtInRegion(region,390,234));Assert.True(PtInRegion(region,390,2));
        }
        finally{DeleteObject(region);}
        var handle=edge.Handle;
        Assert.False(edge.TryShow(new(80,80,780,468),new(MaterialKind.LiquidEdge,Displacement:0),0,false));
        Assert.False(NativeMethods.IsWindow(handle));Assert.Equal(0,edge.Statistics.NativeWindows);Assert.False(edge.Statistics.TimerActive);
    });
    private static void Sta(Action action)
    {
        Exception? error=null;var thread=new Thread(()=>{try{action();}catch(Exception ex){error=ex;}});
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(error is not null)ExceptionDispatchInfo.Capture(error).Throw();
    }
    [DllImport("gdi32.dll")]private static extern nint CreateRectRgn(int a,int b,int c,int d);
    [DllImport("user32.dll")]private static extern int GetWindowRgn(nint window,nint region);
    [DllImport("gdi32.dll")]private static extern bool PtInRegion(nint region,int x,int y);
    [DllImport("gdi32.dll")]private static extern bool DeleteObject(nint region);
}
