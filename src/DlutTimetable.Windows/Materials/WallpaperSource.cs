using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DlutTimetable.Domain;

namespace DlutTimetable.Windows.Materials;

public static class WallpaperSource
{
    [ComImport, Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorId, [MarshalAs(UnmanagedType.LPWStr)] string path);
        void GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string monitorId, [MarshalAs(UnmanagedType.LPWStr)] out string path);
        void GetMonitorDevicePathAt(uint index, [MarshalAs(UnmanagedType.LPWStr)] out string monitorId);
        void GetMonitorDevicePathCount(out uint count);
        void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId, out NativeMethods.Rect rect);
        void SetBackgroundColor(uint color);
        void GetBackgroundColor(out uint color);
        void SetPosition(WallpaperPosition position);
        void GetPosition(out WallpaperPosition position);
    }
    public static WallpaperDescriptor Resolve(DisplayLayout display)
    {
        var type = Type.GetTypeFromCLSID(new("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD"), throwOnError: true)!;
        var service = (IDesktopWallpaper)Activator.CreateInstance(type)!;
        try
        {
            service.GetMonitorDevicePathCount(out var count);
            if (count > 64) throw new InvalidDataException("Unsupported monitor count");
            for (uint i = 0; i < count; i++)
            {
                service.GetMonitorDevicePathAt(i, out var monitor);
                service.GetMonitorRECT(monitor, out var rect);
                if (rect.Left != display.PhysicalX || rect.Top != display.PhysicalY || rect.Right - rect.Left != display.PhysicalWidth || rect.Bottom - rect.Top != display.PhysicalHeight) continue;
                service.GetWallpaper(monitor, out var path); service.GetPosition(out var position); service.GetBackgroundColor(out var color);
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || new Uri(path).IsUnc)
                    throw new InvalidDataException("No supported local static wallpaper");
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > 64 * 1024 * 1024) throw new InvalidDataException("Wallpaper missing or above decode input bound");
                var area = new DipRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
                if (position == WallpaperPosition.Span)
                {
                    var screens = DisplayLayout.All();
                    var left = screens.Min(m => m.PhysicalX); var top = screens.Min(m => m.PhysicalY);
                    area = new(left, top, screens.Max(m => m.PhysicalX + m.PhysicalWidth) - left, screens.Max(m => m.PhysicalY + m.PhysicalHeight) - top);
                }
                var stamp = FormattableString.Invariant($"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{position}|{area.X},{area.Y},{area.Width},{area.Height}|{color}");
                var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stamp)));
                return new(id, path, position, area, color);
            }
            throw new InvalidDataException("Wallpaper monitor mapping unavailable");
        }
        finally { Marshal.ReleaseComObject(service); }
    }
    public static WallpaperPixels Decode(WallpaperDescriptor descriptor, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var stream = new FileStream(descriptor.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length > 64 * 1024 * 1024) throw new InvalidDataException("Wallpaper input too large");
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
        var header = decoder.Frames[0];
        var width = header.PixelWidth; var height = header.PixelHeight;
        if (width < 1 || height < 1 || width > 65536 || height > 65536) throw new InvalidDataException("Invalid wallpaper dimensions");
        var factor = Math.Min(1, 768d / Math.Max(width, height));
        stream.Position = 0;
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile; image.StreamSource = stream;
        image.DecodePixelWidth = Math.Max(1, (int)Math.Round(width * factor));
        image.DecodePixelHeight = Math.Max(1, (int)Math.Round(height * factor)); image.EndInit();
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[checked(converted.PixelWidth * converted.PixelHeight * 4)];
        ct.ThrowIfCancellationRequested(); converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        return new(descriptor.SourceId, converted.PixelWidth, converted.PixelHeight, width, height, pixels);
    }
}
