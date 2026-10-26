using DlutTimetable.Domain;

namespace DlutTimetable.Windows.Materials;

internal sealed class LiveLiquidEdge:IDisposable
{
    private readonly LiquidEdgeSurface source,fallback;
    private readonly Action<nint>? bindingProbe;
    private GpuEdgeSurface? output;
    private long renderGeneration;
    private GpuEdgeRenderer? gpu;
    private MaterialParameters? parameters,gpuFailed;
    private DipRect bounds;
    private nint owner,anchor;
    private bool topmost,usingFallback,creating,reconfigure;
    private long generation;
    private (int Width,int Height) captureSize;
    public LiveLiquidEdge(Action<nint>? bindingProbe=null,nint controlledSource=0,uint controlledProcess=0)
    {
        this.bindingProbe=bindingProbe;
        source=new(true,controlledSource,controlledProcess);fallback=new(false,controlledSource,controlledProcess);
        source.Changed+=()=>{if(!source.Active)gpu?.Detach();Changed?.Invoke();};
        source.Failed+=UseFallback;fallback.Changed+=()=>Changed?.Invoke();fallback.Failed+=()=>Failed?.Invoke();
    }
    public event Action? Changed;
    public event Action? Failed;
    public nint Handle=>usingFallback?fallback.Handle:output?.Handle??source.Handle;
    internal nint CaptureHandle=>source.Handle;
    internal nint RenderedTexture=>gpu?.RenderedTexture??0;
    internal IReadOnlyList<nint> SourceWindows=>usingFallback?fallback.SourceWindows:source.SourceWindows;
    public RefractionStatistics Statistics
    {
        get
        {
            if(usingFallback)return fallback.Statistics with{Failure=fallback.Failure??"GpuUnavailable"};
            return source.Statistics with{NativeWindows=source.Statistics.NativeWindows+(output?.Handle!=0 && output is not null?1:0),Active=source.Active && gpu?.Active==true,GpuEnhanced=gpu?.Active==true,
                GpuDraws=gpu?.Draws??0,CaptureFrames=gpu?.Frames??0,EstimatedTextureBytes=gpu?.EstimatedTextureBytes??0,
                MaximumDisplacement=(parameters?.Displacement??0)*(1+(parameters?.Dispersion??0))*bounds.Width/780,
                TimerActive=source.Statistics.TimerActive || gpu?.TimerActive==true};
        }
    }
    public bool TryShow(DipRect rect,MaterialParameters value,nint owner,bool asTopmost)
    {
        bounds=rect;parameters=value;this.owner=owner;topmost=asTopmost;
        if(value.Displacement==0){Dispose();return false;}
        if(creating){reconfigure=true;return true;}
        if(gpuFailed==value){usingFallback=true;return fallback.TryShow(rect,value,owner,asTopmost);}
        usingFallback=false;fallback.Dispose();
        if(!source.TryShow(rect,value,owner,asTopmost)){UseFallback();return fallback.Active;}
        try
        {
            if(gpu is not null && captureSize!=source.CaptureSize){renderGeneration++;output?.Dispose();output=null;gpu.Dispose();gpu=null;}
            if(gpu is null)
            {
                var ticket=generation;var renderTicket=++renderGeneration;creating=true;
                captureSize=source.CaptureSize;
                try
                {
                    var created=new GpuEdgeRenderer(source.Handle,captureSize,value,chain=>Bind(chain,renderTicket),()=>source.Active,()=>Changed?.Invoke(),UseFallback);
                    if(ticket!=generation || parameters is null){created.Dispose();return false;}
                    gpu=created;
                }
                finally{creating=false;}
                if(reconfigure && parameters is {} latest)
                {reconfigure=false;return TryShow(bounds,latest,this.owner,topmost);}
            }
            else gpu.Configure(value);
            output?.Update(rect,value,owner,asTopmost);
            return true;
        }
        catch(Exception ex) when(GpuEdgeRenderer.Expected(ex)){UseFallback();return fallback.Active;}
    }
    private void Bind(nint swapChain,long ticket)
    {
        if(ticket!=renderGeneration)return;
        if(swapChain==0){output?.Dispose();output=null;return;}
        if(parameters is not {} current)return;
        bindingProbe?.Invoke(swapChain);
        var created=new GpuEdgeSurface(swapChain,captureSize,bounds,current,owner,topmost);
        if(ticket!=renderGeneration || parameters is null){created.Dispose();return;}
        output?.Dispose();output=created;if(anchor!=0)output.PlaceBehind(anchor);
    }
    public void PlaceBehind(nint foreground)
    {anchor=foreground;if(usingFallback)fallback.PlaceBehind(foreground);else{source.PlaceBehind(foreground);output?.PlaceBehind(foreground);}}

    private void UseFallback()
    {
        if(parameters is null)return;
        gpuFailed=parameters;usingFallback=true;renderGeneration++;output?.Dispose();output=null;
        var previous=gpu;gpu=null;previous?.Dispose();source.Dispose();
        fallback.TryShow(bounds,parameters,owner,topmost);
        if(anchor!=0)fallback.PlaceBehind(anchor);
        Changed?.Invoke();
    }
    public void ResetFailure(){gpuFailed=null;source.ResetFailure();fallback.ResetFailure();}
    public void Dispose()
    {
        generation++;renderGeneration++;parameters=null;reconfigure=false;output?.Dispose();output=null;
        var previous=gpu;gpu=null;
        try{previous?.Dispose();}
        finally{source.Dispose();fallback.Dispose();anchor=0;}
    }
}
