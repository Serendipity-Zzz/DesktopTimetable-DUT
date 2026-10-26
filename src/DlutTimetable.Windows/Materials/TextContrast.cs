using System.Windows.Media;

namespace DlutTimetable.Windows.Materials;

public static class TextContrast
{
    private static readonly double[] LinearChannels=Enumerable.Range(0,256).Select(channel=>
    {var c=channel/255d;return c<=0.04045?c/12.92:Math.Pow((c+0.055)/1.055,2.4);}).ToArray();
    internal static double Channel(int channel)=>LinearChannels[Math.Clamp(channel,0,255)];
    public static double Luminance(Color color)
    {
        return 0.2126 * LinearChannels[color.R] + 0.7152 * LinearChannels[color.G] + 0.0722 * LinearChannels[color.B];
    }
    public static double Ratio(Color a, Color b)
    { var x = Luminance(a); var y = Luminance(b); return (Math.Max(x,y)+0.05)/(Math.Min(x,y)+0.05); }
    public static Color Blend(Color source, Color tint, double opacity) => Color.FromRgb(
        (byte)Math.Round(source.R*(1-opacity)+tint.R*opacity),
        (byte)Math.Round(source.G*(1-opacity)+tint.G*opacity),
        (byte)Math.Round(source.B*(1-opacity)+tint.B*opacity));
}
