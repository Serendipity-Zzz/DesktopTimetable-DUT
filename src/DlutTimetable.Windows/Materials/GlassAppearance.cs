using System.Windows;
using System.Windows.Media;

namespace DlutTimetable.Windows.Materials;

public readonly record struct GlassSurface(Color Foreground,Color Veil,double VeilOpacity,double Contrast);

public static class GlassAppearance
{
    public static readonly Color DarkInk=Color.FromRgb(20,23,28),LightInk=Color.FromRgb(248,250,253);
    public const double CourseTintOpacity=0.14;
    public static GlassSurface Solve(PixelBoundsAnalysis analysis,Rect area,Color accent,double accentOpacity)
    {
        const double target=4.85;
        var range=analysis.LuminanceRange(area,accent,accentOpacity,Colors.White,0);
        var darkRatio=DarkRatio(range.Minimum);var lightRatio=LightRatio(range.Maximum);
        if(darkRatio>=target || lightRatio>=target)
            return darkRatio>=lightRatio ? new(DarkInk,Colors.White,0,darkRatio) : new(LightInk,Colors.Black,0,lightRatio);
        var dark=Find(Colors.White,true);var light=Find(Colors.Black,false);
        return dark.VeilOpacity<=light.VeilOpacity ? dark : light;

        GlassSurface Find(Color veil,bool darkText)
        {
            var low=0d;var high=0.65;
            for(var i=0;i<9;i++)
            {
                var middle=(low+high)/2;
                var bounds=analysis.LuminanceRange(area,accent,accentOpacity,veil,middle);
                var ratio=darkText ? DarkRatio(bounds.Minimum) : LightRatio(bounds.Maximum);
                if(ratio>=target)high=middle;else low=middle;
            }
            // Round up to the same 8-bit alpha that WPF will draw.
            var alpha=Math.Ceiling(high*255)/255;
            var final=analysis.LuminanceRange(area,accent,accentOpacity,veil,alpha);
            return new(darkText?DarkInk:LightInk,veil,alpha,darkText?DarkRatio(final.Minimum):LightRatio(final.Maximum));
        }
    }
    private static double DarkRatio(double minimum) => minimum<TextContrast.Luminance(DarkInk)?1:(minimum+0.05)/(TextContrast.Luminance(DarkInk)+0.05);
    private static double LightRatio(double maximum) => maximum>TextContrast.Luminance(LightInk)?1:(TextContrast.Luminance(LightInk)+0.05)/(maximum+0.05);
}
