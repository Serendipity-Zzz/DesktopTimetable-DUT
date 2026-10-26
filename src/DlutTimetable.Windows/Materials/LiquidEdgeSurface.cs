using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using DlutTimetable.Domain;

namespace DlutTimetable.Windows.Materials;

/// <summary>One hollow native surface; DWM updates bounded live window crops without a frame timer or CPU pixel copies.</summary>
internal sealed class LiquidEdgeSurface:IDisposable
{
    private readonly bool captureOnly;
    private readonly nint controlledSource;
    private readonly uint controlledProcess;
    private sealed record Source(nint Window,List<nint> Thumbnails,uint ProcessId);
    private readonly record struct Piece(RefractionBox Box,int Band);
    private readonly List<Source> sources=[];
    private readonly Dispatcher dispatcher=Dispatcher.CurrentDispatcher;
    private readonly NativeMethods.WindowEventCallback callback;
    private RefractionLayout? layout;
    private Piece[] pieces=[];
    private RefractionLayout[] profiles=[];
    private MaterialParameters? failedParameters,currentParameters;
    private DipRect bounds;
    private nint anchor,geometryHook,foregroundHook,sourceOwner;
    private bool topmost,queued;
    private DispatcherTimer? pendingUpdate;
    private long lastUpdateAt;
    private long generation,updates,sourcePixels;
    private double lastUpdate,maximumUpdate;
    public nint Handle{get;private set;}
    public bool Active=>Handle!=0 && sourcePixels>0;
    public string? Failure{get;private set;}
    public event Action? Failed;
    public event Action? Changed;
    public RefractionStatistics Statistics=>new(Active,Handle==0?0:1,sources.Count,sources.Sum(s=>s.Thumbnails.Count),(geometryHook!=0?1:0)+(foregroundHook!=0?1:0),pendingUpdate?.IsEnabled==true,updates,sourcePixels,profiles.Length>0?profiles[0].MaximumDisplacement:0,lastUpdate,maximumUpdate,Failure);
    public (int Width,int Height) CaptureSize {get;private set;}
    internal IReadOnlyList<nint> SourceWindows=>sources.Select(s=>s.Window).ToArray();
    public LiquidEdgeSurface(bool captureOnly=false,nint controlledSource=0,uint controlledProcess=0)
    {
        this.captureOnly=captureOnly;
        this.controlledSource=controlledSource;this.controlledProcess=controlledProcess;
        callback=(_,kind,window,objectId,child,_,_)=>
        {
            if(Handle==0 || window==0 || objectId!=0 || child!=0 || kind!=3 && kind is not (0x8001 or 0x8002 or 0x8003 or 0x800B))return;
            if(kind!=3 && GetAncestor(window,2)!=window && !sources.Any(s=>s.Window==window))return;
            if(!sources.Any(s=>s.Window==window))
            {
                if(!NativeMethods.GetWindowRect(window,out var rect) || rect.Right<=bounds.X || rect.Bottom<=bounds.Y || rect.Left>=bounds.X+bounds.Width || rect.Top>=bounds.Y+bounds.Height)return;
            }
            QueueUpdate();
        };
    }
    public bool TryShow(DipRect rect,MaterialParameters parameters,nint owner,bool asTopmost)
    {
        if(parameters.Displacement==0){Dispose();return false;}
        if(failedParameters==parameters)return false;
        if(captureOnly && Handle!=0 && NativeMethods.IsWindow(Handle) && layout is not null && bounds==rect && sourceOwner==owner && topmost==asTopmost)
        {currentParameters=parameters;return true;}
        try
        {
            if(captureOnly)anchor=0;
            currentParameters=parameters;
            var next=RefractionLayout.Create((int)Math.Round(rect.Width),(int)Math.Round(rect.Height),rect.Width/780,parameters);
            var resized=layout is null || layout.Width!=next.Width || layout.Height!=next.Height || layout.Radius!=next.Radius || layout.EdgeWidth!=next.EdgeWidth;
            var modeChanged=Handle!=0 && (topmost!=asTopmost || sourceOwner!=owner);
            if(Handle==0)
            {
                Handle=CreateWindowEx(captureOnly?0x080000A0:0x080800A0,"STATIC",captureOnly?"大工课表 · 局部背景源":"大工课表 · 边缘折射",unchecked((int)0x80000000),0,0,1,1,0,0,0,0);
                if(Handle==0)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                if(!captureOnly)NativeMethods.Check(SetLayeredWindowAttributes(Handle,0,0,2),"Prepare edge surface");
            }
            bounds=rect;layout=next;topmost=asTopmost;sourceOwner=owner;
            profiles=captureOnly?[next with{Inset=0}]:Enumerable.Range(0,3).Select(band=>next.AtDepth((band+0.5)/3)).ToArray();
            if(resized)
            {
                if(captureOnly)pieces=[new(new(0,0,next.Width,next.Height),0)];
                else
                {
                var list=new List<Piece>();
                for(var band=0;band<3;band++)
                {
                    var region=Ring(next,band*next.EdgeWidth/3,(band+1)*next.EdgeWidth/3);
                    try{list.AddRange(ReadRegion(region).Select(box=>new Piece(box,band)));}finally{DeleteObject(region);}
                }
                pieces=list.ToArray();SetRegion(Ring(next,0,next.EdgeWidth));
                }
                ClearSources();
            }
            var density=Math.Min(1,Math.Min(1200d/next.Width,900d/next.Height));
            CaptureSize=captureOnly?((int)Math.Ceiling(next.Width*density),(int)Math.Ceiling(next.Height*density)):(next.Width,next.Height);
            NativeMethods.SetLong(Handle,-8,captureOnly?0:owner);
            NativeMethods.Check(NativeMethods.SetWindowPos(Handle,!captureOnly && topmost?-1:-2,
                captureOnly?GetSystemMetrics(76)-3000:(int)Math.Round(rect.X),captureOnly?GetSystemMetrics(77)-3000:(int)Math.Round(rect.Y),
                CaptureSize.Width,CaptureSize.Height,NativeMethods.NoActivate|0x40),"Position edge surface");
            if(geometryHook==0 || modeChanged)Hook();
            Failure=null;return true;
        }
        catch(Exception ex) when(IsNativeFailure(ex)){Fail(ex);return false;}
    }
    public void PlaceBehind(nint foreground)
    {
        if(Handle==0)return;
        if(captureOnly && anchor==foreground)return;
        try
        {
            anchor=foreground;
            if(!captureOnly)NativeMethods.Check(NativeMethods.SetWindowPos(Handle,foreground,0,0,0,0,NativeMethods.NoActivate|NativeMethods.NoMove|NativeMethods.NoSize),"Place edge behind ink");
            QueueUpdate();
        }
        catch(Exception ex) when(IsNativeFailure(ex)){Fail(ex);}
    }
    private void Hook()
    {
        Unhook();NativeMethods.GetWindowThreadProcessId(NativeMethods.FindWindow("Progman",null),out var process);
        if(controlledSource!=0)process=controlledProcess;
        if(process==0)throw new InvalidOperationException("Shell source unavailable");
        // Geometry/visibility only; no mouse/keyboard hook. Own-window events are skipped.
        geometryHook=NativeMethods.SetWinEventHook(0x8000,0x800B,0,callback,process,0,2);
        if(geometryHook==0)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    private void QueueUpdate()
    {
        if(queued)return;queued=true;var ticket=generation;
        dispatcher.BeginInvoke(DispatcherPriority.Background,()=>
        {
            if(ticket!=generation || Handle==0)return;
            var remaining=TimeSpan.FromMilliseconds(1000d/30)-Stopwatch.GetElapsedTime(lastUpdateAt);
            if(remaining>TimeSpan.Zero)
            {
                // One event deadline only. No recurring timer while the background is static.
                var timer=new DispatcherTimer(DispatcherPriority.Background,dispatcher){Interval=remaining};
                pendingUpdate=timer;EventHandler? tick=null;
                tick=(_,_)=>{timer.Stop();timer.Tick-=tick;if(pendingUpdate==timer)pendingUpdate=null;CompleteUpdate(ticket);};
                timer.Tick+=tick;timer.Start();
            }
            else CompleteUpdate(ticket);
        });
    }
    private void CompleteUpdate(long ticket)
    {
        if(ticket!=generation || Handle==0)return;queued=false;
        try{RefreshSources();}catch(Exception ex) when(IsNativeFailure(ex)){Fail(ex);}
    }
    private void RefreshSources()
    {
        if(layout is null || Handle==0 || anchor==0)return;
        var began=Stopwatch.GetTimestamp();lastUpdateAt=began;var candidates=new List<nint>();var behind=false;
        NativeMethods.GetWindowThreadProcessId(NativeMethods.FindWindow("Progman",null),out var shell);
        NativeMethods.EnumWindows((window,_)=>
        {
            if(window==anchor){behind=true;return true;}
            if(!behind || !NativeMethods.IsWindowVisible(window) || IsIconic(window))return true;
            NativeMethods.GetWindowThreadProcessId(window,out var process);
            if(process==Environment.ProcessId)return true;
            if(controlledSource!=0)
            {if(window!=controlledSource || process!=controlledProcess)return true;}
            else
            {
                if(process!=shell)return true;
                var name=new System.Text.StringBuilder(64);NativeMethods.GetClassName(window,name,64);
                if(name.ToString() is not ("Progman" or "WorkerW"))return true;
            }
            if(DwmGetWindowAttribute(window,14,out var cloaked,4)==0 && cloaked!=0)return true;
            if(!NativeMethods.GetWindowRect(window,out var rect))return true;
            var screen=new RefractionBox(rect.Left,rect.Top,rect.Right-rect.Left,rect.Bottom-rect.Top);
            if(layout.Map((int)Math.Round(bounds.X),(int)Math.Round(bounds.Y),screen,new(0,0,screen.Width,screen.Height)) is not null)candidates.Add(window);
            return true;
        },0);
        if(candidates.Count>16 || candidates.Count*pieces.Length>1024)throw new NotSupportedException("Live edge source graph exceeds its bounded complexity");
        candidates.Reverse();
        if(!sources.Select(s=>s.Window).SequenceEqual(candidates))
        {
            ClearSources();
            foreach(var window in candidates)
            {
                NativeMethods.GetWindowThreadProcessId(window,out var process);
                var item=new Source(window,[],process);sources.Add(item);
                foreach(var piece in pieces)
                {Marshal.ThrowExceptionForHR(DwmRegisterThumbnail(Handle,window,out var thumbnail));item.Thumbnails.Add(thumbnail);}
            }
        }
        sourcePixels=0;
        foreach(var source in sources)
        {
            NativeMethods.GetWindowThreadProcessId(source.Window,out var process);
            if(process!=source.ProcessId || !NativeMethods.IsWindowVisible(source.Window) || IsIconic(source.Window) || !NativeMethods.GetWindowRect(source.Window,out var rect))
            {ClearSources();QueueUpdate();return;}
            Marshal.ThrowExceptionForHR(DwmQueryThumbnailSourceSize(source.Thumbnails[0],out var size));
            for(var i=0;i<pieces.Length;i++)
            {
                var profile=profiles[pieces[i].Band];
                var depthMapping=profile.Map((int)Math.Round(bounds.X),(int)Math.Round(bounds.Y),new(rect.Left,rect.Top,rect.Right-rect.Left,rect.Bottom-rect.Top),new(0,0,size.Width,size.Height));
                var clipped=depthMapping is {} full?Clip(full,pieces[i].Box):null;
                var properties=new ThumbnailProperties{Flags=clipped is null?8u:31u,Visible=clipped is not null,Opacity=255};
                if(clipped is {} map)
                {
                    properties.Source=Rect(map.Source);properties.Destination=Rect(map.Destination);
                    if(captureOnly)
                    {
                        var destination=properties.Destination;
                        properties.Destination=new(){Left=(int)Math.Round((double)destination.Left/layout.Width*CaptureSize.Width),
                            Top=(int)Math.Round((double)destination.Top/layout.Height*CaptureSize.Height),
                            Right=(int)Math.Round((double)destination.Right/layout.Width*CaptureSize.Width),
                            Bottom=(int)Math.Round((double)destination.Bottom/layout.Height*CaptureSize.Height)};
                    }
                    sourcePixels+=(long)map.Source.Width*map.Source.Height;
                }
                Marshal.ThrowExceptionForHR(DwmUpdateThumbnailProperties(source.Thumbnails[i],ref properties));
            }
        }
        if(!captureOnly)NativeMethods.Check(SetLayeredWindowAttributes(Handle,0,sourcePixels>0?(byte)Math.Round(255*(0.22+0.20*(1-currentParameters!.BackgroundTransparency))):(byte)0,2),"Blend live edge");
        updates++;lastUpdate=Stopwatch.GetElapsedTime(began).TotalMilliseconds;maximumUpdate=Math.Max(maximumUpdate,lastUpdate);
        Failure=sourcePixels==0?"NoVisibleSource":null;Changed?.Invoke();
    }
    private static NativeMethods.Rect Rect(RefractionBox box)=>new(){Left=box.X,Top=box.Y,Right=box.X+box.Width,Bottom=box.Y+box.Height};
    private static RefractionMapping? Clip(RefractionMapping full,RefractionBox piece)
    {
        var d=full.Destination;var s=full.Source;
        var x=Math.Max(d.X,piece.X);var y=Math.Max(d.Y,piece.Y);var right=Math.Min(d.X+d.Width,piece.X+piece.Width);var bottom=Math.Min(d.Y+d.Height,piece.Y+piece.Height);
        if(right<=x || bottom<=y)return null;
        var sx=(double)s.Width/d.Width;var sy=(double)s.Height/d.Height;
        var a=(int)Math.Floor(s.X+(x-d.X)*sx);var b=(int)Math.Floor(s.Y+(y-d.Y)*sy);
        var c=(int)Math.Ceiling(s.X+(right-d.X)*sx);var e=(int)Math.Ceiling(s.Y+(bottom-d.Y)*sy);
        return new(new(a,b,c-a,e-b),new(x,y,right-x,bottom-y));
    }
    private static RefractionBox[] ReadRegion(nint region)
    {
        var bytes=GetRegionData(region,0,null);if(bytes<32 || bytes>65536)throw new NotSupportedException("Edge region exceeds bounded geometry");
        var data=new byte[bytes];if(GetRegionData(region,bytes,data)!=bytes)throw new System.ComponentModel.Win32Exception();
        var count=BitConverter.ToInt32(data,8);if(count<=0 || count>256 || 32+count*16>data.Length)throw new NotSupportedException("Edge region exceeds bounded rectangles");
        var result=new RefractionBox[count];
        for(var i=0;i<count;i++)
        {var start=32+i*16;var x=BitConverter.ToInt32(data,start);var y=BitConverter.ToInt32(data,start+4);result[i]=new(x,y,BitConverter.ToInt32(data,start+8)-x,BitConverter.ToInt32(data,start+12)-y);}
        return result;
    }
    private static bool IsNativeFailure(Exception ex)=>ex is System.ComponentModel.Win32Exception or COMException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException or ArgumentException or InvalidOperationException;
    private void Fail(Exception ex)
    {var parameters=currentParameters;Dispose();failedParameters=parameters;Failure=ex.GetType().Name;Failed?.Invoke();}
    private static nint Ring(RefractionLayout plan,int outerInset,int innerInset)
    {
        var r=Math.Max(0,plan.Radius-outerInset);
        var outer=CreateRoundRectRgn(outerInset,outerInset,plan.Width-outerInset+1,plan.Height-outerInset+1,r*2,r*2);
        var e=innerInset;r=Math.Max(0,plan.Radius-e);
        var inner=CreateRoundRectRgn(e,e,plan.Width-e+1,plan.Height-e+1,r*2,r*2);
        if(outer==0 || inner==0){if(outer!=0)DeleteObject(outer);if(inner!=0)DeleteObject(inner);throw new System.ComponentModel.Win32Exception();}
        try{if(CombineRgn(outer,outer,inner,4)==0){DeleteObject(outer);throw new System.ComponentModel.Win32Exception();}}
        finally{DeleteObject(inner);}return outer;
    }
    private void SetRegion(nint region)
    {if(SetWindowRgn(Handle,region,true)==0){DeleteObject(region);throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());}}
    private void ClearSources()
    {foreach(var source in sources)foreach(var thumbnail in source.Thumbnails)DwmUnregisterThumbnail(thumbnail);sources.Clear();sourcePixels=0;if(Handle!=0 && !captureOnly)SetLayeredWindowAttributes(Handle,0,0,2);}
    private void Unhook()
    {if(geometryHook!=0)NativeMethods.UnhookWinEvent(geometryHook);if(foregroundHook!=0)NativeMethods.UnhookWinEvent(foregroundHook);geometryHook=foregroundHook=0;}
    public void ResetFailure(){failedParameters=null;Failure=null;}
    public void Dispose()
    {generation++;queued=false;pendingUpdate?.Stop();pendingUpdate=null;Unhook();ClearSources();if(Handle!=0){DestroyWindow(Handle);Handle=0;}layout=null;pieces=[];profiles=[];sourcePixels=0;anchor=sourceOwner=0;}
    [StructLayout(LayoutKind.Sequential)]private struct ThumbnailSize{public int Width,Height;}
    [StructLayout(LayoutKind.Sequential)]private struct ThumbnailProperties
    {public uint Flags;public NativeMethods.Rect Destination,Source;public byte Opacity;[MarshalAs(UnmanagedType.Bool)]public bool Visible;[MarshalAs(UnmanagedType.Bool)]public bool ClientOnly;}
    [DllImport("dwmapi.dll")]private static extern int DwmRegisterThumbnail(nint target,nint source,out nint thumbnail);
    [DllImport("dwmapi.dll")]private static extern int DwmQueryThumbnailSourceSize(nint thumbnail,out ThumbnailSize size);
    [DllImport("dwmapi.dll")]private static extern int DwmUpdateThumbnailProperties(nint thumbnail,ref ThumbnailProperties properties);
    [DllImport("dwmapi.dll")]private static extern int DwmUnregisterThumbnail(nint thumbnail);
    [DllImport("dwmapi.dll")]private static extern int DwmGetWindowAttribute(nint window,int attribute,out int value,int bytes);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern nint CreateWindowEx(int exStyle,string className,string title,int style,int x,int y,int width,int height,nint parent,nint menu,nint instance,nint parameter);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool SetLayeredWindowAttributes(nint window,uint key,byte alpha,uint flags);
    [DllImport("user32.dll")]private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")]private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")]private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")]private static extern nint GetAncestor(nint window,uint flags);
    [DllImport("gdi32.dll")]private static extern nint CreateRoundRectRgn(int left,int top,int right,int bottom,int width,int height);
    [DllImport("gdi32.dll")]private static extern int CombineRgn(nint result,nint first,nint second,int mode);
    [DllImport("gdi32.dll")]private static extern uint GetRegionData(nint region,uint bytes,[Out]byte[]? data);
    [DllImport("gdi32.dll")]private static extern bool DeleteObject(nint region);
    [DllImport("user32.dll",SetLastError=true)]private static extern int SetWindowRgn(nint window,nint region,bool redraw);
}
