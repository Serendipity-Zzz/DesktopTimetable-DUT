using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace DlutTimetable.Windows.Materials;

// One bounded own-window capture surface, one retained input texture, two
// premultiplied swap-chain buffers. Frame events are coalesced; no CPU readback.
internal sealed class GpuEdgeRenderer:IDisposable
{
    internal static Action<string>? Trace {get;set;}
    private readonly object sync=new();
    private readonly Dispatcher dispatcher=Dispatcher.CurrentDispatcher;
    private readonly Action<nint> bind;
    private readonly Func<bool> sourceAvailable;
    private readonly Action failed,changed;
    private readonly int width,height;
    private readonly IDirect3DDevice captureDevice;
    private readonly Direct3D11CaptureFramePool pool;
    private readonly GraphicsCaptureSession capture;
    private readonly GraphicsCaptureItem item;
    private Direct3D11CaptureFrame? pending;
    private DispatcherTimer? deadline,startup;
    private bool queued,disposed,bound,hasTexture,processing,redraw,cleaned;
    private long lastDraw;
    private ID3D11Device? device;
    private ID3D11DeviceContext? context,renderContext;
    private ID3D11Texture2D? source,backBuffer;
    private ID3D11ShaderResourceView? sourceView;
    private ID3D11RenderTargetView? renderTarget;
    private ID3D11VertexShader? vertex;
    private ID3D11PixelShader? pixel;
    private ID3D11SamplerState? sampler;
    private ID3D11Buffer? constants;
    private IDXGISwapChain1? swapChain;
    private MaterialParameters parameters;
    public long Draws {get;private set;}
    public long Frames {get;private set;}
    public bool Active=>!disposed && bound;
    internal nint RenderedTexture=>!disposed?backBuffer?.NativePointer??0:0;
    public bool TimerActive=>deadline?.IsEnabled==true || startup?.IsEnabled==true;
    public long EstimatedTextureBytes=>(long)width*height*4*5;
    public GpuEdgeRenderer(nint ownWindow,(int Width,int Height) size,MaterialParameters parameters,
        Action<nint> bind,Func<bool> sourceAvailable,Action changed,Action failed)
    {
        this.bind=bind;this.sourceAvailable=sourceAvailable;this.changed=changed;this.failed=failed;this.parameters=parameters;
        width=size.Width;height=size.Height;
        if(width is <1 or >1200 || height is <1 or >900)throw new NotSupportedException("Bounded capture size unsupported");
        NativeMethods.GetWindowThreadProcessId(ownWindow,out var owner);
        if(owner!=Environment.ProcessId)throw new InvalidOperationException("Capture target is not owned");
        if(!GraphicsCaptureSession.IsSupported())throw new NotSupportedException("Capture unavailable");
        captureDevice=CaptureInterop.Device();
        try
        {
            item=CaptureInterop.Item(ownWindow);
            if(item.Size.Width!=width || item.Size.Height!=height)throw new NotSupportedException("Capture size changed");
            pool=Direct3D11CaptureFramePool.CreateFreeThreaded(captureDevice,DirectXPixelFormat.B8G8R8A8UIntNormalized,2,item.Size);
            try
            {
                capture=pool.CreateCaptureSession(item);capture.IsCursorCaptureEnabled=false;
                pool.FrameArrived+=OnFrame;item.Closed+=OnClosed;
                try
                {
                    startup=new DispatcherTimer(DispatcherPriority.Background,dispatcher){Interval=TimeSpan.FromSeconds(10)};
                    startup.Tick+=StartupExpired;startup.Start();capture.StartCapture();
                }
                catch{startup?.Stop();item.Closed-=OnClosed;pool.FrameArrived-=OnFrame;capture.Dispose();throw;}
            }
            catch{pool.Dispose();throw;}
        }
        catch{captureDevice.Dispose();throw;}
    }
    private void StartupExpired(object? sender,EventArgs args)
    {startup?.Stop();startup=null;if(!disposed && !bound)failed();}
    private void OnClosed(GraphicsCaptureItem sender,object args)=>dispatcher.BeginInvoke(()=>{if(!disposed)failed();});
    private void OnFrame(Direct3D11CaptureFramePool sender,object args)
    {
        Direct3D11CaptureFrame? frame=null,previous=null;var schedule=false;
        try
        {
            frame=sender.TryGetNextFrame();if(frame is null)return;
            lock(sync)
            {
                if(disposed)return;
                Frames++;previous=pending;pending=frame;frame=null;
                if(!queued){queued=true;schedule=true;}
            }
            if(schedule)dispatcher.BeginInvoke(DispatcherPriority.Background,ProcessFrame);
        }
        catch(Exception ex) when(Expected(ex))
        {dispatcher.BeginInvoke(()=>{if(!disposed)failed();});}
        finally{previous?.Dispose();frame?.Dispose();}
    }
    private void ProcessFrame()
    {
        if(disposed || processing)return;
        var remaining=TimeSpan.FromMilliseconds(1000d/30)-Stopwatch.GetElapsedTime(lastDraw);
        if(remaining>TimeSpan.Zero)
        {
            if(deadline is not null)return;
            deadline=new DispatcherTimer(DispatcherPriority.Background,dispatcher){Interval=remaining};
            deadline.Tick+=(_,_)=>{deadline?.Stop();deadline=null;ProcessFrame();};deadline.Start();return;
        }
        Direct3D11CaptureFrame? frame;bool needsDraw;
        lock(sync){frame=pending;pending=null;queued=false;needsDraw=redraw;redraw=false;}
        if(frame is null && !needsDraw)return;
        processing=true;
        try
        {
            if(disposed || !sourceAvailable())return;
            if(frame is not null)
            {
                if(frame.ContentSize.Width!=width || frame.ContentSize.Height!=height)throw new NotSupportedException("Capture size changed");
                using var input=CaptureInterop.Texture(frame.Surface);
                if(device is null)Initialize(input);
                if(disposed)return;
                context!.CopyResource(source!,input);hasTexture=true;
            }
            if(hasTexture && !disposed)Draw();
        }
        catch(Exception ex) when(Expected(ex)){failed();}
        finally
        {
            frame?.Dispose();processing=false;
            if(disposed)Cleanup();
            else
            {
                var schedule=false;
                lock(sync){if(pending is not null || redraw){queued=true;schedule=true;}}
                if(schedule)dispatcher.BeginInvoke(DispatcherPriority.Background,ProcessFrame);
            }
        }
    }
    private void Initialize(ID3D11Texture2D input)
    {
        // A frame texture's cached Device wrapper is disposed together with the
        // texture by these bindings. Retain our own native GetDevice reference.
        device=CaptureInterop.DeviceForTexture(input.NativePointer);context=device.ImmediateContext;
        renderContext=device.CreateDeferredContext();
        Trace?.Invoke($"Initialize context={context.NativePointer!=0}");
        using(var multithread=context.QueryInterface<ID3D11Multithread>())multithread.SetMultithreadProtected(true);
        Trace?.Invoke($"After multithread context={context.NativePointer!=0}");
        source=device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm,(uint)width,(uint)height,1,1,BindFlags.ShaderResource));
        sourceView=device.CreateShaderResourceView(source);
        using(var dxgi=device.QueryInterface<IDXGIDevice>())
        using(var adapter=dxgi.GetAdapter())
        using(var factory=adapter.GetParent<IDXGIFactory2>())
            swapChain=factory.CreateSwapChainForComposition(device,new SwapChainDescription1
            {Width=(uint)width,Height=(uint)height,Format=Format.B8G8R8A8_UNorm,SampleDescription=new(1,0),
             BufferUsage=Usage.RenderTargetOutput,BufferCount=2,Scaling=Scaling.Stretch,SwapEffect=SwapEffect.FlipSequential,AlphaMode=AlphaMode.Premultiplied});
        backBuffer=swapChain.GetBuffer<ID3D11Texture2D>(0);renderTarget=device.CreateRenderTargetView(backBuffer);
        vertex=device.CreateVertexShader(ShaderCode.Value.Vertex);pixel=device.CreatePixelShader(ShaderCode.Value.Pixel);
        sampler=device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear,TextureAddressMode.Clamp));
        constants=device.CreateBuffer(48,BindFlags.ConstantBuffer);
        Trace?.Invoke($"Initialized context={context.NativePointer!=0} source={source.NativePointer!=0} constants={constants.NativePointer!=0}");
    }
    public void Configure(MaterialParameters value)
    {
        if(disposed || parameters==value)return;parameters=value;
        var schedule=false;
        lock(sync){redraw=true;if(!queued){queued=true;schedule=true;}}
        if(schedule)dispatcher.BeginInvoke(DispatcherPriority.Background,ProcessFrame);
    }
    private void Draw()
    {
        if(context is null || context.NativePointer==0 || constants is null || constants.NativePointer==0)
        {
            Trace?.Invoke($"Draw invalid contextObject={context is not null} contextAlive={context is not null && context.NativePointer!=0} constantsObject={constants is not null} constantsAlive={constants is not null && constants.NativePointer!=0}");
            throw new InvalidOperationException("GPU resources unavailable");
        }
        var scale=width/780f;var angle=(float)(parameters.LightAngle*Math.PI/180);
        ReadOnlySpan<Vector4> values=[new(width,height,(float)parameters.EdgeWidth*scale,18*scale),
            new((float)parameters.Displacement*scale,(float)parameters.Dispersion,MathF.Cos(angle),MathF.Sin(angle)),
            new((float)(.22+.20*(1-parameters.BackgroundTransparency)),1.2f,0,0)];
        // WGC shares the device's immediate context. Record our complete state
        // separately and preserve that context when executing, so capture work
        // cannot leak blend/scissor state into this pass (or vice versa).
        var draw=renderContext!;
        draw.UpdateSubresource(values,constants!);
        draw.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);draw.VSSetShader(vertex!);draw.PSSetShader(pixel!);
        draw.PSSetShaderResource(0,sourceView!);draw.PSSetSampler(0,sampler!);draw.PSSetConstantBuffer(0,constants!);
        draw.OMSetRenderTargets(renderTarget!);draw.RSSetViewport(0,0,width,height);
        draw.ClearRenderTargetView(renderTarget!,new Vortice.Mathematics.Color4(0,0,0,0));draw.Draw(3,0);
        using(var commands=draw.FinishCommandList(false))
        using(var protection=context!.QueryInterface<ID3D11Multithread>())
        {
            protection.Enter();
            try{context.ExecuteCommandList(commands,true);swapChain!.Present(0,PresentFlags.None).CheckError();}
            finally{protection.Leave();}
        }
        if(!bound)
        {
            bind(swapChain.NativePointer);
            if(disposed){bind(0);return;}
            bound=true;startup?.Stop();startup=null;
        }
        Draws++;lastDraw=Stopwatch.GetTimestamp();changed();
    }
    public void Detach(){if(bound){bind(0);bound=false;}}
    public void Dispose()
    {
        lock(sync){if(disposed)return;disposed=true;pending?.Dispose();pending=null;queued=false;}
        deadline?.Stop();deadline=null;startup?.Stop();startup=null;
        item.Closed-=OnClosed;pool.FrameArrived-=OnFrame;
        // COM/D3D initialization may pump the dispatcher. A reentrant mode change
        // must not destroy a half-built renderer or let it bind after disposal.
        if(!processing)Cleanup();
    }
    private void Cleanup()
    {
        if(cleaned)return;cleaned=true;
        try{Detach();}
        finally
        {
            capture.Dispose();pool.Dispose();renderContext?.ClearState();
            constants?.Dispose();sampler?.Dispose();pixel?.Dispose();vertex?.Dispose();renderTarget?.Dispose();backBuffer?.Dispose();
            sourceView?.Dispose();source?.Dispose();swapChain?.Dispose();renderContext?.Dispose();context?.Dispose();device?.Dispose();captureDevice.Dispose();
        }
    }
    internal static bool Expected(Exception ex)=>ex is COMException or SharpGen.Runtime.SharpGenException or InvalidOperationException
        or ArgumentException or NotSupportedException or IOException or DllNotFoundException or EntryPointNotFoundException;
    private static readonly Lazy<(byte[] Vertex,byte[] Pixel)> ShaderCode=new(()=>
    {
        using var input=typeof(GpuEdgeRenderer).Assembly.GetManifestResourceStream("DlutTimetable.Windows.Materials.LiquidEdge.hlsl")??throw new InvalidDataException("Shader unavailable");
        using var reader=new StreamReader(input);var source=Encoding.UTF8.GetBytes(reader.ReadToEnd());
        return(Compile(source,"VS","vs_5_0"),Compile(source,"PS","ps_5_0"));
    });
    private static byte[] Compile(byte[] source,string entry,string profile)
    {
        nint code=0,error=0;
        try
        {
            Marshal.ThrowExceptionForHR(D3DCompile(source,(nuint)source.Length,null,0,0,entry,profile,0x8000,0,out code,out error));
            var pointer=CaptureInterop.Method<BufferPointer>(code,3)(code);var length=checked((int)CaptureInterop.Method<BufferSize>(code,4)(code));
            if(length is <1 or >1048576)throw new InvalidDataException("Shader size unsupported");
            var bytes=new byte[length];Marshal.Copy(pointer,bytes,0,length);return bytes;
        }
        finally{if(error!=0)Marshal.Release(error);if(code!=0)Marshal.Release(code);}
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate nint BufferPointer(nint self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate nuint BufferSize(nint self);
    [DllImport("d3dcompiler_47.dll",CharSet=CharSet.Ansi)]private static extern int D3DCompile(byte[] source,nuint length,string? name,nint defines,nint include,string entry,string target,uint flags,uint flags2,out nint code,out nint error);
}

internal static class CaptureInterop
{
    public static T Method<T>(nint pointer,int slot) where T:Delegate=>Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(pointer),slot*IntPtr.Size));
    public static ID3D11Texture2D Texture(IDirect3DSurface surface)
    {
        var raw=MarshalInterface<IDirect3DSurface>.FromManaged(surface);nint access=0;
        try
        {
            var iid=new Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");Marshal.ThrowExceptionForHR(Marshal.QueryInterface(raw,in iid,out access));
            iid=typeof(ID3D11Texture2D).GUID;
            Marshal.ThrowExceptionForHR(Method<GetInterface>(access,3)(access,in iid,out var texture));return new(texture);
        }
        finally{if(access!=0)Marshal.Release(access);Marshal.Release(raw);}
    }
    public static ID3D11Device DeviceForTexture(nint texture)
    {Method<GetDevice>(texture,3)(texture,out var device);return new(device);}
    public static GraphicsCaptureItem Item(nint window)
    {
        const string name="Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(name,name.Length,out var text));nint factory=0,result=0;
        try
        {
            var interop=new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");Marshal.ThrowExceptionForHR(RoGetActivationFactory(text,in interop,out factory));
            var iid=new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            Marshal.ThrowExceptionForHR(Method<CreateItem>(factory,3)(factory,window,in iid,out result));return MarshalInterface<GraphicsCaptureItem>.FromAbi(result);
        }
        finally{if(result!=0)Marshal.Release(result);if(factory!=0)Marshal.Release(factory);WindowsDeleteString(text);}
    }
    public static IDirect3DDevice Device()
    {
        nint device=0,context=0,dxgi=0,inspectable=0;
        try
        {
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(0,1,0,0x20,0,0,7,out device,out _,out context));
            var iid=new Guid("54EC77FA-1377-44E6-8C32-88FD5F44C84C");Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device,in iid,out dxgi));
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi,out inspectable));return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally{foreach(var pointer in new[]{inspectable,dxgi,context,device})if(pointer!=0)Marshal.Release(pointer);}
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate int GetInterface(nint self,in Guid iid,out nint result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate void GetDevice(nint self,out nint device);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]private delegate int CreateItem(nint self,nint window,in Guid iid,out nint item);
    [DllImport("combase.dll",CharSet=CharSet.Unicode)]private static extern int WindowsCreateString(string value,int length,out nint text);
    [DllImport("combase.dll")]private static extern int WindowsDeleteString(nint text);
    [DllImport("combase.dll")]private static extern int RoGetActivationFactory(nint name,in Guid iid,out nint factory);
    [DllImport("d3d11.dll")]private static extern int D3D11CreateDevice(nint adapter,uint driver,nint software,uint flags,nint levels,uint count,uint sdk,out nint device,out uint level,out nint context);
    [DllImport("d3d11.dll")]private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint device,out nint inspectable);
}
