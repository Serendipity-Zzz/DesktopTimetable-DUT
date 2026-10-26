namespace DlutTimetable.Windows.Materials;

internal readonly record struct RefractionBox(int X,int Y,int Width,int Height);
internal readonly record struct RefractionMapping(RefractionBox Source,RefractionBox Destination);
internal sealed record RefractionLayout(int Width,int Height,int Radius,int EdgeWidth,int Inset)
{
    public RefractionBox SourceViewport=>new(Inset,Inset,Width-2*Inset,Height-2*Inset);
    public double MaximumDisplacement=>Math.Sqrt(2)*Inset;
    public RefractionLayout AtDepth(double depth)
    {
        if(!double.IsFinite(depth) || depth is <0 or >1)throw new ArgumentOutOfRangeException(nameof(depth));
        var gain=1-depth*depth*(3-2*depth);return this with{Inset=(int)Math.Floor(Inset*gain)};
    }
    public static RefractionLayout Create(int width,int height,double scale,MaterialParameters parameters)
    {
        parameters.Validate();
        if(width<64 || height<64 || !double.IsFinite(scale) || scale<0.25 || scale>8)throw new ArgumentOutOfRangeException(nameof(width));
        var radius=Math.Clamp((int)Math.Round(18*scale),1,Math.Min(width,height)/3);
        var edge=Math.Clamp((int)Math.Round(parameters.EdgeWidth*scale),1,radius);
        return new(width,height,radius,edge,Math.Min((int)Math.Floor(parameters.Displacement*scale/Math.Sqrt(2)),Math.Min(width,height)/4));
    }
    public RefractionMapping? Map(int panelX,int panelY,RefractionBox screen,RefractionBox sourceSize)
    {
        if(screen.Width<=0 || screen.Height<=0 || sourceSize.Width<=0 || sourceSize.Height<=0)return null;
        var viewport=SourceViewport;
        var left=Math.Max(panelX+viewport.X,screen.X);var top=Math.Max(panelY+viewport.Y,screen.Y);
        var right=Math.Min(panelX+viewport.X+viewport.Width,screen.X+screen.Width);
        var bottom=Math.Min(panelY+viewport.Y+viewport.Height,screen.Y+screen.Height);
        if(right<=left || bottom<=top)return null;
        var sx=(double)sourceSize.Width/screen.Width;var sy=(double)sourceSize.Height/screen.Height;
        var dx=(double)Width/viewport.Width;var dy=(double)Height/viewport.Height;
        static int Round(double value)=>(int)Math.Round(value);
        var a=Round((left-screen.X)*sx);var b=Round((top-screen.Y)*sy);
        var c=Round((right-screen.X)*sx);var d=Round((bottom-screen.Y)*sy);
        var x=Round((left-panelX-viewport.X)*dx);var y=Round((top-panelY-viewport.Y)*dy);
        var r=Round((right-panelX-viewport.X)*dx);var h=Round((bottom-panelY-viewport.Y)*dy);
        return c>a && d>b && r>x && h>y?new(new(a,b,c-a,d-b),new(x,y,r-x,h-y)):null;
    }
}
public readonly record struct RefractionStatistics(bool Active,int NativeWindows,int LiveSources,int Relationships,int EventSubscriptions,bool TimerActive,long Updates,long SourcePixels,
    double MaximumDisplacement,double LastUpdateMilliseconds,double MaximumUpdateMilliseconds,string? Failure)
{
    public bool GpuEnhanced {get;init;}
    public long GpuDraws {get;init;}
    public long CaptureFrames {get;init;}
    public long EstimatedTextureBytes {get;init;}
}
