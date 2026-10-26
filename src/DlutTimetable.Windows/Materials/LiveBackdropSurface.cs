using System.Numerics;
using System.Runtime.InteropServices;
using DlutTimetable.Domain;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace DlutTimetable.Windows.Materials;

/// <summary>One compositor-owned live background. Never reads a wallpaper file or captures the screen.</summary>
internal sealed class LiveBackdropSurface : IDisposable
{
    [ThreadStatic]private static object? dispatcherQueue;
    private Compositor? compositor;
    private DesktopWindowTarget? target;
    private SpriteVisual? backdrop;
    private CompositionRoundedRectangleGeometry? geometry;
    private CompositionColorBrush? tint;
    private ContainerVisual? root,viewport;
    private SpriteVisual? veil;
    private CompositionGeometricClip? roundedClip;
    private InsetClip? revealClip;
    private Vector2 size;
    private (int Width,int Height,int Radius) windowShape;
    private (DipRect Bounds,nint Owner,bool Topmost)? placement;
    public nint Handle { get; private set; }
    public bool Visible { get; private set; }
    private void SetShape(DipRect rect,double radius,double opacity)
    {
        var width=(int)Math.Round(rect.Width);var height=(int)Math.Round(rect.Height);
        var nextSize=new Vector2(width,height);
        if(size!=nextSize){size=nextSize;geometry!.Size=size;}
        var corners=new Vector2((float)Math.Clamp(radius,0,Math.Min(width,height)/2d));
        if(geometry!.CornerRadius!=corners)geometry.CornerRadius=corners;
        var shape=(width,height,(int)Math.Round(corners.X));
        if(windowShape!=shape)
        {
            // A composition clip only clips visuals. The STATIC host still paints its
            // rectangular client background outside that clip unless the HWND is clipped too.
            var region=CreateRoundRectRgn(0,0,width+1,height+1,shape.Item3*2,shape.Item3*2);
            if(region==0)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            if(SetWindowRgn(Handle,region,true)==0)
            {DeleteObject(region);throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());}
            // Windows owns region after a successful SetWindowRgn.
            windowShape=shape;
        }
        var alpha=(float)Math.Clamp(opacity,0,1);
        if(root!.Opacity!=alpha)root.Opacity=alpha;
    }

    public void Show(DipRect rect, MaterialParameters parameters, double cornerScale, nint owner, bool topmost)
    {
        Ensure();
        var width=(int)Math.Round(rect.Width);var height=(int)Math.Round(rect.Height);
        SetShape(rect,18*cornerScale,1);
        var alpha=(byte)Math.Round(255*(1-parameters.BackgroundTransparency));
        var color=parameters.Dark ? global::Windows.UI.Color.FromArgb(alpha,15,19,28) : global::Windows.UI.Color.FromArgb(alpha,255,255,255);
        if(!tint!.Color.Equals(color))tint.Color=color;
        if(placement!=(rect,owner,topmost) || !Visible)
        {
            NativeMethods.SetLong(Handle,-8,owner);
            NativeMethods.Check(NativeMethods.SetWindowPos(Handle,topmost?-1:-2,(int)Math.Round(rect.X),(int)Math.Round(rect.Y),width,height,
                NativeMethods.NoActivate | 0x40),"Position live backdrop");
            placement=(rect,owner,topmost);
        }
        Visible=true;
    }
    public void FollowMotion(BallMotionPose pose)
    {
        if(!Visible)return;
        placement=null;
        SetShape(pose.Bounds,pose.Radius,pose.TextureOpacity);
        NativeMethods.Check(NativeMethods.SetWindowPos(Handle,0,(int)Math.Round(pose.Bounds.X),(int)Math.Round(pose.Bounds.Y),
            (int)Math.Round(pose.Bounds.Width),(int)Math.Round(pose.Bounds.Height),NativeMethods.NoActivate|NativeMethods.NoZOrder),
            "Follow own texture motion with live glass");
    }
    private void Ensure()
    {
        if(Handle!=0)return;
        if(!OperatingSystem.IsWindowsVersionAtLeast(10,0,22000))throw new NotSupportedException("Live backdrop requires Windows 11.");
        try
        {
            EnsureDispatcherQueue();
            // A separate native layered HWND keeps real cross-process mouse pass-through.
            // WPF's AllowsTransparency HWND cannot also own this DesktopWindowTarget.
            Handle=CreateWindowEx(0x080800A0,"STATIC","大工课表 · 实时毛玻璃",unchecked((int)0x80000000),0,0,1,1,0,0,0,0);
            if(Handle==0)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            if(!SetLayeredWindowAttributes(Handle,0,255,2))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var enabled=1;
            Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(Handle,17,ref enabled,4));
            compositor=new();
            compositor.As<ICompositorDesktopInterop>().CreateDesktopWindowTarget(Handle,false,out var raw);
            try { target=MarshalInterface<DesktopWindowTarget>.FromAbi(raw); }
            finally { Marshal.Release(raw); }
            root=compositor.CreateContainerVisual();root.RelativeSizeAdjustment=Vector2.One;
            geometry=compositor.CreateRoundedRectangleGeometry();
            roundedClip=compositor.CreateGeometricClip(geometry);root.Clip=roundedClip;
            viewport=compositor.CreateContainerVisual();viewport.RelativeSizeAdjustment=Vector2.One;
            revealClip=compositor.CreateInsetClip();viewport.Clip=revealClip;root.Children.InsertAtTop(viewport);
            backdrop=compositor.CreateSpriteVisual();backdrop.RelativeSizeAdjustment=Vector2.One;
            backdrop.Brush=compositor.CreateHostBackdropBrush();viewport.Children.InsertAtBottom(backdrop);
            tint=compositor.CreateColorBrush();
            veil=compositor.CreateSpriteVisual();veil.RelativeSizeAdjustment=Vector2.One;veil.Brush=tint;
            viewport.Children.InsertAtTop(veil);target.Root=root;
        }
        catch { Dispose();throw; }
    }
    public void Reveal(double left=0,double top=0,double right=1,double bottom=1)
    {
        if(revealClip is null)return;
        revealClip.LeftInset=(float)Math.Clamp(left,0,1)*size.X;revealClip.TopInset=(float)Math.Clamp(top,0,1)*size.Y;
        revealClip.RightInset=(float)(1-Math.Clamp(right,0,1))*size.X;revealClip.BottomInset=(float)(1-Math.Clamp(bottom,0,1))*size.Y;
    }
    internal static void EnsureDispatcherQueue()
    {
        if(dispatcherQueue is null)Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(new(12,2,2),out dispatcherQueue));
    }
    public void Hide()
    {
        if(Handle!=0)ShowWindow(Handle,0);Visible=false;
    }
    public void Dispose()
    {
        Visible=false;
        if(target is not null){target.Root=null;target.Dispose();target=null;}
        backdrop?.Brush?.Dispose();backdrop?.Dispose();backdrop=null;
        veil?.Dispose();veil=null;viewport?.Dispose();viewport=null;root?.Dispose();root=null;
        revealClip?.Dispose();revealClip=null;roundedClip?.Dispose();roundedClip=null;
        geometry?.Dispose();geometry=null;tint?.Dispose();tint=null;
        compositor?.Dispose();compositor=null;size=default;windowShape=default;placement=null;
        if(Handle!=0){DestroyWindow(Handle);Handle=0;}
    }
    [StructLayout(LayoutKind.Sequential)]private record struct QueueOptions(int Size,int ThreadType,int ApartmentType);
    [DllImport("coremessaging.dll")]private static extern int CreateDispatcherQueueController(QueueOptions options,[MarshalAs(UnmanagedType.IUnknown)]out object controller);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern nint CreateWindowEx(int exStyle,string className,string title,int style,int x,int y,int width,int height,nint parent,nint menu,nint instance,nint parameter);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool SetLayeredWindowAttributes(nint hwnd,uint key,byte alpha,uint flags);
    [DllImport("user32.dll")]private static extern bool ShowWindow(nint hwnd,int command);
    [DllImport("user32.dll")]private static extern bool DestroyWindow(nint hwnd);
    [DllImport("dwmapi.dll")]private static extern int DwmSetWindowAttribute(nint hwnd,int attribute,ref int value,int size);
    [DllImport("gdi32.dll",SetLastError=true)]private static extern nint CreateRoundRectRgn(int left,int top,int right,int bottom,int width,int height);
    [DllImport("user32.dll",SetLastError=true)]private static extern int SetWindowRgn(nint hwnd,nint region,bool redraw);
    [DllImport("gdi32.dll")]private static extern bool DeleteObject(nint value);
    [ComImport,Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorDesktopInterop
    {
        void CreateDesktopWindowTarget(nint hwnd,[MarshalAs(UnmanagedType.Bool)]bool topmost,out nint target);
        void EnsureOnThread(uint threadId);
    }
}
