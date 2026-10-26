using System.Windows.Media;

namespace DlutTimetable.Windows.Materials;

internal readonly record struct SurfacePaint(double R,double G,double B,double Alpha)
{
    public SurfacePaint Over(Color color,double opacity)
    {
        var a=opacity*color.A/255;
        return new(color.R*a+R*(1-a),color.G*a+G*(1-a),color.B*a+B*(1-a),a+Alpha*(1-a));
    }
}
internal readonly record struct TextProtection(Color Ink,Color Veil,double Opacity,double GuaranteedMinimum);

// The live desktop can be any RGB value. No wallpaper lookup, screen capture or
// recurring sampling is needed: bound the final source-over colors instead.
internal sealed class TextProtectionSolver
{
    public const int Capacity=64;
    private const double FullTarget=4.85;
    private readonly Dictionary<(Color,double,SurfacePaint),TextProtection> cache=[];
    public int Count=>cache.Count;
    public TextProtection Solve(Color ink,double opacity,SurfacePaint paint=default)
    {
        if(!double.IsFinite(opacity) || opacity is <0.55 or >1)throw new ArgumentOutOfRangeException(nameof(opacity));
        var key=(ink,opacity,paint);if(cache.TryGetValue(key,out var value))return value;
        var target=opacity>=0.65?FullTarget:2+(opacity-0.55)*(FullTarget-2)/0.10;
        var dark=TextContrast.Luminance(ink)<0.5;
        ink=Color.FromRgb(ink.R,ink.G,ink.B);
        if(!Pass(ink,opacity,paint,1,dark,target))ink=dark?GlassAppearance.DarkInk:GlassAppearance.LightInk;
        var low=0d;var high=1d;
        for(var i=0;i<10;i++){var middle=(low+high)/2;if(Pass(ink,opacity,paint,middle,dark,target))high=middle;else low=middle;}
        var alpha=Math.Ceiling(high*255)/255;
        if(!Pass(ink,opacity,paint,alpha,dark,target))throw new InvalidOperationException("Text contrast cannot be protected within the configured range.");
        value=new(ink,dark?Colors.White:Colors.Black,alpha,target);
        if(cache.Count==Capacity)cache.Remove(cache.Keys.First());cache.Add(key,value);return value;
    }
    private static bool Pass(Color ink,double g,SurfacePaint paint,double p,bool dark,double target)
    {
        var veil=dark?255d:0d;
        var margin=0.2126*Minimum(ink.R,paint.R)+0.7152*Minimum(ink.G,paint.G)+0.0722*Minimum(ink.B,paint.B);
        return margin>=0.05*(target-1);
        double Minimum(byte channel,double substrate)
        {
            var minimum=double.PositiveInfinity;
            for(var b=0;b<=255;b++)
            {
                // Independent channel minima give a conservative bound over all
                // RGB combinations. Each interval also covers fractional blur colors.
                var bgBase=dark?b:Math.Min(255,b+1);var inkBase=dark?Math.Min(255,b+1):b;
                var absent=g*veil*p+bgBase*(1-g*p);
                var present=g*(veil*p+substrate*(1-p))+bgBase*(1-g*(p+paint.Alpha*(1-p)));
                var bg=dark?Math.Min(absent,present):Math.Max(absent,present);
                var fg=g*channel+inkBase*(1-g);
                // Allow four channel levels for premultiplication/blend rounding.
                var value=dark
                    ?TextContrast.Channel((int)Math.Floor(bg-4))-target*TextContrast.Channel((int)Math.Ceiling(fg+4))
                    :TextContrast.Channel((int)Math.Floor(fg-4))-target*TextContrast.Channel((int)Math.Ceiling(bg+4));
                minimum=Math.Min(minimum,value);
            }
            return minimum;
        }
    }
}
