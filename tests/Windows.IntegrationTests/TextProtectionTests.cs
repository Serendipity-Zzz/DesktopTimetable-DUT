using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DlutTimetable.Domain;
using DlutTimetable.Windows;
using DlutTimetable.Windows.Materials;

namespace DlutTimetable.Windows.IntegrationTests;

public sealed class TextProtectionTests
{
    [Theory]
    [InlineData(false,1)] [InlineData(false,.88)] [InlineData(false,.65)]
    [InlineData(true,1)] [InlineData(true,.88)] [InlineData(true,.65)]
    public void FinalCompositedInkPassesRgbBackdropAndSixAccentMatrix(bool dark,double opacity)
    {
        var solver=new TextProtectionSolver();
        foreach(var accent in new[]{"#4F8FF7","#AE83E8","#E5A851","#65B18C","#56AFBC","#DB86A6"})
        {
            var paint=default(SurfacePaint).Over(dark?Colors.Black:Colors.White,dark?.18:.22)
                .Over((Color)ColorConverter.ConvertFromString(accent),.12);
            var guard=solver.Solve(dark?GlassAppearance.LightInk:GlassAppearance.DarkInk,opacity,paint);
            for(var r=0;r<=255;r+=17)for(var g=0;g<=255;g+=17)for(var b=0;b<=255;b+=17)
            {
                var backdrop=Color.FromRgb((byte)r,(byte)g,(byte)b);
                // Independent source-over calculation; includes the view's group opacity.
                var source=Color.FromRgb((byte)Math.Round(paint.R+r*(1-paint.Alpha)),
                    (byte)Math.Round(paint.G+g*(1-paint.Alpha)),(byte)Math.Round(paint.B+b*(1-paint.Alpha)));
                var background=TextContrast.Blend(backdrop,TextContrast.Blend(source,guard.Veil,guard.Opacity),opacity);
                var ink=TextContrast.Blend(backdrop,guard.Ink,opacity);
                Assert.True(TextContrast.Ratio(ink,background)>=4.5,$"{dark}/{opacity}/{accent}/{backdrop}");
            }
        }
    }

    [Fact]
    public void ProtectionCacheIsBoundedAndRejectsInvalidOpacity()
    {
        var solver=new TextProtectionSolver();
        for(var i=0;i<100;i++)solver.Solve(GlassAppearance.DarkInk,.65+i*.003);
        Assert.Equal(TextProtectionSolver.Capacity,solver.Count);
        Assert.Throws<ArgumentOutOfRangeException>(()=>solver.Solve(Colors.Black,double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(()=>solver.Solve(Colors.Black,.64));
    }

    [Theory]
    [InlineData(false,1)] [InlineData(false,.88)] [InlineData(false,.65)]
    [InlineData(true,1)] [InlineData(true,.88)] [InlineData(true,.65)]
    public void ActualButtonsKeepProtectedInkWhenPressedOrPinned(bool dark,double opacity)=>Sta(()=>
    {
        for(var index=0;index<4;index++)for(var state=0;state<3;state++)
        {
            ButtonBase button=index==3?new NativePinButton():new NativeCommandButton();
            button.Width=36;button.Height=30;button.FontSize=14;
            CommandChrome.Attach(button,index);
            var root=new Grid{Width=40,Height=34};root.Children.Add(button);
            if(state==1)((INativeCommandInput)button).SetNativePressed(true);
            if(state==2 && button is ToggleButton pin)pin.IsChecked=true;
            CommandChrome.Update(button,new SolidColorBrush(dark?GlassAppearance.LightInk:GlassAppearance.DarkInk),opacity);
            byte[] Capture()
            {
                root.Measure(new(40,34));root.Arrange(new(0,0,40,34));root.UpdateLayout();
                root.Dispatcher.Invoke(()=>{},System.Windows.Threading.DispatcherPriority.Render);
                var bitmap=new RenderTargetBitmap(80,68,192,192,PixelFormats.Pbgra32);bitmap.Render(root);
                var pixels=new byte[80*68*4];bitmap.CopyPixels(pixels,80*4,0);return pixels;
            }
            var complete=Capture();button.Foreground=Brushes.Transparent;var background=Capture();
            button.Opacity=1;button.Foreground=Brushes.White;button.Background=Brushes.Transparent;
            var mask=Capture();var cores=0;
            // Exclude button borders, including checked/pressed border triggers.
            for(var y=16;y<52;y++)for(var x=20;x<60;x++)
            {
                var i=y*80+x;if(mask[i*4+3]<254)continue;cores++;
                foreach(var backdrop in new[]{Colors.Black,Colors.White,Colors.Magenta,Colors.Cyan})
                    Assert.True(TextContrast.Ratio(Over(complete,i,backdrop),Over(background,i,backdrop))>=4.5,
                        $"Button {index}, state {state}, dark={dark}, opacity={opacity}");
            }
            Assert.True(cores>0,$"Button {index} must render measurable glyph cores");
        }
    });

    [Theory]
    [InlineData(false,0)] [InlineData(false,.12)] [InlineData(false,.35)]
    [InlineData(true,0)] [InlineData(true,.12)] [InlineData(true,.35)]
    public void ActualRenderedGlyphCoresRemainReadableOverControlledBackdrops(bool dark,double transparency)=>Sta(()=>
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root is not null && !File.Exists(Path.Combine(root.FullName,"DlutTimetable.slnx")))root=root.Parent;
        var data=JsonSerializer.Deserialize<CurrentDataset>(File.ReadAllText(Path.Combine(root!.FullName,"fixtures/dlut/synthetic-term.json")),
            new JsonSerializerOptions{PropertyNameCaseInsensitive=true})!;
        var view=new TimetableView();view.SetDataset(ValidatedDataset.Create(data),1);
        view.SetLiveBackdrop(true,dark);view.SetForegroundTransparency(transparency);
        view.Measure(new(780,468));view.Arrange(new(0,0,780,468));view.UpdateLayout();
        var layer=(DrawingVisual)typeof(TimetableView).GetField("foreground",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!;
        var complete=Render(layer.Drawing,1-transparency);
        var background=Render(Filter(layer.Drawing,false),1-transparency);
        var mask=Render(Filter(layer.Drawing,true),1);
        var cores=Enumerable.Range(0,780*468).Where(i=>mask[i*4+3]>=254).ToArray();
        Assert.True(cores.Length>100,"Exercise actual header, weekdays, periods and course glyphs");
        foreach(var backdrop in new[]{Colors.Black,Colors.White,Color.FromRgb(127,127,127),Colors.Magenta,Colors.Cyan})
        foreach(var i in cores)
            Assert.True(TextContrast.Ratio(Over(complete,i,backdrop),Over(background,i,backdrop))>=4.5,
                $"Rendered glyph {i%780},{i/780}, dark={dark}, transparency={transparency}, backdrop={backdrop}");
        var directory=Path.Combine(root.FullName,"artifacts/integrated-text-stage");Directory.CreateDirectory(directory);
        var composed=new byte[complete.Length];
        for(var i=0;i<780*468;i++)
        {
            var x=i%780;var y=i/780;
            var color=Over(complete,i,Color.FromRgb((byte)(x*255/780),(byte)(y*255/468),(byte)(x%80<40?210:40)));
            composed[i*4]=color.B;composed[i*4+1]=color.G;composed[i*4+2]=color.R;composed[i*4+3]=255;
        }
        var bitmap=BitmapSource.Create(780,468,96,96,PixelFormats.Bgra32,null,composed,780*4);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file=File.Create(Path.Combine(directory,$"{(dark?"dark":"light")}-{transparency:F2}.png"));encoder.Save(file);
    });

    private static Drawing Filter(Drawing drawing,bool mask)
    {
        if(drawing is GlyphRunDrawing glyph)return mask?new GlyphRunDrawing(Brushes.White,glyph.GlyphRun):new DrawingGroup();
        if(drawing is not DrawingGroup group)return mask?new DrawingGroup():drawing;
        var copy=group.Clone();copy.Children.Clear();
        foreach(var child in group.Children)copy.Children.Add(Filter(child,mask));return copy;
    }
    private static byte[] Render(Drawing drawing,double opacity)
    {
        var visual=new DrawingVisual{Opacity=opacity};using(var dc=visual.RenderOpen())dc.DrawDrawing(drawing);
        var bitmap=new RenderTargetBitmap(780,468,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
        var bytes=new byte[780*468*4];bitmap.CopyPixels(bytes,780*4,0);return bytes;
    }
    private static Color Over(byte[] pixels,int i,Color backdrop)
    {
        var alpha=pixels[i*4+3]/255d;
        return Color.FromRgb((byte)Math.Clamp(Math.Round(pixels[i*4+2]+backdrop.R*(1-alpha)),0,255),
            (byte)Math.Clamp(Math.Round(pixels[i*4+1]+backdrop.G*(1-alpha)),0,255),
            (byte)Math.Clamp(Math.Round(pixels[i*4]+backdrop.B*(1-alpha)),0,255));
    }
    private static void Sta(Action action)
    {
        Exception? error=null;var thread=new Thread(()=>{try{action();}catch(Exception ex){error=ex;}});
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(error is not null)ExceptionDispatchInfo.Capture(error).Throw();
    }
}
