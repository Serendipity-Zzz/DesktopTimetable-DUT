using System.Runtime.InteropServices;
using DlutTimetable.Domain;

namespace DlutTimetable.Windows;

public sealed record DisplayLayout(string Id, DipRect WorkArea, double DpiScale, int PhysicalX, int PhysicalY,
    int PhysicalWidth, int PhysicalHeight, bool Primary)
{
    public static IReadOnlyList<DisplayLayout> All()
    {
        var result = new List<DisplayLayout>();
        NativeMethods.EnumDisplayMonitors(0, 0, (nint monitor, nint dc, ref NativeMethods.Rect rect, nint data) =>
        { result.Add(FromMonitor(monitor)); return true; }, 0);
        if (result.Count == 0) throw new InvalidOperationException("未发现可用显示器。");
        return result;
    }
    private static DisplayLayout FromMonitor(nint monitor)
    {
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>(), Device = "" };
        NativeMethods.Check(NativeMethods.GetMonitorInfo(monitor, ref info), "GetMonitorInfo");
        var hr = NativeMethods.GetDpiForMonitor(monitor, 0, out var dpi, out _);
        var ratio = hr == 0 ? dpi / 96d : 1;
        return new(info.Device, new(info.Work.Left / ratio, info.Work.Top / ratio,
            (info.Work.Right-info.Work.Left) / ratio, (info.Work.Bottom-info.Work.Top) / ratio), ratio,
            info.Monitor.Left, info.Monitor.Top, info.Monitor.Right-info.Monitor.Left, info.Monitor.Bottom-info.Monitor.Top, (info.Flags & 1) != 0);
    }
    public static DisplayLayout Select(string? id) => All().FirstOrDefault(m => m.Id == id) ?? All().First(m => m.Primary);
    public static DisplayLayout AtPhysical(int x, int y) => FromMonitor(NativeMethods.MonitorFromPoint(new(x, y), 2));
}
