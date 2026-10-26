using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DlutTimetable.Application;
using DlutTimetable.Domain;
using Microsoft.Data.Sqlite;

namespace DlutTimetable.Infrastructure;

internal enum DatasetWriteStage { DatasetWritten, BeforeCommit, Committed }

public sealed class SqliteStore : ICurrentDatasetStore, ISyncCheckpointStore, IPrivateNoteStore, IDisposable
{
    private readonly string connectionString;
    private readonly SemaphoreSlim writer = new(1, 1);
    private bool initialized;
    private readonly Action<DatasetWriteStage>? writeProbe;
    public SqliteStore(string path):this(path,null) { }
    // Only the isolated crash probe can pause a writer at transaction boundaries.
    internal SqliteStore(string path,Action<DatasetWriteStage>? writeProbe)
    {
        this.writeProbe=writeProbe;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder
        { DataSource = Path.GetFullPath(path), Pooling = false, DefaultTimeout = 5 }.ToString();
    }
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await writer.WaitAsync(ct);
        try
        {
            if (initialized) return;
            using var db = await OpenAsync(ct);
            using var version = db.CreateCommand();
            version.CommandText = "PRAGMA user_version";
            var schema = Convert.ToInt32(await version.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            if (schema is <0 or >2) throw new InvalidDataException("数据库版本不受支持，未覆盖原数据。");
            if (schema < 2)
            {
                using var tx = db.BeginTransaction();
                using var migration = db.CreateCommand();
                migration.Transaction = tx;
                if(schema==0)
                {
                    migration.CommandText = """
                        CREATE TABLE IF NOT EXISTS current_dataset (
                          singleton INTEGER PRIMARY KEY CHECK(singleton = 1), account_id TEXT NOT NULL,
                          term_id TEXT NOT NULL, schema_version TEXT NOT NULL, adapter_version TEXT NOT NULL,
                          coverage_json TEXT NOT NULL, dataset_json TEXT NOT NULL, fetched_at_utc TEXT NOT NULL,
                          content_hash TEXT NOT NULL);
                        CREATE TABLE IF NOT EXISTS sync_checkpoint (
                          account_id TEXT PRIMARY KEY, last_auto_date TEXT, last_auto_term_id TEXT,
                          last_attempt_at_utc TEXT, last_success_at_utc TEXT, last_manual_at_utc TEXT,
                          retry_after_utc TEXT, status TEXT NOT NULL, reason_code TEXT);
                        """;
                    await migration.ExecuteNonQueryAsync(ct);
                }
                migration.CommandText="""
                    CREATE TABLE IF NOT EXISTS private_note (
                      account_id TEXT NOT NULL, term_id TEXT NOT NULL, meeting_id TEXT NOT NULL,
                      note_text TEXT NOT NULL CHECK(length(note_text) BETWEEN 1 AND 4096),
                      updated_at_utc TEXT NOT NULL,
                      PRIMARY KEY(account_id,term_id,meeting_id));
                    PRAGMA user_version = 2;
                    """;
                await migration.ExecuteNonQueryAsync(ct);
                tx.Commit();
            }
            initialized = true;
        }
        finally { writer.Release(); }
    }
    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var db = new SqliteConnection(connectionString);
        try
        {
            await db.OpenAsync(ct);
            // Recheck even after initialization: an externally restored/replaced file
            // must not bypass the supported-schema guard on a cached store instance.
            using var version=db.CreateCommand();version.CommandText="PRAGMA user_version";
            var schema=Convert.ToInt32(await version.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture);
            if(schema is <0 or >2)throw new InvalidDataException("数据库版本不受支持，原数据保持不变。");
            return db;
        }
        catch { await db.DisposeAsync(); throw; }
    }
    public async Task<ValidatedDataset?> ReadAsync(string? accountId = null, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var db = await OpenAsync(ct);
        using var command = db.CreateCommand();
        command.CommandText = "SELECT dataset_json FROM current_dataset WHERE singleton=1 AND ($account IS NULL OR account_id=$account)";
        command.Parameters.AddWithValue("$account", (object?)accountId ?? DBNull.Value);
        var value = await command.ExecuteScalarAsync(ct);
        return value is string json ? DatasetJson.Parse(Encoding.UTF8.GetBytes(json), accountId) : null;
    }
    public async Task ReplaceAsync(ValidatedDataset dataset, CancellationToken ct = default)
    {
        var bytes = DatasetJson.Serialize(dataset); // Validation/serialization completes before the short transaction.
        var d = dataset.Value;
        await InitializeAsync(ct);
        await writer.WaitAsync(ct);
        try
        {
            using var db = await OpenAsync(ct);
            using var tx = db.BeginTransaction();
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO current_dataset VALUES(1,$account,$term,$schema,$adapter,$coverage,$json,$at,$hash)
                ON CONFLICT(singleton) DO UPDATE SET account_id=excluded.account_id,term_id=excluded.term_id,
                schema_version=excluded.schema_version,adapter_version=excluded.adapter_version,
                coverage_json=excluded.coverage_json,dataset_json=excluded.dataset_json,
                fetched_at_utc=excluded.fetched_at_utc,content_hash=excluded.content_hash;
                """;
            cmd.Parameters.AddWithValue("$account", d.AccountId);
            cmd.Parameters.AddWithValue("$term", d.Term.Id);
            cmd.Parameters.AddWithValue("$schema", d.SchemaVersion);
            cmd.Parameters.AddWithValue("$adapter", d.AdapterVersion);
            cmd.Parameters.AddWithValue("$coverage", System.Text.Json.JsonSerializer.Serialize(d.Coverage, DatasetJson.Options));
            cmd.Parameters.AddWithValue("$json", Encoding.UTF8.GetString(bytes));
            cmd.Parameters.AddWithValue("$at", Stamp(d.FetchedAt));
            cmd.Parameters.AddWithValue("$hash", Convert.ToHexString(SHA256.HashData(bytes)));
            await cmd.ExecuteNonQueryAsync(ct);
            writeProbe?.Invoke(DatasetWriteStage.DatasetWritten);
            // Local import never consumes or changes a network checkpoint.
            if (d.Source != "local-import" && !d.Synthetic)
            {
                cmd.CommandText = "UPDATE sync_checkpoint SET last_success_at_utc=$at,status='Succeeded',reason_code=NULL WHERE account_id=$account";
                await cmd.ExecuteNonQueryAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            writeProbe?.Invoke(DatasetWriteStage.BeforeCommit);
            tx.Commit();
            writeProbe?.Invoke(DatasetWriteStage.Committed);
        }
        finally { writer.Release(); }
    }
    public async Task<SyncAdmission> TryBeginAsync(string accountId, string termId, SyncIntent intent,
        DateOnly today, DateTimeOffset now, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        ArgumentException.ThrowIfNullOrWhiteSpace(termId);
        await InitializeAsync(ct);
        await writer.WaitAsync(ct);
        try
        {
            using var db = await OpenAsync(ct);
            using var tx = db.BeginTransaction(); // Immediate SQLite write lock also serializes other store instances.
            var prior = await ReadCheckpointCoreAsync(db, accountId, tx, ct);
            if (prior?.Status == SyncStatus.Running) return new(AdmissionReason.Busy);
            if (prior?.RetryAfterUtc is { } retry && retry > now) return new(AdmissionReason.RateLimited, retry);
            if (intent == SyncIntent.Automatic && prior?.LastAutoDate is { } used && used >= today)
                return new(AdmissionReason.AlreadyAttempted);
            if (intent == SyncIntent.Manual && prior?.LastManualAtUtc is { } manual && manual.AddSeconds(30) > now)
                return new(AdmissionReason.RateLimited, manual.AddSeconds(30));
            using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO sync_checkpoint(account_id,last_auto_date,last_auto_term_id,last_attempt_at_utc,last_manual_at_utc,status)
                VALUES($account,$date,$term,$now,$manual,'Running')
                ON CONFLICT(account_id) DO UPDATE SET last_auto_date=COALESCE($date,last_auto_date),
                last_auto_term_id=COALESCE($term,last_auto_term_id),last_attempt_at_utc=$now,
                last_manual_at_utc=COALESCE($manual,last_manual_at_utc),status='Running',reason_code=NULL;
                """;
            cmd.Parameters.AddWithValue("$account", accountId);
            cmd.Parameters.AddWithValue("$date", intent == SyncIntent.Automatic ? today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : DBNull.Value);
            cmd.Parameters.AddWithValue("$term", intent == SyncIntent.Automatic ? termId : DBNull.Value);
            cmd.Parameters.AddWithValue("$now", Stamp(now));
            cmd.Parameters.AddWithValue("$manual", intent == SyncIntent.Manual ? Stamp(now) : DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
            ct.ThrowIfCancellationRequested();
            tx.Commit();
            return new(AdmissionReason.Admitted);
        }
        finally { writer.Release(); }
    }
    public async Task FinishAsync(string accountId, SyncStatus status, string? reasonCode = null,
        DateTimeOffset? retryAfterUtc = null, CancellationToken ct = default)
    {
        if (status is SyncStatus.Running or SyncStatus.Idle) throw new ArgumentOutOfRangeException(nameof(status));
        await ExecuteWriteAsync("UPDATE sync_checkpoint SET status=$status,reason_code=$reason,retry_after_utc=$retry WHERE account_id=$account",
            new() { ["$status"] = status.ToString(), ["$reason"] = reasonCode, ["$retry"] = retryAfterUtc is { } t ? Stamp(t) : null, ["$account"] = accountId }, ct);
    }
    // Only the singleton application owner may call this once during startup, before admitting work.
    public Task RecoverInterruptedAsync(CancellationToken ct = default) => ExecuteWriteAsync(
        "UPDATE sync_checkpoint SET status='Interrupted',reason_code='ProcessInterrupted' WHERE status='Running'", [], ct);
    private async Task ExecuteWriteAsync(string sql, Dictionary<string, object?> parameters, CancellationToken ct)
    {
        await InitializeAsync(ct);
        await writer.WaitAsync(ct);
        try
        {
            using var db = await OpenAsync(ct);
            using var command = db.CreateCommand();
            command.CommandText = sql;
            foreach (var p in parameters) command.Parameters.AddWithValue(p.Key, p.Value ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(ct);
        }
        finally { writer.Release(); }
    }
    public async Task<SyncCheckpoint?> ReadCheckpointAsync(string accountId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var db = await OpenAsync(ct);
        return await ReadCheckpointCoreAsync(db, accountId, null, ct);
    }
    public async Task<PrivateNote?> ReadNoteAsync(PrivateNoteKey key,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(key);key.Validate();
        await InitializeAsync(ct);
        using var db=await OpenAsync(ct);using var command=db.CreateCommand();
        command.CommandText="SELECT note_text,updated_at_utc FROM private_note WHERE account_id=$account AND term_id=$term AND meeting_id=$meeting";
        command.Parameters.AddWithValue("$account",key.AccountId);command.Parameters.AddWithValue("$term",key.TermId);command.Parameters.AddWithValue("$meeting",key.MeetingId);
        using var reader=await command.ExecuteReaderAsync(ct);
        if(!await reader.ReadAsync(ct))return null;
        var note=new PrivateNote(key,reader.GetString(0),DateTimeOffset.Parse(reader.GetString(1),CultureInfo.InvariantCulture));
        note.Validate();return note;
    }
    public Task SaveNoteAsync(PrivateNote note,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(note);note.Validate();
        return ExecuteWriteAsync("""
            INSERT INTO private_note(account_id,term_id,meeting_id,note_text,updated_at_utc)
            VALUES($account,$term,$meeting,$text,$at)
            ON CONFLICT(account_id,term_id,meeting_id) DO UPDATE SET note_text=excluded.note_text,updated_at_utc=excluded.updated_at_utc
            """,new(){["$account"]=note.Key.AccountId,["$term"]=note.Key.TermId,["$meeting"]=note.Key.MeetingId,["$text"]=note.Text,["$at"]=Stamp(note.UpdatedAt)},ct);
    }
    public Task DeleteNoteAsync(PrivateNoteKey key,CancellationToken ct=default)
    {
        ArgumentNullException.ThrowIfNull(key);key.Validate();
        return ExecuteWriteAsync("DELETE FROM private_note WHERE account_id=$account AND term_id=$term AND meeting_id=$meeting",
            new(){["$account"]=key.AccountId,["$term"]=key.TermId,["$meeting"]=key.MeetingId},ct);
    }
    private static async Task<SyncCheckpoint?> ReadCheckpointCoreAsync(SqliteConnection db, string account,
        SqliteTransaction? tx, CancellationToken ct)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT account_id,last_auto_date,last_auto_term_id,last_attempt_at_utc,last_success_at_utc,last_manual_at_utc,retry_after_utc,status,reason_code FROM sync_checkpoint WHERE account_id=$account";
        cmd.Parameters.AddWithValue("$account", account);
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
        DateTimeOffset? Time(int i) => Text(i) is { } s ? DateTimeOffset.Parse(s, CultureInfo.InvariantCulture) : null;
        return new(reader.GetString(0), Text(1) is { } date ? DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
            Text(2), Time(3), Time(4), Time(5), Time(6), Enum.Parse<SyncStatus>(reader.GetString(7)), Text(8));
    }
    private static string Stamp(DateTimeOffset time) => time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    public void Dispose() => writer.Dispose();
}
