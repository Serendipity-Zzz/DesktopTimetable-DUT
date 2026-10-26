using DlutTimetable.Contracts;

namespace DlutTimetable.Application;

public sealed class AppCommandRouter(WindowStateController scene, Action<Action> dispatch, Action<AppCommand> external)
{
    public void Route(AppCommandKind kind, CommandOrigin origin) => dispatch(() =>
    {
        if (scene.Current.Exiting) return;
        var command = new AppCommand(Guid.NewGuid(), kind, origin);
        if (!scene.Execute(kind)) external(command);
    });
}
