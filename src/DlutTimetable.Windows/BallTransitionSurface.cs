using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DlutTimetable.Application;
using DlutTimetable.Domain;

namespace DlutTimetable.Windows;

internal sealed class BallTransitionSurface
{
    private readonly BallTransitionView view = new();
    private readonly TimeProvider clock;
    private readonly Action<Action> deferCompletion;
    private MotionTimeline? timeline;
    private DipRect panel, orb;
    private BallMotionPose correction;
    private bool reduced, subscribed, prepared, continuing, rendering, refreshing;
    private bool completionPending;
    private bool controlCaptured;
    private DispatcherTimer? completionTimer;
    private EventHandler? completionTimerTick;
    private long completionTicket;
    private BallMotionPose previous;
    private long pendingGeneration;
    private long began;
    private TimeSpan lastRenderingTime=TimeSpan.MinValue;
    private double captureMilliseconds,maxFrameGapMilliseconds;
    private double minimumBallScale=1;
    private long lastFrame;
    public DesktopLayer Layer { get; }
    public bool Running => prepared || completionPending || timeline?.Running == true;
    public bool ControlIsBall { get; private set; }
    public bool TargetBall { get; private set; }
    public BallMotionPose Pose { get; private set; }
    private Size hostSize;
    public DipRect HostBounds => new(Pose.Bounds.X,Pose.Bounds.Y,hostSize.Width,hostSize.Height);
    public long TextureBytes => view.TextureBytes;
    public bool FrameSubscribed => subscribed;
    public bool CompletionTimerActive=>completionTimer?.IsEnabled==true;
    public long FrameCount { get; private set; }
    public double Progress => timeline?.Progress ?? (TargetBall?1:0);
    public event Action<BallMotionPose>? Frame;
    public event Action<long>? Completed;
    public event Action<string>? Trace;
    public event Action<Exception>? Failed;
    public BallTransitionSurface(TimeProvider clock,Action<Action>? deferCompletion=null)
    {
        this.clock=clock;
        Layer=new("收展过渡",view);
        this.deferCompletion=deferCompletion??DeferAfterNativeInput;
    }
    private void CancelCompletionTimer()
    {
        if(completionTimer is null)return;
        completionTimer.Stop();completionTimer.Tick-=completionTimerTick;
        completionTimer=null;completionTimerTick=null;
    }
    private void DeferAfterNativeInput(Action action)
    {
        CancelCompletionTimer();
        // WM_TIMER is serviced after queued native input; a posted Dispatcher operation
        // alone can run before a queued mouse message. This is a one-shot handoff only.
        var timer=new DispatcherTimer(DispatcherPriority.Background,Layer.Dispatcher){Interval=TimeSpan.FromMilliseconds(1)};
        EventHandler tick=(_,_)=>{CancelCompletionTimer();action();};
        completionTimer=timer;completionTimerTick=tick;timer.Tick+=tick;timer.Start();
    }
    public void SetControlCaptured(bool value)
    {
        if(controlCaptured==value)return;
        controlCaptured=value;
        if(!value && completionPending && timeline is not null)QueueCompletion(timeline,completionTicket);
    }
    private void QueueCompletion(MotionTimeline active,long ticket)
    {
        deferCompletion(()=>
        {
            if(ticket!=completionTicket || !completionPending || active!=timeline || prepared || controlCaptured)return;
            completionPending=false;
            Trace?.Invoke(FormattableString.Invariant($"BALL_END generation={active.Generation} elapsedMs={clock.GetElapsedTime(began).TotalMilliseconds:F3} durationMs={active.Duration.TotalMilliseconds:F3} frames={FrameCount} captureMs={captureMilliseconds:F3} maxFrameGapMs={maxFrameGapMilliseconds:F3} minBallScale={minimumBallScale:F5}"));
            Completed?.Invoke(active.Generation);
        });
    }
    public void Prepare(Func<BitmapSource> capture, DipRect panelBounds, DipRect orbBounds, bool toBall,long generation,bool reduce)
    {
        if (Running && TargetBall==toBall)
        { if(prepared)pendingGeneration=generation;else timeline!.Retarget(toBall,generation,reduce); return; }
        previous=Pose;
        continuing=Running;
        CancelCompletionTimer();
        completionPending=false;completionTicket++;
        Unsubscribe();
        if (!continuing)
        {
            var captureStarted=clock.GetTimestamp();
            view.SetTexture(capture());
            captureMilliseconds=clock.GetElapsedTime(captureStarted).TotalMilliseconds;
            timeline=new(clock,toBall?0:1);
            ControlIsBall=!toBall; FrameCount=0;
        }
        panel=panelBounds;orb=orbBounds;reduced=reduce;TargetBall=toBall;
        // Keep the layered WPF render target at a stable size. Only its origin moves;
        // the retained image and clip describe the visible shrinking surface.
        hostSize=new(Math.Max(panel.Width,Math.Max(orb.Width,continuing?previous.Bounds.Width:0)),
            Math.Max(panel.Height,Math.Max(orb.Height,continuing?previous.Bounds.Height:0)));
        view.SetHostSize(hostSize);
        pendingGeneration=generation;prepared=true;
        if(!continuing)Pose=BallMotionPath.Evaluate(toBall?0:1,panel,orb,reduced);
        view.SetPose(Pose);
    }
    public void Activate()
    {
        if(!Running)return;
        if(!prepared){Refresh();return;}
        timeline!.Retarget(TargetBall,pendingGeneration,reduced);
        prepared=false;
        began=clock.GetTimestamp();
        minimumBallScale=1;
        lastFrame=began;maxFrameGapMilliseconds=0;lastRenderingTime=TimeSpan.MinValue;
        var start=BallMotionPath.Evaluate(timeline.Progress,panel,orb,reduced);
        correction=continuing ? Difference(previous,start) : default;
        Refresh();
        if(!Running)return;
        if (!subscribed) { CompositionTarget.Rendering+=RenderFrame;subscribed=true; }
        Trace?.Invoke($"BALL_BEGIN generation={pendingGeneration} target={(TargetBall?"Ball":"Panel")} reduced={reduced} textureBytes={TextureBytes}");
    }
    public void Refresh()
    {
        if (!Running || completionPending || prepared || refreshing) return;
        refreshing=true;
        try
        {
        var pose=BallMotionPath.Evaluate(timeline!.Progress,panel,orb,reduced);
        pose=BallMotionPath.Settle(pose,timeline.Fraction,panel.Height/468,reduced);
        var w=1-timeline.Fraction;
        Pose=new(new(pose.Bounds.X+correction.Bounds.X*w,pose.Bounds.Y+correction.Bounds.Y*w,
            Math.Max(1,pose.Bounds.Width+correction.Bounds.Width*w),Math.Max(1,pose.Bounds.Height+correction.Bounds.Height*w)),
            Math.Clamp(pose.Crop+correction.Crop*w,0.01,1),Math.Max(0,pose.Radius+correction.Radius*w),
            Math.Clamp(pose.TextureOpacity+correction.TextureOpacity*w,0,1),Math.Clamp(pose.BallOpacity+correction.BallOpacity*w,0,1),
            Math.Clamp(pose.BallScale+correction.BallScale*w,reduced?1:BallMotionPath.MinimumBallScale,1));
        minimumBallScale=Math.Min(minimumBallScale,Pose.BallScale);
        view.SetPose(Pose);Frame?.Invoke(Pose);
        }
        finally { refreshing=false; }
    }
    private static BallMotionPose Difference(BallMotionPose a,BallMotionPose b) => new(
        new(a.Bounds.X-b.Bounds.X,a.Bounds.Y-b.Bounds.Y,a.Bounds.Width-b.Bounds.Width,a.Bounds.Height-b.Bounds.Height),
        a.Crop-b.Crop,a.Radius-b.Radius,a.TextureOpacity-b.TextureOpacity,a.BallOpacity-b.BallOpacity,a.BallScale-b.BallScale);
    private void RenderFrame(object? sender,EventArgs args)
    {
        if(rendering)return;
        // WPF can raise Rendering more than once for a presentation timestamp after a resize.
        if(args is RenderingEventArgs frame)
        { if(frame.RenderingTime==lastRenderingTime)return;lastRenderingTime=frame.RenderingTime; }
        rendering=true;
        try
        {
            if (!Running) { Unsubscribe();return; }
            var active=timeline;
            var now=clock.GetTimestamp();
            maxFrameGapMilliseconds=Math.Max(maxFrameGapMilliseconds,clock.GetElapsedTime(lastFrame,now).TotalMilliseconds);lastFrame=now;
            Refresh();
            if(active is null || active!=timeline || prepared)return;
            FrameCount++;
            if (!active.Advance()) return;
            Unsubscribe();
            completionPending=true;
            var ticket=++completionTicket;
            // Rendering has higher dispatcher priority than input. Leave the current control
            // alive until queued mouse releases can retarget instead of hiding it at the endpoint.
            QueueCompletion(active,ticket);
        }
        catch(Exception ex) when(ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        { Stop();Failed?.Invoke(ex); }
        finally { rendering=false; }
    }
    private void Unsubscribe()
    { if (!subscribed)return;CompositionTarget.Rendering-=RenderFrame;subscribed=false; }
    public void Stop()
    {
        var hadResources=timeline is not null || TextureBytes>0;
        Unsubscribe();CancelCompletionTimer();controlCaptured=false;completionPending=false;completionTicket++;timeline=null;prepared=false;view.Clear();Layer.Hide();
        if(hadResources)Trace?.Invoke($"BALL_RELEASE textureBytes={TextureBytes} subscribed={subscribed} completionTimer={CompletionTimerActive}");
    }
    public static BitmapSource Capture(TimetableView source,double scale)
    {
        source.Measure(new(780,468));source.Arrange(new(0,0,780,468));source.UpdateLayout();
        var factor=Math.Clamp(scale,0.5,1.2);
        var width=(int)Math.Ceiling(780*factor);var height=(int)Math.Ceiling(468*factor);
        var visual=new DrawingVisual();
        using(var dc=visual.RenderOpen())dc.DrawRectangle(new VisualBrush(source) {Stretch=Stretch.Fill},null,new(0,0,width,height));
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);bitmap.Freeze();
        return bitmap;
    }
}

internal sealed class BallTransitionView : FrameworkElement
{
    private readonly DrawingVisual drawing=new();
    private readonly ImageDrawing image=new();
    private readonly RectangleGeometry clip=new();
    private readonly DrawingGroup content=new();
    private BitmapSource? texture;
    private BallMotionPose pose;
    private Size hostSize;
    public long TextureBytes => texture is null ? 0 : (long)texture.PixelWidth*texture.PixelHeight*4;
    public BallTransitionView()
    {
        AddVisualChild(drawing);IsHitTestVisible=false;
        content.ClipGeometry=clip;content.Children.Add(image);
        using var dc=drawing.RenderOpen();dc.DrawDrawing(content);
    }
    protected override int VisualChildrenCount=>1;
    protected override Visual GetVisualChild(int index)=>index==0?drawing:throw new ArgumentOutOfRangeException(nameof(index));
    public void SetTexture(BitmapSource bitmap) { texture=bitmap;image.ImageSource=bitmap;UpdateDrawing(); }
    public void SetPose(BallMotionPose value) { pose=value;UpdateDrawing(); }
    public void SetHostSize(Size value) { hostSize=value;UpdateDrawing(); }
    public void Clear() { texture=null;image.ImageSource=null; }
    protected override Size ArrangeOverride(Size finalSize)
    { var result=base.ArrangeOverride(finalSize);UpdateDrawing(finalSize);return result; }
    private void UpdateDrawing(Size? arrangedSize=null)
    {
        var width=arrangedSize?.Width??ActualWidth;var height=arrangedSize?.Height??ActualHeight;
        if(texture is null || width<=0 || height<=0 || pose.Crop<=0)return;
        // Keep one retained image and clip for the whole transition. Mutating these
        // composition properties avoids rebuilding a visual tree on every native resize.
        var scale=width/(hostSize.Width>0?hostSize.Width:pose.Bounds.Width);
        if(hostSize.Width>0){width=pose.Bounds.Width*scale;height=pose.Bounds.Height*scale;}
        var radius=pose.Radius*scale;
        content.Opacity=pose.TextureOpacity;
        clip.Rect=new(0,0,width,height);clip.RadiusX=clip.RadiusY=radius;
        image.Rect=new(0,0,width,height/pose.Crop);
    }
}
