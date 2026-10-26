using System.Runtime.InteropServices;

namespace DlutTimetable.Windows;

internal static class NativeMethods
{
    internal const uint NoActivate = 0x10, NoZOrder = 4, NoSize = 1, NoMove = 2;
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; public Point(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct MonitorInfo
    {
        public int Size; public Rect Monitor, Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    internal delegate bool MonitorCallback(nint monitor, nint dc, ref Rect rect, nint data);
    internal delegate bool WindowCallback(nint window, nint data);
    internal delegate void WindowEventCallback(nint hook, uint eventType, nint window, int objectId, int childId, uint threadId, uint time);
    [StructLayout(LayoutKind.Sequential)] internal struct MouseTracking
    { public uint Size, Flags; public nint Window; public uint HoverTime; }
    [DllImport("user32", CharSet = CharSet.Unicode)] internal static extern nint FindWindow(string? name, string? title);
    [DllImport("user32", CharSet = CharSet.Unicode)] internal static extern nint FindWindowEx(nint parent, nint after, string? name, string? title);
    [DllImport("user32", CharSet = CharSet.Unicode)] internal static extern int GetClassName(nint window, System.Text.StringBuilder name, int maximum);
    [DllImport("user32")] internal static extern bool EnumWindows(WindowCallback callback, nint data);
    [DllImport("user32")] internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32", SetLastError = true)] internal static extern nint SetWinEventHook(uint min, uint max, nint module, WindowEventCallback callback, uint process, uint thread, uint flags);
    [DllImport("user32")] internal static extern bool UnhookWinEvent(nint hook);
    [DllImport("user32", EntryPoint = "GetWindowLongPtrW")] internal static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] internal static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32", SetLastError = true)] internal static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32")] internal static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32")] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32")] internal static extern nint WindowFromPoint(Point point);
    [DllImport("user32")] internal static extern nint SetCapture(nint hwnd);
    [DllImport("user32")] internal static extern bool ReleaseCapture();
    [DllImport("user32")] internal static extern bool TrackMouseEvent(ref MouseTracking tracking);
    [DllImport("user32")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32")] internal static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32")] internal static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32", CharSet = CharSet.Unicode)] internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("shcore")] internal static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [DllImport("user32", CharSet = CharSet.Unicode)] internal static extern uint RegisterWindowMessage(string message);
    internal static void Check(bool value, string operation)
    { if (!value) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), operation); }
    internal static void SetLong(nint hwnd, int index, nint value)
    {
        // Win32 rejects assigning a zero owner when the current owner is already zero.
        // WPF's own SetOwnerHandle follows the same guard; avoid redundant style writes too.
        if (GetWindowLongPtr(hwnd, index) == value) return;
        Marshal.SetLastPInvokeError(0);
        var prior = SetWindowLongPtr(hwnd, index, value);
        if (prior == 0 && Marshal.GetLastPInvokeError() != 0) Check(false, "SetWindowLongPtr");
    }
}
