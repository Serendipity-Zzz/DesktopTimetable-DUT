using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using DlutTimetable.Domain;
using Microsoft.Win32;

namespace DlutTimetable.Windows.Materials;

/// <summary>UI-thread coordinator. One bounded render at a time; latest movement wins.</summary>
public sealed class MaterialController : IDisposable
{
    private readonly TimetableView view;
    private readonly WallpaperCache cache = new();
    private WallpaperDescriptor? descriptor;
    private string? descriptorMonitor;
    private MaterialKey? pending, displayed, requested;
    private CancellationTokenSource? work;
    private bool disposed;
    private readonly Func<DisplayLayout,WallpaperDescriptor> resolve;
    private readonly Func<WallpaperDescriptor,CancellationToken,WallpaperPixels> decode;
    private readonly Func<bool> transparencyAllowed;
    private readonly Func<bool> highContrast;
    private Action<DipRect,MaterialParameters>? showLive;
    private Action? releaseLive;
    private Func<RefractionStatistics>? refractionState;
    private bool liveVisible;
    internal void UseLiveBackdrop(Action<DipRect,MaterialParameters> show,Action release,Func<RefractionStatistics>? refraction=null)
    { Release();showLive=show;releaseLive=release;refractionState=refraction; }
    internal void ReportRefractionFailure()=>SetStatus("边缘采样不可用，保留实时毛玻璃");
    internal void UpdateRefractionStatus()
    {
        if(Parameters.Kind!=MaterialKind.LiquidEdge || !liveVisible)return;
        var edge=refractionState?.Invoke()??default;
        SetStatus(EdgeStatus(edge));
    }
    private string EdgeStatus(RefractionStatistics edge)=>edge.Active
        ?edge.GpuEnhanced?"实时毛玻璃 · 柔光与色散":"实时毛玻璃 · 基础边缘（色散暂不可用）"
        :Parameters.Displacement==0?"实时毛玻璃 · 折射强度为 0"
        :edge.Failure is not null?"边缘采样不可用，保留实时毛玻璃":"实时毛玻璃 · 交互结束后恢复边缘折射";
    public MaterialParameters Parameters { get; private set; } = new();
    public string Status { get; private set; } = "等待生成壁纸材质";
    public long CacheBytes => cache.Bytes;
    public double LastRenderMilliseconds { get; private set; }
    public Task RenderingCompletion { get; private set; } = Task.CompletedTask;
    public event Action<string>? StatusChanged;
    public event Action<string, long, double, double>? ValidatedFrame;
    public MaterialController(TimetableView view, Func<DisplayLayout,WallpaperDescriptor>? resolve = null,
        Func<WallpaperDescriptor,CancellationToken,WallpaperPixels>? decode = null, Func<bool>? transparencyAllowed = null,
        Func<bool>? highContrast = null)
    {
        this.view = view; this.resolve = resolve ?? WallpaperSource.Resolve; this.decode = decode ?? WallpaperSource.Decode;
        this.transparencyAllowed = transparencyAllowed ?? (() => !SystemParameters.HighContrast && TransparencyEnabled());
        this.highContrast=highContrast??(()=>SystemParameters.HighContrast);
    }
    public void Configure(MaterialParameters value)
    {
        value.Validate(); if (Parameters == value) return;
        // Live tint/ink adjustments update the existing compositor surface in place.
        // Recreating the HWND on every slider tick caused flashing and unnecessary allocations.
        if(showLive is null || value.Kind==MaterialKind.Solid || Parameters.Kind==MaterialKind.Solid)Release();
        Parameters = value;
        view.SetForegroundTransparency(highContrast()?0:value.ForegroundTransparency);
    }
    public void InvalidateSource() { Release(); descriptor = null; descriptorMonitor = null; }
    private void SetStatus(string value)
    { if (Status == value) return; Status = value; StatusChanged?.Invoke(value); }
    public void Request(DisplayLayout display, DipRect physicalViewport, double scale)
    {
        if (disposed) return;
        var contrast=highContrast();
        view.SetFallbackAppearance(Parameters.Dark,contrast);
        view.SetForegroundTransparency(contrast?0:Parameters.ForegroundTransparency);
        view.SetMaterialViewport(physicalViewport);
        if(contrast){Release();SetStatus("系统高对比度 · 使用系统配色实色回退");return;}
        if (Parameters.Kind == MaterialKind.Solid) { Release(); SetStatus("实色材质"); return; }
        try
        {
            if (!transparencyAllowed())
            { Release(); SetStatus("系统已关闭透明效果，使用可读实色回退"); return; }
            if(showLive is not null)
            {
                showLive(physicalViewport,Parameters);view.SetLiveBackdrop(true,Parameters.Dark);
                var edge=refractionState?.Invoke()??default;
                if(!liveVisible)ValidatedFrame?.Invoke("LiveBackdrop",0,0,edge.MaximumDisplacement);
                liveVisible=true;
                SetStatus(Parameters.Kind==MaterialKind.LiquidEdge ? EdgeStatus(edge):"实时半透明毛玻璃 · 系统合成");
                return;
            }
            if (descriptor is null || descriptorMonitor != display.Id)
            { descriptor = resolve(display); descriptorMonitor = display.Id; }
            var key = new MaterialKey(descriptor.SourceId, display.Id, display.DpiScale, physicalViewport, scale, Parameters);
            requested = key;
            if (displayed == key) { pending = null; return; }
            if (pending == key) return;
            if (cache.Get(key) is { } frame) { pending = null; displayed = key; view.SetMaterial(frame); return; }
            pending = key;
            if (work is null)
            {
                work = new();
                RenderingCompletion = PumpAsync(work, cache.Generation);
            }
        }
        catch (Exception ex) when (IsSourceFailure(ex))
        { Release(); SetStatus(showLive is null?"静态壁纸不可用，使用可读实色回退":"系统玻璃不可用，使用可读实色回退"); }
    }
    private async Task PumpAsync(CancellationTokenSource owner, long generation)
    {
        try
        {
            while (pending is not null)
            {
                // Event-driven debounce; no timer remains after the latest request is rendered.
                await Task.Delay(80, owner.Token);
                if (pending is not { } key) break;
                pending = null;
                var input = descriptor!;
                var frame = await Task.Run(() =>
                {
                    var pixels = cache.GetSource(input.SourceId) ?? decode(input, owner.Token);
                    if (!cache.SetSource(pixels, generation)) throw new OperationCanceledException();
                    return MaterialRasterizer.Render(input, pixels, key, owner.Token);
                }, owner.Token);
                owner.Token.ThrowIfCancellationRequested();
                if (disposed || work != owner || !cache.Add(frame, generation)) return;
                // Intermediate positions are discarded. The old overscan stays world-aligned while moving.
                if (requested == key)
                {
                    pending = null; displayed = key; LastRenderMilliseconds = frame.RenderMilliseconds;
                    view.SetMaterial(frame);
                    ValidatedFrame?.Invoke(key.Parameters.Kind.ToString(),cache.Bytes,frame.RenderMilliseconds,frame.MaximumDisplacement);
                    SetStatus(Parameters.Kind == MaterialKind.LiquidEdge ? "液态边缘 · 静态壁纸参考" : "轻透磨砂 · 静态壁纸参考");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (IsSourceFailure(ex))
        {
            if (work == owner) { pending = null; displayed = null; cache.Clear(); view.ClearMaterial(); SetStatus("材质生成失败，使用可读实色回退"); }
        }
        finally { if (work == owner) work = null; owner.Dispose(); }
    }
    private static bool IsSourceFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or COMException
        or ArgumentException or InvalidOperationException or NotSupportedException or FormatException or System.Security.SecurityException
        or System.ComponentModel.Win32Exception;
    private static bool TransparencyEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("EnableTransparency") is not int value || value != 0;
    }
    public void Release()
    {
        var hadResources = work is not null || view.HasMaterial || cache.Bytes > 0 || liveVisible;
        releaseLive?.Invoke();liveVisible=false;view.SetLiveBackdrop(false);
        pending = displayed = requested = null; var current = work; work = null; current?.Cancel();
        cache.Clear(); view.ClearMaterial();
        if (hadResources) ValidatedFrame?.Invoke("Released",cache.Bytes,0,0);
    }
    public void Dispose() { if (disposed) return; disposed = true; Release(); descriptor = null; }
}
