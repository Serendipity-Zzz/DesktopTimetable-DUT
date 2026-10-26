using System.Text;
using System.Text.Json;
using DlutTimetable.Application;
using DlutTimetable.Domain;
using DlutTimetable.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DlutTimetable.Tests;

public sealed class StorageTests : IDisposable
{
    [Fact]
    public async Task OpticalSettingsKeepLegacyDefaultsAndPersistWithoutReplacingInvalidInput()
    {
        var path=Path.Combine(folder,"optics.json");await File.WriteAllTextAsync(path,"{\"schemaVersion\":1,\"startupDesired\":false}");
        using var store=new SettingsStore(path);var original=(await store.LoadAsync()).Settings!;
        Assert.Equal(.04,original.MaterialDispersion);Assert.Equal(315,original.MaterialLightAngle);
        await store.SaveAsync(original with{MaterialDispersion=.09,MaterialLightAngle=90});
        var saved=(await store.LoadAsync()).Settings!;Assert.Equal(.09,saved.MaterialDispersion);Assert.Equal(90,saved.MaterialLightAngle);
        Assert.False(saved.StartupDesired);var bytes=await File.ReadAllBytesAsync(path);
        await Assert.ThrowsAsync<ArgumentException>(()=>store.SaveAsync(saved with{MaterialLightAngle=double.NaN}));
        Assert.Equal(bytes,await File.ReadAllBytesAsync(path));
    }
    [Fact]
    public async Task FutureSchemaIsRejectedEvenAfterThisStoreWasPreviouslyInitialized()
    {
        using var store=new SqliteStore(DbPath);await store.InitializeAsync();
        using(var db=new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {await db.OpenAsync();using var version=db.CreateCommand();version.CommandText="PRAGMA user_version=99";await version.ExecuteNonQueryAsync();}
        var bytes=await File.ReadAllBytesAsync(DbPath);
        await Assert.ThrowsAsync<InvalidDataException>(()=>store.ReadAsync());
        await Assert.ThrowsAsync<InvalidDataException>(()=>store.ReplaceAsync(ValidatedDataset.Create(Samples.Dataset)));
        Assert.Equal(bytes,await File.ReadAllBytesAsync(DbPath));
    }
    [Fact]
    public async Task NonDatabaseFileIsNeverRecreatedOrReplacedWithAnEmptyCache()
    {
        var bytes=Encoding.UTF8.GetBytes("NotSQLitePrivateSecret");await File.WriteAllBytesAsync(DbPath,bytes);
        using var store=new SqliteStore(DbPath);
        await Assert.ThrowsAsync<SqliteException>(()=>store.ReadAsync());
        await Assert.ThrowsAsync<SqliteException>(()=>store.ReplaceAsync(ValidatedDataset.Create(Samples.Dataset)));
        Assert.Equal(bytes,await File.ReadAllBytesAsync(DbPath));
    }
    [Fact]
    public async Task UnreadableConfigurationDoesNotBecomeMissingAndCannotBeOverwritten()
    {
        if(!OperatingSystem.IsWindows())return; // Windows sharing violation, our supported desktop platform.
        var path=Path.Combine(folder,"locked-settings.json");var bytes="{\"schemaVersion\":1,\"startupDesired\":false}"u8.ToArray();
        await File.WriteAllBytesAsync(path,bytes);using var store=new SettingsStore(path);
        using(var locked=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
        {
            Assert.Equal(SettingsLoadStatus.Unreadable,(await store.LoadAsync()).Status);
            await Assert.ThrowsAsync<InvalidDataException>(()=>store.SaveAsync(new()));
        }
        Assert.Equal(bytes,await File.ReadAllBytesAsync(path));
        Assert.False((await store.LoadAsync()).Settings!.StartupDesired);
    }
    private readonly string folder = Path.Combine(Path.GetTempPath(), "DlutTests-" + Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(folder, "test.db");
    public StorageTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, true);
    [Fact]
    public async Task ReplacesWholeBatchAndPersistsAcrossReopenWithoutOtherAccountLeak()
    {
        using (var store = new SqliteStore(DbPath))
        {
            await store.ReplaceAsync(ValidatedDataset.Create(Samples.Dataset));
            Assert.Null(await store.ReadAsync("another-account"));
            await store.ReplaceAsync(ValidatedDataset.Create(Samples.Dataset with { Meetings = [], VerifiedEmpty = true }));
        }
        using var reopened = new SqliteStore(DbPath);
        var data = await reopened.ReadAsync();
        Assert.NotNull(data); Assert.Empty(data.Value.Meetings);
        using var db = new SqliteConnection($"Data Source={DbPath};Pooling=False"); db.Open();
        using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM current_dataset";
        Assert.Equal(1L, cmd.ExecuteScalar());
    }
    [Fact]
    public async Task TransactionFailureLeavesPreviousDatasetAndCheckpointIntact()
    {
        using var store = new SqliteStore(DbPath);
        var prior = ValidatedDataset.Create(Samples.Dataset);
        await store.ReplaceAsync(prior);
        var now = DateTimeOffset.UtcNow;
        await store.TryBeginAsync(prior.Value.AccountId, prior.Value.Term.Id, SyncIntent.Automatic, new(2026, 10, 22), now);
        using (var db = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            db.Open(); using var cmd = db.CreateCommand();
            // Fail the second write, AFTER singleton update, to prove the transaction rolls it back.
            cmd.CommandText = "CREATE TRIGGER injected_failure BEFORE UPDATE ON sync_checkpoint BEGIN SELECT RAISE(ABORT, 'injected'); END";
            cmd.ExecuteNonQuery();
        }
        var candidate = ValidatedDataset.Create(prior.Value with { Synthetic = false, Source = "verified-fixture", Meetings = [], VerifiedEmpty = true });
        await Assert.ThrowsAsync<SqliteException>(() => store.ReplaceAsync(candidate));
        Assert.Single((await store.ReadAsync())!.Value.Meetings);
        Assert.Equal(SyncStatus.Running, (await store.ReadCheckpointAsync(prior.Value.AccountId))!.Status);
    }
    [Fact]
    public async Task CancelledReplacementAndInvalidJsonPreserveStoredData()
    {
        using var store = new SqliteStore(DbPath);
        await store.ReplaceAsync(ValidatedDataset.Create(Samples.Dataset));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReplaceAsync(
            ValidatedDataset.Create(Samples.Dataset with { Meetings = [], VerifiedEmpty = true }), cancelled.Token));
        Assert.Throws<JsonException>(() => DatasetJson.Parse("{\"schemaVersion\":\"1.0\"}"u8.ToArray()));
        Assert.Single((await store.ReadAsync())!.Value.Meetings);
    }
    [Fact]
    public void JsonRoundtripAndStrictBoundaries()
    {
        var bytes = DatasetJson.Serialize(ValidatedDataset.Create(Samples.Dataset));
        Assert.Equal(new[] { 1, 3, 5, 8 }, DatasetJson.Parse(bytes).Value.Meetings[0].Weeks.ToArray());
        var json = Encoding.UTF8.GetString(bytes);
        Assert.Throws<JsonException>(() => DatasetJson.Parse(Encoding.UTF8.GetBytes(json.Replace("\"synthetic\": true", "\"synthetic\": true, \"synthetic\": false"))));
        Assert.Throws<JsonException>(() => DatasetJson.Parse(Encoding.UTF8.GetBytes(json.Replace("\"source\":", "\"unexpected\":"))));
        Assert.Throws<InvalidDataException>(() => DatasetJson.Parse(new byte[5 * 1024 * 1024 + 1]));
    }
    [Theory]
    [InlineData("{\"schemaVersion\":99}", SettingsLoadStatus.UnsupportedVersion)]
    [InlineData("broken", SettingsLoadStatus.Corrupt)]
    [InlineData("{\"schemaVersion\":1,\"scale\":-1}", SettingsLoadStatus.Corrupt)]
    public async Task RefusesToOverwriteUnsupportedOrCorruptSettings(string content, SettingsLoadStatus expected)
    {
        var path = Path.Combine(folder, "settings.json");
        await File.WriteAllTextAsync(path, content);
        using var store = new SettingsStore(path);
        Assert.Equal(expected, (await store.LoadAsync()).Status);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new()));
        Assert.Equal(content, await File.ReadAllTextAsync(path));
    }
    [Fact]
    public async Task StripSettingsKeepOldDefaultsAndPersistBoundsWithoutOverwritingInvalidFiles()
    {
        var path=Path.Combine(folder,"strip-settings.json");
        await File.WriteAllTextAsync(path,"{\"schemaVersion\":1}");
        using var store=new SettingsStore(path);
        var old=(await store.LoadAsync()).Settings!;
        Assert.Equal(6,old.StripThickness);Assert.Equal(64,old.StripLength);
        await store.SaveAsync(old with {StripThickness=12,StripLength=120,StartupDesired=false});
        var saved=(await store.LoadAsync()).Settings!;
        Assert.Equal(12,saved.StripThickness);Assert.Equal(120,saved.StripLength);Assert.False(saved.StartupDesired);
        var bytes=await File.ReadAllBytesAsync(path);
        await Assert.ThrowsAsync<ArgumentException>(()=>store.SaveAsync(saved with {StripThickness=3}));
        await Assert.ThrowsAsync<ArgumentException>(()=>store.SaveAsync(saved with {StripLength=double.PositiveInfinity}));
        Assert.Equal(bytes,await File.ReadAllBytesAsync(path));
        await File.WriteAllTextAsync(path,"{\"schemaVersion\":1,\"stripLength\":121}");
        Assert.Equal(SettingsLoadStatus.Corrupt,(await store.LoadAsync()).Status);
        await Assert.ThrowsAsync<InvalidDataException>(()=>store.SaveAsync(old));
    }
    [Fact]
    public async Task TransparencySettingsUpgradeOldFilesAndPersistIndependentValues()
    {
        var path=Path.Combine(folder,"opacity-settings.json");
        await File.WriteAllTextAsync(path,"{\"schemaVersion\":1}");
        using var store=new SettingsStore(path);
        var old=(await store.LoadAsync()).Settings!;
        Assert.Equal(0.8,old.BackgroundTransparency);Assert.Equal(0.12,old.ForegroundTransparency);
        await store.SaveAsync(old with {BackgroundTransparency=0.91,ForegroundTransparency=0.25});
        var loaded=(await store.LoadAsync()).Settings!;
        Assert.Equal(0.91,loaded.BackgroundTransparency);Assert.Equal(0.25,loaded.ForegroundTransparency);
        await Assert.ThrowsAsync<ArgumentException>(()=>store.SaveAsync(loaded with {ForegroundTransparency=double.NaN}));
        await File.WriteAllTextAsync(path,"{\"schemaVersion\":1,\"backgroundTransparency\":0.99}");
        Assert.Equal(SettingsLoadStatus.Corrupt,(await store.LoadAsync()).Status);
    }
    [Fact]
    public async Task SettingsPreserveDisabledStartupAndColorsAcrossReplacementAndCancellation()
    {
        var path = Path.Combine(folder, "settings.json");
        using var settings = new SettingsStore(path);
        Assert.True((await settings.LoadAsync()).Settings!.StartupDesired);
        var disabled = new AppSettings { StartupInitialized = true, StartupDesired = false, CourseColorMap = new() { ["class-a"] = "#112233" } };
        await settings.SaveAsync(disabled);
        using var store = new SqliteStore(DbPath);
        await store.ReplaceAsync(ValidatedDataset.Create(Samples.Dataset));
        var loaded = (await settings.LoadAsync()).Settings!;
        Assert.False(loaded.StartupDesired); Assert.Equal("#112233", loaded.CourseColorMap["class-a"]);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => settings.SaveAsync(new(), cancelled.Token));
        Assert.False((await settings.LoadAsync()).Settings!.StartupDesired);
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
    }
    [Fact]
    public async Task AutomaticAdmissionIsAtomicAcrossIndependentConnectionsAndSurvivesFailure()
    {
        using var one = new SqliteStore(DbPath); using var two = new SqliteStore(DbPath);
        var now = new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);
        var tasks = Enumerable.Range(0, 12).Select(i => Task.Run(() => (i % 2 == 0 ? one : two)
            .TryBeginAsync("a", "term", SyncIntent.Automatic, new(2026, 10, 22), now))).ToArray();
        var results = await Task.WhenAll(tasks);
        Assert.Single(results, r => r.Allowed);
        await one.FinishAsync("a", SyncStatus.Failed);
        Assert.Equal(AdmissionReason.AlreadyAttempted, (await two.TryBeginAsync("a", "new-term", SyncIntent.Automatic, new(2026, 10, 22), now)).Reason);
        Assert.Equal(AdmissionReason.AlreadyAttempted, (await two.TryBeginAsync("a", "term", SyncIntent.Automatic, new(2026, 10, 21), now)).Reason);
        Assert.True((await two.TryBeginAsync("a", "term", SyncIntent.Automatic, new(2026, 10, 23), now.AddDays(1))).Allowed);
    }
    [Fact]
    public async Task InterruptedAttemptKeepsDateAndManualHasSeparateThrottleAndRetryAfter()
    {
        using var store = new SqliteStore(DbPath);
        var now = new DateTimeOffset(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);
        await store.TryBeginAsync("a", "term", SyncIntent.Automatic, new(2026, 10, 22), now);
        await store.RecoverInterruptedAsync();
        Assert.Equal(SyncStatus.Interrupted, (await store.ReadCheckpointAsync("a"))!.Status);
        Assert.Equal(AdmissionReason.AlreadyAttempted, (await store.TryBeginAsync("a", "term", SyncIntent.Automatic, new(2026, 10, 22), now)).Reason);
        Assert.True((await store.TryBeginAsync("a", "term", SyncIntent.Manual, new(2026, 10, 22), now)).Allowed);
        await store.FinishAsync("a", SyncStatus.Cancelled);
        Assert.Equal(AdmissionReason.RateLimited, (await store.TryBeginAsync("a", "term", SyncIntent.Manual, new(2026, 10, 22), now.AddSeconds(29))).Reason);
        Assert.True((await store.TryBeginAsync("a", "term", SyncIntent.Manual, new(2026, 10, 22), now.AddSeconds(30))).Allowed);
        await store.FinishAsync("a", SyncStatus.Failed, "429", now.AddHours(1));
        Assert.Equal(AdmissionReason.RateLimited, (await store.TryBeginAsync("a", "term", SyncIntent.Manual, new(2026, 10, 22), now.AddMinutes(1))).Reason);
        Assert.True((await store.TryBeginAsync("b", "term", SyncIntent.Automatic, new(2026, 10, 22), now)).Allowed);
    }
}
