using DlutTimetable.Application;
using DlutTimetable.Domain;
using DlutTimetable.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DlutTimetable.Tests;

public sealed class PrivateNoteStorageTests : IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"DlutNoteTests-"+Guid.NewGuid().ToString("N"));
    private string Database=>Path.Combine(directory,"current.db");
    public PrivateNoteStorageTests()=>Directory.CreateDirectory(directory);
    public void Dispose()
    {
        var target=new DirectoryInfo(directory);
        if(!string.Equals(target.Parent?.FullName,Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase)
            || !target.Name.StartsWith("DlutNoteTests-",StringComparison.Ordinal))throw new InvalidOperationException("Unexpected test cleanup path.");
        target.Delete(true);
    }
    private static PrivateNote Note()=>new(new(Samples.Dataset.AccountId,Samples.Dataset.Term.Id,"m1"),"虚构私人备注\n下周携带资料",Samples.Dataset.FetchedAt);
    private SqliteConnection Connection()=>new(new SqliteConnectionStringBuilder{DataSource=Database,Pooling=false}.ToString());

    [Fact]
    public async Task NotesSurviveEmptyReplacementAccountChangesAndReopenWithExactScope()
    {
        var note=Note();
        using(var store=new SqliteStore(Database))
        {
            await store.ReplaceAsync(ValidatedDataset.Create(Samples.Dataset));
            await store.SaveNoteAsync(note);
            Assert.Null(await store.ReadNoteAsync(note.Key with{AccountId="another-account"}));
            Assert.Null(await store.ReadNoteAsync(note.Key with{TermId="another-term"}));
            Assert.Null(await store.ReadNoteAsync(note.Key with{MeetingId="another-meeting"}));
            await store.ReplaceAsync(ValidatedDataset.Create(Samples.Dataset with{Meetings=[],VerifiedEmpty=true}));
            await store.ReplaceAsync(ValidatedDataset.Create(Samples.Dataset with{AccountId="another-account"}));
            await store.SaveNoteAsync(note with{Key=note.Key with{AccountId="another-account"},Text="另一虚构账号备注"});
            await store.SaveNoteAsync(note with{Key=note.Key with{TermId="another-term"},Text="另一虚构学期备注"});
        }
        using var reopened=new SqliteStore(Database);
        Assert.Equal(note,await reopened.ReadNoteAsync(note.Key));
        Assert.Equal("另一虚构账号备注",(await reopened.ReadNoteAsync(note.Key with{AccountId="another-account"}))!.Text);
        Assert.Equal("另一虚构学期备注",(await reopened.ReadNoteAsync(note.Key with{TermId="another-term"}))!.Text);
        Assert.Equal("another-account",(await reopened.ReadAsync())!.Value.AccountId);
    }

    [Fact]
    public async Task EditingDeletingAndCancelledWritesDoNotMutateOfficialDataOrCheckpoint()
    {
        using var store=new SqliteStore(Database);var note=Note();
        await store.ReplaceAsync(ValidatedDataset.Create(Samples.Dataset));
        await store.TryBeginAsync(note.Key.AccountId,note.Key.TermId,SyncIntent.Automatic,new(2026,10,23),note.UpdatedAt);
        var checkpoint=await store.ReadCheckpointAsync(note.Key.AccountId);
        var official=DatasetJson.Serialize((await store.ReadAsync())!);
        await store.SaveNoteAsync(note);
        var edited=note with{Text="已修改的虚构备注",UpdatedAt=note.UpdatedAt.AddMinutes(1)};
        await store.SaveNoteAsync(edited);
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>store.SaveNoteAsync(note,cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>store.DeleteNoteAsync(note.Key,cancelled.Token));
        Assert.Equal(edited,await store.ReadNoteAsync(note.Key));
        await store.DeleteNoteAsync(note.Key with{MeetingId="m1' OR 1=1 --"});
        Assert.Equal(edited,await store.ReadNoteAsync(note.Key));
        using(var db=Connection())
        {
            await db.OpenAsync();using var command=db.CreateCommand();command.CommandText="SELECT COUNT(*) FROM private_note";
            Assert.Equal(1L,await command.ExecuteScalarAsync());
        }
        await store.DeleteNoteAsync(note.Key);Assert.Null(await store.ReadNoteAsync(note.Key));
        Assert.Equal(official,DatasetJson.Serialize((await store.ReadAsync())!));
        Assert.Equal(checkpoint,await store.ReadCheckpointAsync(note.Key.AccountId));
    }

    [Fact]
    public async Task LegacyDatabaseMigratesWithoutReplacingDatasetOrSyncAttempt()
    {
        var note=Note();byte[] official;SyncCheckpoint? checkpoint;
        using(var seed=new SqliteStore(Database))
        {
            await seed.ReplaceAsync(ValidatedDataset.Create(Samples.Dataset));
            await seed.TryBeginAsync(note.Key.AccountId,note.Key.TermId,SyncIntent.Automatic,new(2026,10,23),note.UpdatedAt);
            official=DatasetJson.Serialize((await seed.ReadAsync())!);checkpoint=await seed.ReadCheckpointAsync(note.Key.AccountId);
        }
        using(var db=Connection())
        {
            await db.OpenAsync();using var command=db.CreateCommand();command.CommandText="DROP TABLE private_note; PRAGMA user_version=1;";await command.ExecuteNonQueryAsync();
        }
        using var migrated=new SqliteStore(Database);
        Assert.Null(await migrated.ReadNoteAsync(note.Key));await migrated.SaveNoteAsync(note);
        Assert.Equal(official,DatasetJson.Serialize((await migrated.ReadAsync())!));
        Assert.Equal(checkpoint,await migrated.ReadCheckpointAsync(note.Key.AccountId));
        Assert.Equal(note,await migrated.ReadNoteAsync(note.Key));
        using var opened=Connection();await opened.OpenAsync();using var version=opened.CreateCommand();version.CommandText="PRAGMA user_version";
        Assert.Equal(2L,await version.ExecuteScalarAsync());
    }

    [Fact]
    public async Task FutureDatabaseIsRejectedWithoutChangingItsBytes()
    {
        using(var seed=new SqliteStore(Database))await seed.ReplaceAsync(ValidatedDataset.Create(Samples.Dataset));
        using(var db=Connection())
        {
            await db.OpenAsync();using var command=db.CreateCommand();command.CommandText="PRAGMA user_version=99";await command.ExecuteNonQueryAsync();
        }
        var before=await File.ReadAllBytesAsync(Database);
        using var store=new SqliteStore(Database);
        await Assert.ThrowsAsync<InvalidDataException>(()=>store.SaveNoteAsync(Note()));
        Assert.Equal(before,await File.ReadAllBytesAsync(Database));
    }
}
