using System.Text.Json;
using DlutTimetable.Domain;
using DlutTimetable.Contracts;

namespace DlutTimetable.Infrastructure;

public sealed record AppSettings
{
    public int SchemaVersion { get; init; } = 1;
    public bool StartupInitialized { get; init; }
    public bool StartupDesired { get; init; } = true;
    public TimetableState State { get; init; } = TimetableState.Locked;
    public double X { get; init; } = 80;
    public double Y { get; init; } = 80;
    public double Scale { get; init; } = 1;
    public string? DisplayId { get; init; }
    public string? BallDisplayId { get; init; }
    public EdgeDirection EdgeDirection { get; init; } = EdgeDirection.Right;
    public WindowLevel EdgeLevel { get; init; } = WindowLevel.Desktop;
    public double EdgeOffset { get; init; } = 0.5;
    public double StripThickness { get; init; } = 6;
    public double StripLength { get; init; } = 64;
    public double BallX { get; init; } = 80;
    public double BallY { get; init; } = 80;
    public double BallDiameter { get; init; } = 44;
    public string Material { get; init; } = "ClearFrosted";
    public bool MaterialDark { get; init; }
    public double MaterialDisplacement { get; init; } = 8;
    public double MaterialDispersion {get;init;}=0.04;
    public double MaterialLightAngle {get;init;}=315;
    public double BackgroundTransparency { get; init; } = 0.80;
    public double ForegroundTransparency { get; init; } = 0.12;
    public bool ReducedMotion { get; init; }
    public int LastSelectedWeek { get; init; } = 1;
    public DateTimeOffset? SchoolRetryAfterUtc { get; init; }
    public CalendarAdjustment? CalendarAdjustment { get; init; }
    public string? AuthProfileScopeId { get; init; }
    public bool UseDefaultAuthProfile { get; init; }
    public string? AuthenticationScope(string dataDirectory)=>UseDefaultAuthProfile?null:AuthProfileScopeId??AuthProfileLocation.ScopeForDataDirectory(dataDirectory);
    public Dictionary<string, string> CourseColorMap { get; init; } = [];
}
public enum SettingsLoadStatus { Missing, Loaded, Corrupt, UnsupportedVersion, Unreadable }
public sealed record SettingsLoad(SettingsLoadStatus Status, AppSettings? Settings);

public sealed class SettingsStore(string path) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<SettingsLoad> LoadAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { return await LoadCoreAsync(ct); }
        finally { gate.Release(); }
    }
    private async Task<SettingsLoad> LoadCoreAsync(CancellationToken ct)
    {
        try
        {
            if (new FileInfo(path).Length > 1024 * 1024) return new(SettingsLoadStatus.Corrupt, null);
            var bytes = await File.ReadAllBytesAsync(path, ct);
            using var doc = JsonDocument.Parse(bytes);
            if (!doc.RootElement.TryGetProperty("schemaVersion", out var version) || !version.TryGetInt32(out var schema))
                return new(SettingsLoadStatus.Corrupt, null);
            if (schema != 1) return new(SettingsLoadStatus.UnsupportedVersion, null);
            var settings = JsonSerializer.Deserialize<AppSettings>(bytes, DatasetJson.Options);
            if (settings is null || !IsValid(settings)) return new(SettingsLoadStatus.Corrupt, null);
            return new(SettingsLoadStatus.Loaded, settings);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        { return new(SettingsLoadStatus.Corrupt, null); }
        catch(Exception ex) when(ex is FileNotFoundException or DirectoryNotFoundException)
        {return Directory.Exists(path)?new(SettingsLoadStatus.Unreadable,null):new(SettingsLoadStatus.Missing,new());}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {return new(SettingsLoadStatus.Unreadable,null);}
    }
    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        if (!IsValid(settings)) throw new ArgumentException("设置范围无效。", nameof(settings));
        await gate.WaitAsync(ct);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var current = await LoadCoreAsync(ct);
            if (current.Status is SettingsLoadStatus.Corrupt or SettingsLoadStatus.UnsupportedVersion or SettingsLoadStatus.Unreadable)
                throw new InvalidDataException("原设置损坏或版本较新，未覆盖原文件。");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, DatasetJson.Options);
            if (bytes.Length > 1024 * 1024) throw new InvalidDataException("设置超过 1 MiB 上限。");
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await file.WriteAsync(bytes, ct);
                await file.FlushAsync(ct);
                file.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
            gate.Release();
        }
    }
    private static bool IsValid(AppSettings s) => s.SchemaVersion == 1
        && double.IsFinite(s.Scale) && s.Scale is >= 0.1 and <= 2
        && double.IsFinite(s.X) && double.IsFinite(s.Y) && double.IsFinite(s.BallX) && double.IsFinite(s.BallY)
        && double.IsFinite(s.EdgeOffset) && s.EdgeOffset is >= 0 and <= 1
        && double.IsFinite(s.StripThickness) && s.StripThickness is >= 4 and <= 12
        && double.IsFinite(s.StripLength) && s.StripLength is >= 48 and <= 120
        && s.BallDiameter is >= 24 and <= 120 && s.LastSelectedWeek is >= 1 and <= 60
        && Enum.IsDefined(s.State) && Enum.IsDefined(s.EdgeDirection) && Enum.IsDefined(s.EdgeLevel)
        && double.IsFinite(s.MaterialDisplacement) && s.MaterialDisplacement is >= 0 and <= 12
        && double.IsFinite(s.MaterialDispersion) && s.MaterialDispersion is >=0 and <=0.12
        && double.IsFinite(s.MaterialLightAngle) && s.MaterialLightAngle is >=0 and <=360
        && double.IsFinite(s.BackgroundTransparency) && s.BackgroundTransparency is >= 0.40 and <= 0.95
        && double.IsFinite(s.ForegroundTransparency) && s.ForegroundTransparency is >= 0 and <= 0.45
        && s.Material is "ClearFrosted" or "LiquidEdge" or "Solid" && s.CourseColorMap is not null
        && AuthProfileLocation.ValidScope(s.AuthProfileScopeId)
        && (!s.UseDefaultAuthProfile || s.AuthProfileScopeId is null)
        && (s.CalendarAdjustment is null || s.CalendarAdjustment.Errors().Count==0);
    public void Dispose() => gate.Dispose();
}
