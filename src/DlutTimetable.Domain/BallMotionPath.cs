namespace DlutTimetable.Domain;

public readonly record struct BallMotionPose(DipRect Bounds, double Crop, double Radius, double TextureOpacity, double BallOpacity, double BallScale = 1);

/// <summary>Own-texture curtain, then bounded convergence to the orb anchor. Progress 0=panel, 1=orb.</summary>
public static class BallMotionPath
{
    public const double MinimumBallScale=0.96;
    // An inward-only damped oscillation keeps both rebounds inside the existing
    // screen envelope. Endpoint value and velocity are zero; no settling timer.
    public static BallMotionPose Settle(BallMotionPose pose,double fraction,double physicalScale,bool reduced)
    {
        if(!double.IsFinite(fraction) || fraction is < 0 or > 1 || !double.IsFinite(physicalScale) || physicalScale<=0)
            throw new ArgumentOutOfRangeException(nameof(fraction));
        if(reduced || fraction<=0.55 || fraction>=1)return pose;
        var t=(fraction-0.55)/0.45;
        var wave=Math.Sin(2*Math.PI*t);
        var envelope=Math.Min(1,4*Math.Exp(-3.5*t)*wave*wave*(1-t)*(1-t));
        var inset=Math.Min(2*physicalScale,Math.Min(pose.Bounds.Width,pose.Bounds.Height)/4)*envelope;
        return pose with {Bounds=new(pose.Bounds.X+inset,pose.Bounds.Y+inset,
            pose.Bounds.Width-2*inset,pose.Bounds.Height-2*inset),BallScale=1-(1-MinimumBallScale)*envelope};
    }
    public static BallMotionPose Evaluate(double progress, DipRect panel, DipRect ball, bool reduced = false)
    {
        if (!double.IsFinite(progress) || progress is < 0 or > 1 || !Valid(panel) || !Valid(ball))
            throw new ArgumentOutOfRangeException(nameof(progress));
        var corner=18*panel.Height/468;
        if (reduced) return new(panel,1,corner,1-progress,progress);
        const double curtainEnd = 0.34, ribbon = 0.14;
        var crop = 1 - (1-ribbon)*Smooth(Math.Min(1,progress/curtainEnd));
        if (progress <= curtainEnd) return new(panel with {Height=panel.Height*crop},crop,Lerp(corner,corner*0.45,Smooth(progress/curtainEnd)),1,0);
        var t=(progress-curtainEnd)/(1-curtainEnd);
        var eased=Smooth(t);
        var width=Lerp(panel.Width,ball.Width,eased);
        var height=Lerp(panel.Height*ribbon,ball.Height,eased);
        // Different bounded axis curves create an anchor path without overshooting the endpoint envelope.
        var x=Lerp(panel.X,ball.X,Smooth(Math.Pow(t,1.15)));
        var y=Lerp(panel.Y,ball.Y,Smooth(Math.Pow(t,0.9)));
        x=Math.Clamp(x,Math.Min(panel.X,ball.X),Math.Max(panel.X+panel.Width,ball.X+ball.Width)-width);
        y=Math.Clamp(y,Math.Min(panel.Y,ball.Y),Math.Max(panel.Y+panel.Height,ball.Y+ball.Height)-height);
        return new(new(x,y,width,height),ribbon,Lerp(corner*0.45,ball.Width/2,eased),
            1-Smooth(Math.Clamp((progress-0.84)/0.16,0,1)),Smooth(Math.Clamp((progress-0.78)/0.22,0,1)));
    }
    private static bool Valid(DipRect value) => double.IsFinite(value.X) && double.IsFinite(value.Y)
        && double.IsFinite(value.Width) && double.IsFinite(value.Height) && value.Width>0 && value.Height>0;
    public static double Smooth(double t) => t*t*(3-2*t);
    public static double Lerp(double from,double to,double t) => from+(to-from)*t;
}
