using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DlutTimetable.Domain;
using DlutTimetable.Windows;
using DlutTimetable.Windows.Materials;

namespace DlutTimetable.Windows.IntegrationTests;

// Pixel/component tests. These do not establish native host or hardware performance acceptance.
public sealed class MaterialTests
{
    private static WallpaperPixels Pixels(string kind = "complex")
    {
        const int width=256,height=160;
        var data = new byte[width*height*4];
        for(var y=0;y<height;y++) for(var x=0;x<width;x++)
        {
            var i=(y*width+x)*4;
            data[i]=kind=="light" ? (byte)235 : kind=="dark" ? (byte)12 : (byte)(x%32<16 ? 230:20);
            data[i+1]=kind=="light" ? (byte)240 : kind=="dark" ? (byte)18 : (byte)(y%24<12 ? 170:35);
            data[i+2]=kind=="light" ? (byte)245 : kind=="dark" ? (byte)24 : (byte)((x+y)%256);
            data[i+3]=255;
        }
        return new(kind,width,height,2560,1600,data);
    }
    private static WallpaperDescriptor Descriptor(string kind="complex") => new(kind,"synthetic",WallpaperPosition.Fill,new(-1920,-100,3840,2160),0);
    private static MaterialKey Key(string kind="complex",MaterialParameters? parameters=null) => new(kind,"synthetic-monitor",1.5,new(-600,90,1170,702),1,parameters??new());
    private static byte[] Bytes(MaterialFrame frame) { var bytes=new byte[frame.Bitmap.PixelWidth*frame.Bitmap.PixelHeight*4];frame.Bitmap.CopyPixels(bytes,frame.Bitmap.PixelWidth*4,0);return bytes; }
    private static string Root()
    {
        var path=new DirectoryInfo(AppContext.BaseDirectory);
        while(path is not null && !File.Exists(Path.Combine(path.FullName,"DlutTimetable.slnx")))path=path.Parent;
        return path?.FullName ?? throw new InvalidOperationException("Repository missing");
    }
    private static void Sta(Action action)
    {
        Exception? error=null;
        var thread=new Thread(()=> { try { action(); }catch(Exception ex){error=ex;} });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(error is not null)ExceptionDispatchInfo.Capture(error).Throw();
    }
    private static void StaAsync(Func<Task> action) => Sta(()=>
    {
        var dispatcher=Dispatcher.CurrentDispatcher;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        var task=action();var frame=new DispatcherFrame();
        _=task.ContinueWith(_=>dispatcher.BeginInvoke(()=>frame.Continue=false),TaskScheduler.Default);
        Dispatcher.PushFrame(frame);task.GetAwaiter().GetResult();
        dispatcher.InvokeShutdown();
    });
    private static DisplayLayout Display() => new("synthetic-monitor",new(0,0,1920,1080),1.5,0,0,2880,1620,true);
    [Fact]
    public void SystemContrastReleasesLiveGlassAndRestoresTheUsersTransparencyWhenDisabled()=>Sta(()=>
    {
        var contrast=false;var shows=0;var releases=0;var view=new TimetableView();
        using var controller=new MaterialController(view,transparencyAllowed:()=>true,highContrast:()=>contrast);
        controller.UseLiveBackdrop((_,_)=>shows++,()=>releases++);
        var preference=new MaterialParameters(Dark:true,ForegroundTransparency:0.45);
        controller.Configure(preference);controller.Request(Display(),Key().PhysicalViewport,1);
        Assert.True(view.HasLiveBackdrop);Assert.Equal(0.55,view.ForegroundOpacity,8);
        var before=releases;contrast=true;controller.Request(Display(),Key().PhysicalViewport,1);
        Assert.False(view.HasLiveBackdrop);Assert.False(view.HasMaterial);Assert.True(releases>before);
        Assert.Equal(1,view.ForegroundOpacity);Assert.Contains("系统高对比度",controller.Status);
        Assert.Equal(preference,controller.Parameters);Assert.Equal(1,shows);
        contrast=false;controller.Request(Display(),Key().PhysicalViewport,1);
        Assert.True(view.HasLiveBackdrop);Assert.Equal(0.55,view.ForegroundOpacity,8);Assert.Equal(2,shows);
    });
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SolidFallbackUsesTheSelectedLightOrDarkPalette(bool dark)=>Sta(()=>
    {
        var view=new TimetableView();
        using var controller=new MaterialController(view,transparencyAllowed:()=>false,highContrast:()=>false);
        controller.Configure(new(Dark:dark));controller.Request(Display(),Key().PhysicalViewport,1);
        view.Measure(new(780,468));view.Arrange(new(0,0,780,468));view.UpdateLayout();
        var bitmap=new RenderTargetBitmap(780,468,96,96,PixelFormats.Pbgra32);bitmap.Render(view);
        var pixel=new byte[4];bitmap.CopyPixels(new Int32Rect(400,150,1,1),pixel,4,0);
        Assert.Equal(255,pixel[3]);Assert.Equal(dark?(byte)36:(byte)246,pixel[0]);
        Assert.Equal(dark,TextContrast.Luminance(((SolidColorBrush)view.ChromeForeground).Color)>0.5);
        Assert.False(view.HasLiveBackdrop);Assert.False(view.HasMaterial);
    });
    [Fact]
    public void TransparencyUpdatesRetainLiveSurfaceAndAffectAllForegroundDrawing() => Sta(()=>
    {
        var view=new TimetableView();var releases=0;
        using var controller=new MaterialController(view,transparencyAllowed:()=>true);
        controller.UseLiveBackdrop((_,_)=>{},()=>releases++);
        controller.Request(Display(),Key().PhysicalViewport,1);
        var before=releases;
        byte AlphaAtHeader()
        {
            view.Measure(new(780,468));view.Arrange(new(0,0,780,468));view.UpdateLayout();
            var bitmap=new RenderTargetBitmap(780,468,96,96,PixelFormats.Pbgra32);bitmap.Render(view);
            var pixel=new byte[4];bitmap.CopyPixels(new Int32Rect(400,18,1,1),pixel,4,0);return pixel[3];
        }
        var initial=AlphaAtHeader();
        controller.Configure(controller.Parameters with {BackgroundTransparency=0.9,ForegroundTransparency=0.3});
        controller.Request(Display(),Key().PhysicalViewport,1);
        Assert.Equal(before,releases);Assert.True(view.HasLiveBackdrop);
        Assert.Equal(0.7,view.ForegroundOpacity,6);Assert.True(AlphaAtHeader()<initial);
        // The shared navigation bar now carries contrast protection instead of
        // per-glyph badges. It must still transmit the backdrop, below ink opacity.
        Assert.InRange(initial,1,(byte)(255*0.88-1));
        var button=new NativeCommandButton();CommandChrome.Attach(button,0);
        CommandChrome.Update(button,view.ChromeForeground,view.ForegroundOpacity);
        Assert.Equal(view.ForegroundOpacity,button.Opacity);
        Assert.Throws<ArgumentOutOfRangeException>(()=>new MaterialParameters(BackgroundTransparency:double.NaN).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(()=>new MaterialParameters(ForegroundTransparency:0.46).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(()=>new MaterialParameters(Dispersion:.13).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(()=>new MaterialParameters(LightAngle:double.NaN).Validate());
    });
    [Fact]
    public void LiveBackendNeverResolvesWallpaperAndReleasesItsSurface() => Sta(()=>
    {
        var view=new TimetableView();var shows=0;var releases=0;
        using var controller=new MaterialController(view,_=>throw new IOException("Wallpaper must not be read"),
            (_,_)=>throw new IOException("Wallpaper must not be decoded"),()=>true);
        controller.UseLiveBackdrop((_,_)=>shows++,()=>releases++);
        var start=releases;
        controller.Request(Display(),Key().PhysicalViewport,1);
        Assert.True(view.HasLiveBackdrop);Assert.False(view.HasMaterial);Assert.Equal(1,shows);
        Assert.Equal(0,controller.CacheBytes);
        controller.Release();Assert.False(view.HasLiveBackdrop);Assert.Equal(start+1,releases);
        controller.Configure(new(MaterialKind.Solid));controller.Request(Display(),Key().PhysicalViewport,1);
        Assert.Equal(1,shows);Assert.False(view.HasLiveBackdrop);
    });
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LiveBackendFailureOrDisabledTransparencyFallsBackWithoutWallpaper(bool enabled) => Sta(()=>
    {
        var view=new TimetableView();
        using var controller=new MaterialController(view,_=>throw new IOException("Unexpected wallpaper read"),
            (_,_)=>throw new IOException("Unexpected wallpaper decode"),()=>enabled);
        controller.UseLiveBackdrop((_,_)=>throw new System.Runtime.InteropServices.COMException("Synthetic compositor failure"),()=>{});
        controller.Request(Display(),Key().PhysicalViewport,1);
        Assert.False(view.HasLiveBackdrop);Assert.False(view.HasMaterial);Assert.Contains("回退",controller.Status);
    });
    [Fact]
    public void ReturningToDisplayedPositionDuringDebounceDoesNotRenderAnObsoleteRequest() => StaAsync(async ()=>
    {
        var view=new TimetableView();var decodes=0;
        using var controller=new MaterialController(view,_=>Descriptor(),(_,_)=> {decodes++;return Pixels();},()=>true);
        var rect=Key().PhysicalViewport;
        controller.Request(Display(),rect,1);await controller.RenderingCompletion;
        Assert.True(view.HasMaterial);
        controller.Request(Display(),rect with {X=300,Y=300},1);
        controller.Request(Display(),rect,1);
        await controller.RenderingCompletion;
        Assert.True(view.HasMaterial);Assert.Equal(1,decodes);
        Assert.InRange(controller.CacheBytes,1,WallpaperCache.BudgetBytes);
    });
    [Fact]
    public void HiddenControllerCancelsPendingWorkAndClearsRetainedImages() => StaAsync(async ()=>
    {
        var view=new TimetableView();var decodes=0;
        using var controller=new MaterialController(view,_=>Descriptor(),(_,_)=>{decodes++;return Pixels();},()=>true);
        controller.Request(Display(),Key().PhysicalViewport,1);
        controller.Release();await controller.RenderingCompletion;
        Assert.Equal(0,decodes);Assert.False(view.HasMaterial);Assert.Equal(0,controller.CacheBytes);
        controller.Request(Display(),Key().PhysicalViewport,1);await controller.RenderingCompletion;
        Assert.True(view.HasMaterial);
        controller.Release();Assert.False(view.HasMaterial);Assert.Equal(0,controller.CacheBytes);
    });
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TransparencyDisabledOrUnsupportedSourceFallsBackWithoutDecoding(bool sourceFailure) => Sta(()=>
    {
        var view=new TimetableView();var decodes=0;
        using var controller=new MaterialController(view,_=>throw new IOException("Synthetic source unavailable"),(_,_)=>{decodes++;return Pixels();},()=>sourceFailure);
        controller.Request(Display(),Key().PhysicalViewport,1);
        Assert.Equal(0,decodes);Assert.False(view.HasMaterial);Assert.Contains("回退",controller.Status);
    });
    [Fact]
    public void RefractionChangesOnlyEdgeSamplingAndRespondsToStrength()
    {
        var pixels=Pixels();var descriptor=Descriptor();
        var clear=MaterialRasterizer.Render(descriptor,pixels,Key());
        var liquid=MaterialRasterizer.Render(descriptor,pixels,Key(parameters:new(MaterialKind.LiquidEdge)));
        var stronger=MaterialRasterizer.Render(descriptor,pixels,Key(parameters:new(MaterialKind.LiquidEdge,Displacement:12)));
        var a=Bytes(clear);var b=Bytes(liquid);var c=Bytes(stronger);var edgeChanges=0;var strengthChanges=0;
        for(var y=0;y<clear.Bitmap.PixelHeight;y++)for(var x=0;x<clear.Bitmap.PixelWidth;x++)
        {
            var at=(y*clear.Bitmap.PixelWidth+x)*4;
            var localX=x+0.5-MaterialRasterizer.OverscanDip;var localY=y+0.5-MaterialRasterizer.OverscanDip;
            var distance=MaterialRasterizer.Edge(localX,localY,780,468,18).Distance;
            var same=a.AsSpan(at,4).SequenceEqual(b.AsSpan(at,4));
            if(distance>=14 || distance<0)Assert.True(same,"Outside edge band must not distort");
            else if(!same)edgeChanges++;
            if(!b.AsSpan(at,4).SequenceEqual(c.AsSpan(at,4)))strengthChanges++;
        }
        Assert.True(edgeChanges>100);Assert.True(strengthChanges>100);
        Assert.InRange(liquid.MaximumDisplacement,7.9,8);Assert.InRange(stronger.MaximumDisplacement,11.9,12);
    }
    [Theory]
    [InlineData(WallpaperPosition.Fill,3840,2400)]
    [InlineData(WallpaperPosition.Span,3840,2400)]
    [InlineData(WallpaperPosition.Fit,3456,2160)]
    [InlineData(WallpaperPosition.Stretch,3840,2160)]
    [InlineData(WallpaperPosition.Center,2560,1600)]
    public void PlacementUsesPhysicalNegativeMonitorCoordinates(WallpaperPosition mode,double width,double height)
    {
        var rect=MaterialRasterizer.ImagePlacement(Descriptor() with {Position=mode},Pixels());
        Assert.Equal(width,rect.Width,6);Assert.Equal(height,rect.Height,6);
        Assert.Equal(0,rect.X+rect.Width/2,6);Assert.Equal(980,rect.Y+rect.Height/2,6);
    }
    [Fact]
    public void CacheIsBoundedLruAndRejectsInFlightPreviousGeneration()
    {
        var cache=new WallpaperCache();var generation=cache.Generation;var pixels=Pixels();
        Assert.True(cache.SetSource(pixels,generation));
        var frame=MaterialRasterizer.Render(Descriptor(),pixels,Key());
        var keys=Enumerable.Range(0,5).Select(i=>Key() with { PhysicalViewport=new(i,0,1170,702) }).ToArray();
        foreach(var key in keys)Assert.True(cache.Add(frame with {Key=key},generation));
        Assert.Equal(3,cache.Count);Assert.Null(cache.Get(keys[0]));Assert.NotNull(cache.Get(keys[2]));
        Assert.True(cache.Add(frame with {Key=keys[0]},generation));Assert.Null(cache.Get(keys[3]));
        Assert.InRange(cache.Bytes,1,WallpaperCache.BudgetBytes);
        cache.Clear();Assert.Equal(0,cache.Bytes);Assert.Equal(0,cache.Count);
        Assert.False(cache.Add(frame,generation));Assert.False(cache.SetSource(pixels,generation));
    }
    [Fact]
    public void RasterizationIsCancelledAndRejectsNonFiniteOrMismatchedKeys()
    {
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(()=>MaterialRasterizer.Render(Descriptor(),Pixels(),Key(),cancellation.Token));
        Assert.Throws<ArgumentOutOfRangeException>(()=>MaterialRasterizer.Render(Descriptor(),Pixels(),Key() with {DpiScale=double.NaN}));
        Assert.Throws<ArgumentOutOfRangeException>(()=>MaterialRasterizer.Render(Descriptor(),Pixels(),Key() with {SourceId="wrong"}));
        Assert.Throws<ArgumentOutOfRangeException>(()=>new MaterialParameters(Displacement:double.PositiveInfinity).Validate());
    }
    [Fact]
    public void SurfaceAdaptationKeepsTextReadableWithoutOpaqueGlyphBackgrounds()
    {
        string[] accents=["#4F8FF7","#AE83E8","#E5A851","#65B18C","#56AFBC","#DB86A6"];
        var colors=new List<Color>();
        for(var r=0;r<=255;r+=51)for(var g=0;g<=255;g+=51)for(var b=0;b<=255;b+=51)colors.Add(Color.FromRgb((byte)r,(byte)g,(byte)b));
        var bytes=colors.SelectMany(c=>new[]{c.B,c.G,c.R,(byte)255}).ToArray();
        var analysis=new PixelBoundsAnalysis(bytes,colors.Count,1);
        foreach(var hex in accents)
        {
            var accent=(Color)ColorConverter.ConvertFromString(hex);
            var surface=GlassAppearance.Solve(analysis,new(0,0,colors.Count,1),accent,GlassAppearance.CourseTintOpacity);
            Assert.InRange(surface.VeilOpacity,0,0.65);Assert.True(surface.Contrast>=4.5);
            foreach(var color in colors)
            {
                var final=TextContrast.Blend(TextContrast.Blend(color,accent,GlassAppearance.CourseTintOpacity),surface.Veil,surface.VeilOpacity);
                Assert.True(TextContrast.Ratio(final,surface.Foreground)>=4.5,hex);
            }
        }
    }
    [Theory]
    [InlineData(20,true)]
    [InlineData(240,false)]
    public void CalmBackgroundsChangeInkInsteadOfAddingMilkyPanels(byte level,bool lightInk)
    {
        var analysis=new PixelBoundsAnalysis([level,level,level,255],1,1);
        var surface=GlassAppearance.Solve(analysis,new(0,0,1,1),Colors.Transparent,0);
        Assert.Equal(0,surface.VeilOpacity);
        Assert.Equal(lightInk?GlassAppearance.LightInk:GlassAppearance.DarkInk,surface.Foreground);
        Assert.True(surface.Contrast>=4.5);
    }
    [Fact]
    public void ProtectedCourseCardTransmitsWallpaperAndLeavesEmptyCanvasUnchanged() => Sta(()=>
    {
        var json=File.ReadAllText(Path.Combine(Root(),"fixtures/dlut/synthetic-term.json"));
        var data=ValidatedDataset.Create(JsonSerializer.Deserialize<CurrentDataset>(json,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})!);
        (Color Card,Color Background,Color Canvas) Render(string id,Color wallpaper)
        {
            var pixels=Enumerable.Range(0,32*32).SelectMany(_=>new[]{wallpaper.B,wallpaper.G,wallpaper.R,(byte)255}).ToArray();
            var source=new WallpaperPixels(id,32,32,2560,1600,pixels);
            var frame=MaterialRasterizer.Render(Descriptor(id),source,Key(id));
            var view=new TimetableView();view.SetDataset(data,1);view.SetMaterialViewport(frame.Key.PhysicalViewport);view.SetMaterial(frame);
            view.Measure(new(780,468));view.Arrange(new(0,0,780,468));view.UpdateLayout();
            var bitmap=new RenderTargetBitmap(780,468,96,96,PixelFormats.Pbgra32);bitmap.Render(view);
            var cardPixel=new byte[4];bitmap.CopyPixels(new Int32Rect(127,123,1,1),cardPixel,4,0);
            var basePixel=new byte[4];frame.Bitmap.CopyPixels(new Int32Rect(175,171,1,1),basePixel,4,0);
            var canvasPixel=new byte[4];bitmap.CopyPixels(new Int32Rect(700,300,1,1),canvasPixel,4,0);
            view.ClearMaterial();
            return(Color.FromRgb(cardPixel[2],cardPixel[1],cardPixel[0]),Color.FromRgb(basePixel[2],basePixel[1],basePixel[0]),
                Color.FromRgb(canvasPixel[2],canvasPixel[1],canvasPixel[0]));
        }
        var a=Render("blue",Color.FromRgb(25,45,95));var b=Render("violet",Color.FromRgb(70,35,100));
        static double Difference(Color a,Color b)=>Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B);
        var backdropChange=Difference(a.Background,b.Background);
        Assert.True(backdropChange>20);
        // Readable text and its continuous card now share one protected surface.
        // Require visible transmission there, and full transmission on empty canvas.
        Assert.True(Difference(a.Card,b.Card)>=backdropChange*0.3,"Protected cards must remain translucent");
        Assert.InRange(Difference(a.Canvas,b.Canvas),backdropChange-3,backdropChange+3);
    });
    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    [InlineData("complex")]
    public void NativeDrawingLayersRenderSyntheticFixturesAndReleaseImages(string kind) => Sta(()=>
    {
        var parameters=new MaterialParameters(MaterialKind.LiquidEdge,Dark:kind=="dark");
        var frame=MaterialRasterizer.Render(Descriptor(kind),Pixels(kind),Key(kind,parameters));
        var view=new TimetableView();
        var json=File.ReadAllText(Path.Combine(Root(),"fixtures/dlut/synthetic-term.json"));
        var data=JsonSerializer.Deserialize<CurrentDataset>(json,new JsonSerializerOptions {PropertyNameCaseInsensitive=true})!;
        view.SetDataset(ValidatedDataset.Create(data),1);
        view.SetMaterialViewport(frame.Key.PhysicalViewport);view.SetMaterial(frame);
        view.Measure(new(780,468));view.Arrange(new(0,0,780,468));view.UpdateLayout();
        var bitmap=new RenderTargetBitmap(780,468,96,96,PixelFormats.Pbgra32);bitmap.Render(view);
        var path=Path.Combine(Root(),"artifacts/material-stage");Directory.CreateDirectory(path);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using(var output=File.Create(Path.Combine(path,$"{kind}.png")))encoder.Save(output);
        Assert.True(view.HasMaterial);view.ClearMaterial();Assert.False(view.HasMaterial);
        var field=typeof(TimetableView).GetField("background",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
        var drawing=((DrawingVisual)field.GetValue(view)!).Drawing;
        Assert.DoesNotContain(drawing.Children,item=>item is ImageDrawing);
    });
}
