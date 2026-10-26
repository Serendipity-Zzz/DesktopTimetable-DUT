using System.Text;
using DlutTimetable.Contracts;
using Microsoft.Data.Sqlite;

namespace DlutTimetable.Infrastructure.Installation;

public sealed class PreparedUserDataMigration : IDisposable
{
    private readonly string destination,stage,sourceDirectory;
    private bool committed;
    private readonly List<(string Name,string? Backup)> applied=[];
    internal PreparedUserDataMigration(string sourceDirectory,string destination,string stage,AppSettings settings)
    {this.sourceDirectory=sourceDirectory;this.destination=destination;this.stage=stage;Settings=settings;}
    public AppSettings Settings {get;}
    public void Commit()
    {
        if(committed)throw new InvalidOperationException("迁移已经提交。");
        try
        {
            // A migration shares the original browser profile. Record that fact at its source
            // before publishing the new reference; keep it conservatively even after rollback.
            var marker=InstallationPaths.Child(sourceDirectory,UserDataMigration.SharedProfileMarker);
            if(!File.Exists(marker))
            {
                var temp=InstallationPaths.Child(sourceDirectory,".dlut-shared-"+Guid.NewGuid().ToString("N")+".tmp");
                try {File.WriteAllText(temp,"DLUTDesktopTimetable shared authentication profile v1");File.Move(temp,marker,false);}
                finally {if(File.Exists(temp))File.Delete(temp);}
            }
            foreach(var name in new[]{"timetable.db","settings.json"})
            {
                var target=InstallationPaths.Child(destination,name);var old=InstallationPaths.Child(stage,name+".before");
                string? backup=null;
                if(File.Exists(target)){File.Copy(target,old,false);backup=old;}
                var source=InstallationPaths.Child(stage,name);
                if(File.Exists(target))File.Replace(source,target,null);else File.Move(source,target);
                applied.Add((name,backup));
            }
            committed=true;
        }
        catch {Rollback();throw;}
    }
    public void Rollback()
    {
        foreach(var entry in applied.AsEnumerable().Reverse().ToArray())
        {
            var target=InstallationPaths.Child(destination,entry.Name);
            if(entry.Backup is {} backup)File.Copy(backup,target,true);
            else if(File.Exists(target))File.Delete(target);
            applied.Remove(entry);
        }
        committed=false;
    }
    public void Dispose()
    {
        // Failed rollback retains its original backups for recovery.
        if(!committed && applied.Count>0)return;
        InstallationPaths.DeleteOwnedStage(destination,stage);
    }
}

public static class UserDataMigration
{
    public const string SharedProfileMarker=".dlut-shared-auth-profile";
    public static async Task<PreparedUserDataMigration> PrepareAsync(string source,string destination,CancellationToken ct=default)
    {
        source=InstallationPaths.Full(source);destination=InstallationPaths.Full(destination);
        if(InstallationPaths.Same(source,destination))throw new InvalidDataException("迁移来源与目标相同。");
        InstallationPaths.NoLinks(source);InstallationPaths.NoLinks(destination);
        CheckClosed(source);CheckClosed(destination);
        var sourceDb=InstallationPaths.Child(source,"timetable.db");
        if(!File.Exists(sourceDb))throw new InvalidDataException("来源没有已保存课表。");
        await using(var existing=await OpenReadOnlyAsync(InstallationPaths.Child(destination,"timetable.db"),ct))
            if(existing is not null && await HasCurrentAsync(existing,ct))throw new InvalidDataException("目标已有课表，未自动覆盖。");
        using var fromSettings=new SettingsStore(InstallationPaths.Child(source,"settings.json"));
        using var toSettings=new SettingsStore(InstallationPaths.Child(destination,"settings.json"));
        var from=await fromSettings.LoadAsync(ct);var to=await toSettings.LoadAsync(ct);
        if(from.Status is not (SettingsLoadStatus.Missing or SettingsLoadStatus.Loaded) || to.Status is not (SettingsLoadStatus.Missing or SettingsLoadStatus.Loaded))
            throw new InvalidDataException("设置损坏或版本不支持，未迁移。");
        var scope=(from.Settings??new()).AuthenticationScope(source);
        var settings=(from.Settings??new()) with{AuthProfileScopeId=scope,UseDefaultAuthProfile=scope is null};
        if(to.Settings?.StartupDesired==false)settings=settings with{StartupDesired=false,StartupInitialized=to.Settings.StartupInitialized};
        Directory.CreateDirectory(destination);
        var stage=InstallationPaths.Child(destination,".dlut-migration-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
        try
        {
            await using(var input=await OpenReadOnlyAsync(sourceDb,ct)??throw new InvalidDataException("无法读取来源课表。"))
            {
                if(!await HasCurrentAsync(input,ct))throw new InvalidDataException("来源没有当前课表。");
                using var command=input.CreateCommand();command.CommandText="SELECT dataset_json FROM current_dataset WHERE singleton=1";
                var json=(string)(await command.ExecuteScalarAsync(ct))!;DatasetJson.Parse(Encoding.UTF8.GetBytes(json));
                using var output=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=InstallationPaths.Child(stage,"timetable.db"),Pooling=false}.ToString());
                await output.OpenAsync(ct);input.BackupDatabase(output);
            }
            using(var store=new SqliteStore(InstallationPaths.Child(stage,"timetable.db"))){await store.InitializeAsync(ct);await store.ReadAsync(ct:ct);}
            await MergeDestinationNotesAsync(stage,destination,ct);
            using(var store=new SettingsStore(InstallationPaths.Child(stage,"settings.json")))await store.SaveAsync(settings,ct);
            return new(source,destination,stage,settings);
        }
        catch {InstallationPaths.DeleteOwnedStage(destination,stage);throw;}
    }
    private static void CheckClosed(string root)
    {
        foreach(var suffix in new[]{"-wal","-shm","-journal"})
            if(File.Exists(InstallationPaths.Child(root,"timetable.db"+suffix)))throw new InvalidDataException("数据库尚有活动日志，请先正常打开并退出课表后再迁移。");
    }
    private static async Task<SqliteConnection?> OpenReadOnlyAsync(string path,CancellationToken ct)
    {
        if(!File.Exists(path))return null;
        var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());
        try
        {
            await connection.OpenAsync(ct);using var version=connection.CreateCommand();version.CommandText="PRAGMA user_version";
            if(Convert.ToInt32(await version.ExecuteScalarAsync(ct)) is <1 or >2)throw new InvalidDataException("数据库版本不受支持。");
            return connection;
        }
        catch {await connection.DisposeAsync();throw;}
    }
    private static async Task<bool> HasCurrentAsync(SqliteConnection connection,CancellationToken ct)
    {
        using var command=connection.CreateCommand();command.CommandText="SELECT COUNT(*) FROM current_dataset";
        var count=Convert.ToInt32(await command.ExecuteScalarAsync(ct));if(count>1)throw new InvalidDataException("数据库当前集合异常。");return count==1;
    }
    private static async Task MergeDestinationNotesAsync(string stage,string destination,CancellationToken ct)
    {
        await using var existing=await OpenReadOnlyAsync(InstallationPaths.Child(destination,"timetable.db"),ct);if(existing is null)return;
        using var exists=existing.CreateCommand();exists.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='private_note'";
        if(Convert.ToInt32(await exists.ExecuteScalarAsync(ct))==0)return;
        using var output=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=InstallationPaths.Child(stage,"timetable.db"),Pooling=false}.ToString());await output.OpenAsync(ct);
        using var transaction=output.BeginTransaction();
        using var read=existing.CreateCommand();read.CommandText="SELECT account_id,term_id,meeting_id,note_text,updated_at_utc FROM private_note LIMIT 10001";
        await using var rows=await read.ExecuteReaderAsync(ct);var count=0;
        while(await rows.ReadAsync(ct))
        {
            if(++count>10000)throw new InvalidDataException("私人备注数量超出迁移上限。");
            using var write=output.CreateCommand();write.Transaction=transaction;
            write.Parameters.AddWithValue("$a",rows.GetString(0));write.Parameters.AddWithValue("$t",rows.GetString(1));write.Parameters.AddWithValue("$m",rows.GetString(2));
            write.CommandText="SELECT note_text FROM private_note WHERE account_id=$a AND term_id=$t AND meeting_id=$m";
            var old=await write.ExecuteScalarAsync(ct);var text=rows.GetString(3);
            if(old is string value && value!=text)throw new InvalidDataException("目标与来源的私人备注冲突，未覆盖。");
            if(old is string)continue;
            write.CommandText="INSERT INTO private_note VALUES($a,$t,$m,$text,$at)";
            write.Parameters.AddWithValue("$text",text);write.Parameters.AddWithValue("$at",rows.GetString(4));await write.ExecuteNonQueryAsync(ct);
        }
        transaction.Commit();
    }
}
