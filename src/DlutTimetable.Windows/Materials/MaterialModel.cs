using System.Windows.Media;
using System.Windows.Media.Imaging;
using DlutTimetable.Domain;

namespace DlutTimetable.Windows.Materials;

public enum MaterialKind { Solid, ClearFrosted, LiquidEdge }
public enum WallpaperPosition { Center, Tile, Stretch, Fit, Fill, Span }
public sealed record MaterialParameters(MaterialKind Kind = MaterialKind.ClearFrosted, bool Dark = false,
    double BlurRadius = 12, double EdgeWidth = 14, double Displacement = 8, double TintTop = 0.12, double TintBottom = 0.06,
    double BackgroundTransparency = 0.80, double ForegroundTransparency = 0.12,
    double Dispersion=0.04,double LightAngle=315)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Kind) || !double.IsFinite(BlurRadius) || BlurRadius is < 0 or > 16
            || !double.IsFinite(EdgeWidth) || EdgeWidth is < 10 or > 16
            || !double.IsFinite(Displacement) || Displacement is < 0 or > 12
            || !double.IsFinite(TintTop) || TintTop is < 0 or > 0.4
            || !double.IsFinite(TintBottom) || TintBottom is < 0 or > 0.4
            || !double.IsFinite(BackgroundTransparency) || BackgroundTransparency is < 0.40 or > 0.95
            || !double.IsFinite(ForegroundTransparency) || ForegroundTransparency is < 0 or > 0.45)
            throw new ArgumentOutOfRangeException(nameof(MaterialParameters));
        if(!double.IsFinite(Dispersion) || Dispersion is <0 or >0.12 || !double.IsFinite(LightAngle) || LightAngle is <0 or >360)
            throw new ArgumentOutOfRangeException(nameof(MaterialParameters));
    }
}
public sealed record WallpaperDescriptor(string SourceId, string Path, WallpaperPosition Position,
    DipRect PhysicalImageArea, uint BackgroundColor);
public sealed class WallpaperPixels
{
    public string SourceId { get; }
    public int Width { get; }
    public int Height { get; }
    public int OriginalWidth { get; }
    public int OriginalHeight { get; }
    public byte[] Bgra { get; }
    public long ByteCount => Bgra.LongLength;
    public WallpaperPixels(string sourceId, int width, int height, int originalWidth, int originalHeight, byte[] ownedBgra)
    {
        if (width is < 1 or > 768 || height is < 1 or > 768 || originalWidth < 1 || originalHeight < 1
            || ownedBgra.Length != checked(width * height * 4)) throw new ArgumentException("Invalid bounded wallpaper pixels");
        SourceId = sourceId; Width = width; Height = height; OriginalWidth = originalWidth; OriginalHeight = originalHeight; Bgra = ownedBgra;
    }
}
public sealed record MaterialKey(string SourceId, string MonitorId, double DpiScale, DipRect PhysicalViewport,
    double LogicalScale, MaterialParameters Parameters);
public sealed record MaterialFrame(MaterialKey Key, DipRect OuterPhysicalRect, BitmapSource Bitmap,
    PixelBoundsAnalysis Analysis, long ByteCount, double RenderMilliseconds, double MaximumDisplacement);

// Small channel bounds per tile. Conservative bounds support readable local text protection without retaining a second full bitmap.
public sealed class PixelBoundsAnalysis
{
    public const int TileSize = 16;
    private readonly byte[] minimum, maximum;
    private readonly byte[] pixels;
    public int Width { get; }
    public int Height { get; }
    private readonly int columns;
    public long ByteCount => minimum.Length + maximum.Length + pixels.LongLength;
    public PixelBoundsAnalysis(byte[] bgra, int width, int height)
    {
        pixels=bgra;
        Width = width; Height = height; columns = (width + TileSize - 1) / TileSize;
        var rows = (height + TileSize - 1) / TileSize;
        minimum = Enumerable.Repeat((byte)255, columns * rows * 3).ToArray(); maximum = new byte[minimum.Length];
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var tile = ((y / TileSize) * columns + x / TileSize) * 3; var pixel = (y * width + x) * 4;
            for (var c = 0; c < 3; c++)
            { minimum[tile+c] = Math.Min(minimum[tile+c], bgra[pixel+c]); maximum[tile+c] = Math.Max(maximum[tile+c], bgra[pixel+c]); }
        }
    }
    // Read actual composited pixels for an entire semantic surface, rather than whitening individual glyphs.
    public (double Minimum,double Maximum) LuminanceRange(System.Windows.Rect area,Color accent,double accentOpacity,Color veil,double veilOpacity)
    {
        if(area.X<0 || area.Y<0 || area.Right>Width || area.Bottom>Height)
        {
            var black=TextContrast.Blend(TextContrast.Blend(Colors.Black,accent,accentOpacity),veil,veilOpacity);
            var white=TextContrast.Blend(TextContrast.Blend(Colors.White,accent,accentOpacity),veil,veilOpacity);
            return(TextContrast.Luminance(black),TextContrast.Luminance(white));
        }
        var low=1d;var high=0d;
        var left=Math.Clamp((int)Math.Floor(area.X),0,Width-1);var top=Math.Clamp((int)Math.Floor(area.Y),0,Height-1);
        var right=Math.Clamp((int)Math.Ceiling(area.Right),left+1,Width);var bottom=Math.Clamp((int)Math.Ceiling(area.Bottom),top+1,Height);
        for(var y=top;y<bottom;y++)for(var x=left;x<right;x++)
        {
            var index=(y*Width+x)*4;
            var color=Color.FromRgb(pixels[index+2],pixels[index+1],pixels[index]);
            if(accentOpacity>0)color=TextContrast.Blend(color,accent,accentOpacity);
            if(veilOpacity>0)color=TextContrast.Blend(color,veil,veilOpacity);
            var luminance=TextContrast.Luminance(color);low=Math.Min(low,luminance);high=Math.Max(high,luminance);
        }
        return(low,high);
    }
    public (Color Minimum, Color Maximum) Bounds(double x, double y, double width, double height)
    {
        if (x < 0 || y < 0 || x + width > Width || y + height > Height) return (Colors.Black, Colors.White);
        byte b = 255, g = 255, r = 255, maxB = 0, maxG = 0, maxR = 0;
        var right = Math.Min(columns - 1, (int)((x + width) / TileSize));
        var bottom = Math.Min((Height - 1) / TileSize, (int)((y + height) / TileSize));
        for (var row = (int)y / TileSize; row <= bottom; row++) for (var col = (int)x / TileSize; col <= right; col++)
        {
            var i = (row * columns + col) * 3;
            b = Math.Min(b, minimum[i]); g = Math.Min(g, minimum[i+1]); r = Math.Min(r, minimum[i+2]);
            maxB = Math.Max(maxB, maximum[i]); maxG = Math.Max(maxG, maximum[i+1]); maxR = Math.Max(maxR, maximum[i+2]);
        }
        return (Color.FromRgb(r,g,b), Color.FromRgb(maxR,maxG,maxB));
    }
}
