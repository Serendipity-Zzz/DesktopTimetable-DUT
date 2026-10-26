namespace DlutTimetable.Domain;

public enum TimetableState { Locked, Movable, EdgeDocked, Ball }
public enum EdgePhase { Collapsed, WaitingOpen, Opening, Expanded, WaitingClose, Closing }
public enum EdgeDirection { Left, Top, Right, Bottom }
public enum WindowLevel { Desktop, Topmost }
public enum AppLifecycle { Booting, Running, Exiting }
public readonly record struct DipPoint(double X, double Y);
public readonly record struct DipRect(double X, double Y, double Width, double Height);
public readonly record struct TimetableGeometry
{
    public DipPoint TopLeft { get; }
    public double Scale { get; }
    public double Width => 780 * Scale;
    public double Height => 468 * Scale;
    public TimetableGeometry(DipPoint topLeft, double scale)
    {
        if (!double.IsFinite(topLeft.X) || !double.IsFinite(topLeft.Y)
            || !double.IsFinite(scale) || scale is < 0.1 or > 2)
            throw new ArgumentOutOfRangeException(nameof(scale));
        TopLeft = topLeft;
        Scale = scale;
    }
    public TimetableGeometry Fit(DipRect workArea)
    {
        if (!double.IsFinite(workArea.X) || !double.IsFinite(workArea.Y) || !double.IsFinite(workArea.Width)
            || !double.IsFinite(workArea.Height) || workArea.Width < 78 || workArea.Height < 46.8)
            throw new ArgumentOutOfRangeException(nameof(workArea));
        var scale = Math.Min(Scale, Math.Min(workArea.Width / 780, workArea.Height / 468));
        return new(new(Math.Clamp(TopLeft.X, workArea.X, workArea.X + workArea.Width - 780 * scale),
            Math.Clamp(TopLeft.Y, workArea.Y, workArea.Y + workArea.Height - 468 * scale)), scale);
    }
}
