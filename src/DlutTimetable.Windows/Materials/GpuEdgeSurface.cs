using System.Numerics;
using System.Runtime.InteropServices;
using DlutTimetable.Domain;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace DlutTimetable.Windows.Materials;

// A separate hollow compositor target. Neither its HWND region nor its four
// visual rectangles cover the center; the HostBackdrop target stays independent.
internal sealed class GpuEdgeSurface:IDisposable
{
    private readonly Compositor compositor;
    private readonly DesktopWindowTarget target;
    private readonly ContainerVisual root;
    private readonly CompositionRoundedRectangleGeometry geometry;
    private readonly CompositionGeometricClip clip;
    private readonly ICompositionSurface surface;
    private readonly List<(SpriteVisual Visual,CompositionSurfaceBrush Brush)> sides=[];
    private readonly (int Width,int Height) sourceSize;
    private (int Width,int Height,int Radius,int Edge) shape;
    private DipRect bounds;
    private nint owner;
    private bool topmost,disposed;
    public nint Handle {get;private set;}
    public GpuEdgeSurface(nint swapChain,(int Width,int Height) sourceSize,DipRect bounds,MaterialParameters parameters,nint owner,bool topmost)
    {
        this.sourceSize=sourceSize;LiveBackdropSurface.EnsureDispatcherQueue();
        compositor=new();
        try
        {
            Handle=CreateWindowEx(0x082800A0,"STATIC","大工课表 · GPU 边缘",unchecked((int)0x80000000),0,0,1,1,0,0,0,0);
            if(Handle==0)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            NativeMethods.Check(SetLayeredWindowAttributes(Handle,0,255,2),"Prepare GPU edge");
            compositor.As<ICompositorDesktopInterop>().CreateDesktopWindowTarget(Handle,false,out var raw);
            try{target=MarshalInterface<DesktopWindowTarget>.FromAbi(raw);}finally{Marshal.Release(raw);}
            root=compositor.CreateContainerVisual();root.RelativeSizeAdjustment=Vector2.One;
            geometry=compositor.CreateRoundedRectangleGeometry();clip=compositor.CreateGeometricClip(geometry);root.Clip=clip;
            compositor.As<ICompositorInterop>().CreateCompositionSurfaceForSwapChain(swapChain,out raw);
            try{surface=MarshalInterface<ICompositionSurface>.FromAbi(raw);}finally{Marshal.Release(raw);}
            target.Root=root;Update(bounds,parameters,owner,topmost);
        }
        catch{Dispose();throw;}
    }
    public void Update(DipRect rect,MaterialParameters parameters,nint nextOwner,bool asTopmost)
    {
        if(disposed)return;
        var width=(int)Math.Round(rect.Width);var height=(int)Math.Round(rect.Height);var scale=rect.Width/780;
        var next=(width,height,(int)Math.Round(18*scale),(int)Math.Round(parameters.EdgeWidth*scale));
        if(shape!=next)
        {
            shape=next;geometry.Size=new(width,height);geometry.CornerRadius=new(shape.Radius,shape.Radius);
            var outside=CreateRoundRectRgn(0,0,width+1,height+1,shape.Radius*2,shape.Radius*2);
            var e=shape.Edge;var inside=CreateRoundRectRgn(e,e,width-e+1,height-e+1,Math.Max(0,shape.Radius-e)*2,Math.Max(0,shape.Radius-e)*2);
            if(outside==0 || inside==0){if(outside!=0)DeleteObject(outside);if(inside!=0)DeleteObject(inside);throw new System.ComponentModel.Win32Exception();}
            try
            {
                if(CombineRgn(outside,outside,inside,4)==0)throw new System.ComponentModel.Win32Exception();
                if(SetWindowRgn(Handle,outside,true)==0)throw new System.ComponentModel.Win32Exception();
                outside=0;
            }
            finally{if(outside!=0)DeleteObject(outside);DeleteObject(inside);}
            ClearSides();
            Add(0,0,width,e);Add(0,height-e,width,e);Add(0,e,e,height-2*e);Add(width-e,e,e,height-2*e);
            void Add(int x,int y,int w,int h)
            {
                var brush=compositor.CreateSurfaceBrush(surface);brush.Stretch=CompositionStretch.None;
                brush.HorizontalAlignmentRatio=0;brush.VerticalAlignmentRatio=0;
                brush.TransformMatrix=Matrix3x2.CreateScale((float)width/sourceSize.Width,(float)height/sourceSize.Height)*Matrix3x2.CreateTranslation(-x,-y);
                var visual=compositor.CreateSpriteVisual();visual.Offset=new(x,y,0);visual.Size=new(w,h);visual.Brush=brush;
                sides.Add((visual,brush));root.Children.InsertAtTop(visual);
            }
        }
        if(bounds!=rect || owner!=nextOwner || topmost!=asTopmost)
        {
            bounds=rect;owner=nextOwner;topmost=asTopmost;NativeMethods.SetLong(Handle,-8,owner);
            NativeMethods.Check(NativeMethods.SetWindowPos(Handle,topmost?-1:-2,(int)Math.Round(rect.X),(int)Math.Round(rect.Y),width,height,NativeMethods.NoActivate|0x40),"Position GPU edge");
        }
    }
    public void PlaceBehind(nint foreground)
    {
        if(disposed || Handle==0)return;
        NativeMethods.Check(NativeMethods.SetWindowPos(Handle,foreground,0,0,0,0,NativeMethods.NoActivate|NativeMethods.NoMove|NativeMethods.NoSize),"Place GPU edge behind ink");
    }
    private void ClearSides()
    {
        root?.Children.RemoveAll();
        foreach(var item in sides){item.Visual.Brush=null;item.Visual.Dispose();item.Brush.Dispose();}sides.Clear();
    }
    public void Dispose()
    {
        if(disposed)return;disposed=true;
        if(target is not null)target.Root=null;ClearSides();
        clip?.Dispose();geometry?.Dispose();root?.Dispose();target?.Dispose();
        if(surface is IWinRTObject owned)owned.NativeObject.Dispose();compositor.Dispose();
        if(Handle!=0){DestroyWindow(Handle);Handle=0;}
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern nint CreateWindowEx(int exStyle,string className,string title,int style,int x,int y,int width,int height,nint parent,nint menu,nint instance,nint parameter);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool SetLayeredWindowAttributes(nint window,uint key,byte alpha,uint flags);
    [DllImport("user32.dll")]private static extern bool DestroyWindow(nint window);
    [DllImport("gdi32.dll")]private static extern nint CreateRoundRectRgn(int left,int top,int right,int bottom,int width,int height);
    [DllImport("gdi32.dll")]private static extern int CombineRgn(nint result,nint first,nint second,int operation);
    [DllImport("gdi32.dll")]private static extern bool DeleteObject(nint region);
    [DllImport("user32.dll")]private static extern int SetWindowRgn(nint window,nint region,bool redraw);
    [ComImport,Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorDesktopInterop
    {void CreateDesktopWindowTarget(nint window,[MarshalAs(UnmanagedType.Bool)]bool topmost,out nint target);void EnsureOnThread(uint thread);}
    [ComImport,Guid("25297D5C-3AD4-4C9C-B5CF-E36A38512330"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICompositorInterop
    {void CreateCompositionSurfaceForHandle(nint handle,out nint surface);void CreateCompositionSurfaceForSwapChain(nint chain,out nint surface);void CreateGraphicsDevice(nint device,out nint graphicsDevice);}
}
