using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.InteropServices;

namespace DlutTimetable.Windows;

internal sealed class DesktopLayer : Window
{
    private HwndSource? source;
    private NativeMethods.Point press;
    private NativeMethods.Rect pressBounds;
    private bool capturing, dragged;
    private bool dragEnabled;
    private bool trackingMouse;
    private bool mouseTransparent;
    public nint Handle { get; private set; }
    public string Role { get; }
    public bool IsClosed { get; private set; }
    public bool DragEnabled
    {
        get => dragEnabled;
        set { if(dragEnabled==value)return;dragEnabled = value; if (!value) CancelGesture(); }
    }
    public double ContentScale { get; set; } = 1;
    public bool IsBall { get; init; }
    public event Action<bool>? CaptureChanged;
    public event Action<int, int, bool>? Dragged;
    public event Action? BallClicked;
    public event Action? ShellChanged;
    public event Action? MouseRegionChanged;
    public event Action<string>? GestureTrace;
    private readonly uint taskbar = NativeMethods.RegisterWindowMessage("TaskbarCreated");
    public DesktopLayer(string role, UIElement content)
    {
        Role = role; Title = "大工课表 · " + role;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; Focusable = false;
        Width = 1; Height = 1; Content = content;
        SourceInitialized += (_, _) =>
        {
            Handle = new WindowInteropHelper(this).Handle;
            source=HwndSource.FromHwnd(Handle);source.AddHook(Hook);
            var style = (long)NativeMethods.GetWindowLongPtr(Handle, -20);
            NativeMethods.SetLong(Handle, -20, (nint)((style | 0x08000080) & ~0x40000));
        };
        Closed += (_, _) => {source?.RemoveHook(Hook);source=null;Handle = 0; IsClosed = true; };
    }
    public void Ensure() => new WindowInteropHelper(this).EnsureHandle();
    private void CancelGesture()
    {
        if (!capturing) return;
        (Content as INativeCommandInput)?.SetNativePressed(false);
        capturing = false; NativeMethods.ReleaseCapture(); CaptureChanged?.Invoke(false);
    }
    public new void Hide() { CancelGesture(); base.Hide(); }
    public void SetTransparent(bool value)
    {
        mouseTransparent=value;
        Ensure(); var style = (long)NativeMethods.GetWindowLongPtr(Handle, -20);
        var desired=value ? style | 0x20 : style & ~0x20;
        if(style!=desired)NativeMethods.SetLong(Handle, -20, (nint)desired);
    }
    public void SetOwner(nint owner)
    {
        Ensure();
        // Read the actual owner so shell recovery can still repair an external change.
        if(NativeMethods.GetWindowLongPtr(Handle,-8)!=owner)NativeMethods.SetLong(Handle,-8,owner);
    }
    private nint Hook(nint hwnd, int message, nint wparam, nint lparam, ref bool handled)
    {
        var nativeCommand=Content as INativeCommandInput;
        if(message==0x7C && wparam==-20 && lparam!=0)
        {
            // WPF rewrites cached extended styles when layered opacity changes. Preserve
            // the current input policy rather than restoring an earlier transition's policy.
            var change=Marshal.PtrToStructure<ExtendedStyleChange>(lparam);
            change.New|=0x08000080;
            change.New=mouseTransparent?change.New|0x20:change.New&~0x20u;
            change.New&=~0x40000u;
            Marshal.StructureToPtr(change,lparam,false);
        }
        if (message == 0x21) { handled = true; return 3; }
        if (message == taskbar || message == 0x7E || message == 0x2E0)
        { GestureTrace?.Invoke($"SYSTEM_MESSAGE kind={message:X} value={wparam}");ShellChanged?.Invoke(); }
        if (message == 0x200 && !trackingMouse)
        {
            var track = new NativeMethods.MouseTracking { Size = (uint)Marshal.SizeOf<NativeMethods.MouseTracking>(), Flags = 2, Window = hwnd };
            trackingMouse = NativeMethods.TrackMouseEvent(ref track);
            MouseRegionChanged?.Invoke();
        }
        if (message == 0x2A3) { trackingMouse = false; MouseRegionChanged?.Invoke(); }
        // Windows replaces the second DOWN with DBLCLK for a rapid pair. Both presses
        // must reach the retargetable orb; there is deliberately no double-click delay.
        if (message is 0x201 or 0x203 && (DragEnabled || IsBall || nativeCommand is not null))
        {
            if(nativeCommand is not null && Content is UIElement {IsEnabled:false}){handled=true;return 0;}
            var localX = (short)((long)lparam & 0xFFFF);
            var localY = (short)(((long)lparam >> 16) & 0xFFFF);
            var dpi = NativeMethods.GetDpiForWindow(hwnd) / 96d;
            GestureTrace?.Invoke(FormattableString.Invariant($"PRESS local={localX},{localY} scale={ContentScale:F3} dpi={dpi:F3} enabled={DragEnabled}"));
            // Header arrows and all four command slots are excluded from the drag handle.
            if (!IsBall && nativeCommand is null && (localY > 36 * dpi * ContentScale || localX < 190 * dpi * ContentScale || localX > 510 * dpi * ContentScale)) return 0;
            NativeMethods.GetWindowRect(hwnd, out pressBounds);
            // Preserve the button-down message position. The live cursor may already have moved
            // while WPF was processing layout or a material update before dispatching this message.
            press=new(pressBounds.Left+localX,pressBounds.Top+localY);
            capturing = true; dragged = false; NativeMethods.SetCapture(hwnd); CaptureChanged?.Invoke(true);
            nativeCommand?.SetNativePressed(true);
            handled = true; return 0;
        }
        if (message == 0x200 && capturing)
        {
            NativeMethods.GetCursorPos(out var now);
            if(nativeCommand is not null)
            {
                NativeMethods.GetWindowRect(hwnd,out var bounds);
                nativeCommand.SetNativePressed(now.X>=bounds.Left && now.X<bounds.Right && now.Y>=bounds.Top && now.Y<bounds.Bottom);
                handled=true;return 0;
            }
            var dx = now.X - press.X; var dy = now.Y - press.Y;
            var threshold = 6 * NativeMethods.GetDpiForWindow(hwnd) / 96d;
            if (dx * (double)dx + dy * (double)dy > threshold * threshold) dragged = true;
            if(dragged)GestureTrace?.Invoke($"MOVE delta={dx},{dy} enabled={DragEnabled}");
            if (dragged && DragEnabled) Dragged?.Invoke(pressBounds.Left + dx, pressBounds.Top + dy, false);
            handled = true; return 0;
        }
        if (message == 0x202 && capturing)
        {
            GestureTrace?.Invoke($"RELEASE dragged={dragged} enabled={DragEnabled}");
            capturing = false; NativeMethods.ReleaseCapture(); CaptureChanged?.Invoke(false);
            NativeMethods.GetWindowRect(hwnd, out var rect);
            if(nativeCommand is not null)
            {
                nativeCommand.SetNativePressed(false);
                var x=(short)((long)lparam&0xFFFF);var y=(short)(((long)lparam>>16)&0xFFFF);
                if(x>=0 && y>=0 && x<rect.Right-rect.Left && y<rect.Bottom-rect.Top && Content is UIElement {IsEnabled:true})
                    nativeCommand.InvokeNativeClick();
            }
            else if (dragged) { if(DragEnabled)Dragged?.Invoke(rect.Left, rect.Top, true); }
            else if (IsBall) BallClicked?.Invoke();
            handled = true; return 0;
        }
        if (message == 0x215 && capturing)
        { GestureTrace?.Invoke("CAPTURE_LOST");capturing = false;nativeCommand?.SetNativePressed(false); CaptureChanged?.Invoke(false); }
        return 0;
    }
    [StructLayout(LayoutKind.Sequential)]private struct ExtendedStyleChange{public uint Old,New;}
}
