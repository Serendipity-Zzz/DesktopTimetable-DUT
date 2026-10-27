namespace DlutTimetable.Windows;

/// <summary>Observe only the current shell process's foreground notification; no keyboard/mouse hooks.</summary>
internal sealed class DesktopShellPlacement(Action changed) : IDisposable
{
    private nint subscription, owner;
    private uint shellProcess;
    private readonly NativeMethods.WindowEventCallback callback = (_,_,window,_,_,_,_) => changed();
    public void Attach(nint desktopOwner)
    {
        if (owner == desktopOwner && subscription != 0) return;
        Dispose(); owner = desktopOwner;
        NativeMethods.GetWindowThreadProcessId(owner,out shellProcess);
        if (shellProcess == 0) return;
        // EVENT_SYSTEM_FOREGROUND, WINEVENT_OUTOFCONTEXT. A precise PID prevents a global observer.
        subscription = NativeMethods.SetWinEventHook(3,3,0,callback,shellProcess,0,0);
        if (subscription == 0) throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
    }
    public IReadOnlyList<nint> Anchors(IReadOnlySet<nint> ownWindows)
    {
        var windows = new List<nint>();
        nint shellRoot = 0;
        NativeMethods.EnumWindows((window,_) =>
        {
            windows.Add(window);
            NativeMethods.GetWindowThreadProcessId(window,out var process);
            if (process == shellProcess)
            {
                var name = new System.Text.StringBuilder(128); NativeMethods.GetClassName(window,name,name.Capacity);
                if (name.ToString() is "Progman" or "WorkerW" && NativeMethods.FindWindowEx(window,0,"SHELLDLL_DefView",null) != 0) shellRoot = window;
            }
            return true;
        },0);
        var index = windows.IndexOf(shellRoot);
        if (index < 0) return [1]; // Shell temporarily rebuilding: preserve the conservative bottom tier.
        var anchors=new List<nint>();
        for (var i=index-1;i>=0;i--)
        {
            var window=windows[i];
            var extendedStyle=NativeMethods.GetWindowLongPtr(window,-20).ToInt64();
            // Hidden system helper HWNDs and tool windows are not reliable insertion points;
            // SetWindowPos may reject them with ERROR_ACCESS_DENIED and abort app startup.
            if (ownWindows.Contains(window) || !NativeMethods.IsWindowVisible(window)
                || (extendedStyle & (NativeMethods.ExTopmost|NativeMethods.ExToolWindow)) != 0) continue;
            anchors.Add(window);
        }
        anchors.Add(1); // HWND_BOTTOM remains the final conservative fallback.
        return anchors;
    }
    public void Dispose()
    { if (subscription != 0) NativeMethods.UnhookWinEvent(subscription); subscription=0;owner=0;shellProcess=0; }
}
