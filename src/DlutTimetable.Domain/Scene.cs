namespace DlutTimetable.Domain;

public enum BallMotionPhase { None, ToBall, ToPanel }

public sealed record SceneSnapshot(TimetableState State, EdgePhase EdgePhase, TimetableGeometry Expanded,
    DipPoint BallCenter, EdgeDirection Edge, double EdgeOffset, WindowLevel EdgeLevel,
    double BallDiameter = 44, long Generation = 0, bool SettingsOpen = false, bool Exiting = false,
    BallMotionPhase BallMotion = BallMotionPhase.None)
{
    public double StripThickness { get; init; } = 6;
    public double StripLength { get; init; } = 64;
    public bool PanelVisible => State is TimetableState.Locked or TimetableState.Movable
        || State == TimetableState.EdgeDocked && EdgePhase != EdgePhase.Collapsed && EdgePhase != EdgePhase.WaitingOpen;
    public bool CommandsVisible => BallMotion == BallMotionPhase.None && (State is TimetableState.Locked or TimetableState.Movable
        || State == TimetableState.EdgeDocked && EdgePhase is EdgePhase.Expanded or EdgePhase.WaitingClose);
    public WindowLevel EffectiveLevel => State == TimetableState.EdgeDocked ? EdgeLevel : WindowLevel.Desktop;
}

public static class SceneLayout
{
    public static DipRect Panel(SceneSnapshot scene, DipRect area)
    {
        var geometry = scene.Expanded.Fit(area);
        if (scene.State != TimetableState.EdgeDocked)
            return new(geometry.TopLeft.X, geometry.TopLeft.Y, geometry.Width, geometry.Height);
        var strip = Math.Min(scene.StripThickness, (scene.Edge is EdgeDirection.Left or EdgeDirection.Right ? area.Width : area.Height)/2);
        var panelArea = scene.Edge is EdgeDirection.Left or EdgeDirection.Right
            ? area with { X = area.X + (scene.Edge == EdgeDirection.Left ? strip : 0), Width = area.Width - strip }
            : area with { Y = area.Y + (scene.Edge == EdgeDirection.Top ? strip : 0), Height = area.Height - strip };
        geometry = geometry.Fit(panelArea);
        var x = panelArea.X + Math.Clamp(scene.EdgeOffset, 0, 1) * (panelArea.Width - geometry.Width);
        var y = panelArea.Y + Math.Clamp(scene.EdgeOffset, 0, 1) * (panelArea.Height - geometry.Height);
        return scene.Edge switch
        {
            EdgeDirection.Left => new(panelArea.X, y, geometry.Width, geometry.Height),
            EdgeDirection.Right => new(panelArea.X + panelArea.Width - geometry.Width, y, geometry.Width, geometry.Height),
            EdgeDirection.Top => new(x, panelArea.Y, geometry.Width, geometry.Height),
            _ => new(x, panelArea.Y + panelArea.Height - geometry.Height, geometry.Width, geometry.Height)
        };
    }
    public static DipRect Strip(SceneSnapshot scene, DipRect area)
    {
        var offset = Math.Clamp(scene.EdgeOffset, 0, 1);
        var vertical = scene.Edge is EdgeDirection.Left or EdgeDirection.Right;
        var thickness = Math.Min(scene.StripThickness, (vertical ? area.Width : area.Height)/2);
        var length = Math.Min(scene.StripLength, vertical ? area.Height : area.Width);
        return scene.Edge switch
        {
            EdgeDirection.Left => new(area.X, area.Y + offset * (area.Height - length), thickness, length),
            EdgeDirection.Right => new(area.X + area.Width - thickness, area.Y + offset * (area.Height - length), thickness, length),
            EdgeDirection.Top => new(area.X + offset * (area.Width - length), area.Y, length, thickness),
            _ => new(area.X + offset * (area.Width - length), area.Y + area.Height - thickness, length, thickness)
        };
    }
    public static DipRect Ball(SceneSnapshot scene, DipRect area)
    {
        var diameter = Math.Min(scene.BallDiameter, Math.Min(area.Width, area.Height));
        return new(Math.Clamp(scene.BallCenter.X - diameter / 2, area.X, area.X + area.Width - diameter),
            Math.Clamp(scene.BallCenter.Y - diameter / 2, area.Y, area.Y + area.Height - diameter), diameter, diameter);
    }
}
