using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DlutTimetable.Domain;

namespace DlutTimetable.Windows.Materials;

/// <summary>Event-driven, bounded CPU rasterization of a local wallpaper. Never captures the screen.</summary>
public static class MaterialRasterizer
{
    public const double OverscanDip = 48;
    public static DipRect ImagePlacement(WallpaperDescriptor descriptor, WallpaperPixels image)
    {
        var a = descriptor.PhysicalImageArea;
        if (descriptor.Position is WallpaperPosition.Stretch) return a;
        if (descriptor.Position == WallpaperPosition.Tile) return new(a.X, a.Y, image.OriginalWidth, image.OriginalHeight);
        var factor = descriptor.Position switch
        {
            WallpaperPosition.Center => 1,
            WallpaperPosition.Fit => Math.Min(a.Width / image.OriginalWidth, a.Height / image.OriginalHeight),
            _ => Math.Max(a.Width / image.OriginalWidth, a.Height / image.OriginalHeight)
        };
        var w = image.OriginalWidth * factor; var h = image.OriginalHeight * factor;
        return new(a.X + (a.Width-w)/2, a.Y + (a.Height-h)/2, w, h);
    }
    public static MaterialFrame Render(WallpaperDescriptor descriptor, WallpaperPixels source, MaterialKey key, CancellationToken ct = default)
    {
        key.Parameters.Validate();
        if (!double.IsFinite(key.DpiScale) || key.DpiScale is < 0.5 or > 8
            || !double.IsFinite(key.LogicalScale) || key.LogicalScale is < 0.1 or > 2
            || !double.IsFinite(key.PhysicalViewport.X) || !double.IsFinite(key.PhysicalViewport.Y)
            || !double.IsFinite(key.PhysicalViewport.Width) || key.PhysicalViewport.Width is <= 0 or > 20000
            || !double.IsFinite(key.PhysicalViewport.Height) || key.PhysicalViewport.Height is <= 0 or > 20000
            || descriptor.SourceId != key.SourceId || source.SourceId != key.SourceId
            || descriptor.PhysicalImageArea.Width <= 0 || descriptor.PhysicalImageArea.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(key));
        var watch = Stopwatch.StartNew();
        var dipWidth = key.PhysicalViewport.Width / key.DpiScale; var dipHeight = key.PhysicalViewport.Height / key.DpiScale;
        // Background need not have the same density as crisp text. Cap material dimensions and memory independently of display DPI.
        var density = Math.Min(1, 1200 / (dipWidth + 2 * OverscanDip));
        density = Math.Min(density, 900 / (dipHeight + 2 * OverscanDip));
        var width = Math.Max(1, (int)Math.Ceiling((dipWidth + 2*OverscanDip) * density));
        var height = Math.Max(1, (int)Math.Ceiling((dipHeight + 2*OverscanDip) * density));
        var physicalOuter = new DipRect(key.PhysicalViewport.X - OverscanDip*key.DpiScale, key.PhysicalViewport.Y - OverscanDip*key.DpiScale,
            (dipWidth + 2*OverscanDip)*key.DpiScale, (dipHeight + 2*OverscanDip)*key.DpiScale);
        var blurRadius = (int)Math.Round(key.Parameters.BlurRadius * density);
        var pad = Math.Max(2, blurRadius * 3 + (int)Math.Ceiling(key.Parameters.Displacement * density) + 2);
        var extendedWidth = width + 2*pad; var extendedHeight = height + 2*pad;
        var sampled = new byte[checked(extendedWidth * extendedHeight * 4)];
        var placement = ImagePlacement(descriptor, source);
        for (var y = 0; y < extendedHeight; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (var x = 0; x < extendedWidth; x++)
            {
                var px = physicalOuter.X + (x-pad+0.5) * physicalOuter.Width / width;
                var py = physicalOuter.Y + (y-pad+0.5) * physicalOuter.Height / height;
                var index = (y * extendedWidth + x) * 4;
                WallpaperSample(source, descriptor, placement, px, py, sampled, index);
            }
        }
        if (blurRadius > 0)
        {
            var scratch = new byte[sampled.Length];
            for (var pass = 0; pass < 3; pass++)
            { BoxBlur(sampled, scratch, extendedWidth, extendedHeight, blurRadius, true, ct); BoxBlur(scratch, sampled, extendedWidth, extendedHeight, blurRadius, false, ct); }
        }
        var result = new byte[checked(width * height * 4)];
        var maxDisplacement = 0d;
        for (var y = 0; y < height; y++)
        {
            ct.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var localX = (x + 0.5) * (dipWidth + 2*OverscanDip) / width - OverscanDip;
                var localY = (y + 0.5) * (dipHeight + 2*OverscanDip) / height - OverscanDip;
                var shiftX = 0d; var shiftY = 0d;
                if (key.Parameters.Kind == MaterialKind.LiquidEdge)
                {
                    var (distance, nx, ny) = Edge(localX, localY, dipWidth, dipHeight, 18 * key.LogicalScale);
                    if (distance >= 0 && distance < key.Parameters.EdgeWidth)
                    {
                        var bend = key.Parameters.Displacement * Math.Sin(Math.PI * distance / key.Parameters.EdgeWidth);
                        shiftX = -nx * bend * density; shiftY = -ny * bend * density;
                        maxDisplacement = Math.Max(maxDisplacement, bend);
                    }
                }
                var output = (y * width + x) * 4;
                Sample(sampled, extendedWidth, extendedHeight, x+pad+shiftX, y+pad+shiftY, result, output);
                // Tint is local to the window; overscan provides alignment while dragging.
                var t = Math.Clamp(localY / dipHeight, 0, 1);
                var tint = key.Parameters.TintTop * (1-t) + key.Parameters.TintBottom*t;
                var target = key.Parameters.Dark ? 0 : 255;
                for (var c = 0; c < 3; c++) result[output+c] = (byte)Math.Round(result[output+c]*(1-tint) + target*tint);
            }
        }
        ct.ThrowIfCancellationRequested();
        var analysis = new PixelBoundsAnalysis(result, width, height);
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, result, width*4); bitmap.Freeze();
        return new(key, physicalOuter, bitmap, analysis, (long)width*height*4 + analysis.ByteCount, watch.Elapsed.TotalMilliseconds, maxDisplacement);
    }
    public static (double Distance, double NormalX, double NormalY) Edge(double x, double y, double width, double height, double radius)
    {
        radius = Math.Min(radius, Math.Min(width,height)/2);
        var qx = Math.Abs(x-width/2) - (width/2-radius); var qy = Math.Abs(y-height/2) - (height/2-radius);
        var ox = Math.Max(qx,0); var oy = Math.Max(qy,0); var length = Math.Sqrt(ox*ox + oy*oy);
        var distance = -(length + Math.Min(Math.Max(qx,qy),0) - radius);
        if (length > 0) return (distance, Math.Sign(x-width/2)*ox/length, Math.Sign(y-height/2)*oy/length);
        return qx > qy ? (distance, Math.Sign(x-width/2), 0) : (distance, 0, Math.Sign(y-height/2));
    }
    private static void WallpaperSample(WallpaperPixels source, WallpaperDescriptor descriptor, DipRect placement, double x, double y, byte[] output, int index)
    {
        var u = (x-placement.X)/placement.Width; var v = (y-placement.Y)/placement.Height;
        if (descriptor.Position == WallpaperPosition.Tile) { u -= Math.Floor(u); v -= Math.Floor(v); }
        else if (u < 0 || v < 0 || u > 1 || v > 1)
        {
            output[index] = (byte)(descriptor.BackgroundColor >> 16); output[index+1] = (byte)(descriptor.BackgroundColor >> 8);
            output[index+2] = (byte)descriptor.BackgroundColor; output[index+3] = 255; return;
        }
        Sample(source.Bgra, source.Width, source.Height, u*source.Width-0.5, v*source.Height-0.5, output, index);
    }
    private static void Sample(byte[] input, int width, int height, double x, double y, byte[] output, int at)
    {
        x = Math.Clamp(x,0,width-1); y = Math.Clamp(y,0,height-1);
        var left = (int)x; var top = (int)y; var right = Math.Min(left+1,width-1); var bottom = Math.Min(top+1,height-1);
        var fx = x-left; var fy = y-top;
        var a = (top*width+left)*4; var b = (top*width+right)*4; var c = (bottom*width+left)*4; var d = (bottom*width+right)*4;
        for (var channel = 0; channel < 3; channel++)
            output[at+channel] = (byte)Math.Round((input[a+channel]*(1-fx)+input[b+channel]*fx)*(1-fy)+(input[c+channel]*(1-fx)+input[d+channel]*fx)*fy);
        output[at+3] = 255;
    }
    private static void BoxBlur(byte[] input, byte[] output, int width, int height, int radius, bool horizontal, CancellationToken ct)
    {
        var lines = horizontal ? height : width; var length = horizontal ? width : height; var count = radius*2+1;
        for (var line = 0; line < lines; line++)
        {
            ct.ThrowIfCancellationRequested();
            for (var channel = 0; channel < 3; channel++)
            {
                int At(int along) => ((horizontal ? line*width+along : along*width+line)*4)+channel;
                var sum = 0; for (var k=-radius;k<=radius;k++) sum += input[At(Math.Clamp(k,0,length-1))];
                for (var along=0;along<length;along++)
                {
                    output[At(along)] = (byte)((sum+count/2)/count);
                    sum += input[At(Math.Min(length-1,along+radius+1))] - input[At(Math.Max(0,along-radius))];
                }
            }
            for (var along=0;along<length;along++) output[(horizontal ? line*width+along : along*width+line)*4+3] = 255;
        }
    }
}
