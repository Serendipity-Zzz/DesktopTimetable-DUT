using System.Text.Json;
using DlutTimetable.Application;

namespace DlutTimetable.Infrastructure.Installation;

public sealed record InstallationState(int SchemaVersion,string ProductId,string CurrentVersion,string? PreviousVersion,
    string DataDirectory,bool UserIntegration,DateTimeOffset InstalledAtUtc);
public sealed record InstallationRequest(string InstallDirectory,string DataDirectory,string? MigrateFrom=null,bool UserIntegration=true);
public sealed record InstallationResult(InstallationState State,string Executable,bool DataMigrated,bool Repaired=false);
public sealed record InstallationOwner(int SchemaVersion,string ProductId,string DataDirectory,bool UserIntegration);
public enum InstallCommitStage { FilesVerified,DataCommitted,StartupCommitted,PointerCommitted }

/// <summary>Versioned user install. OS integration is injected; tests never modify real Run entries.</summary>
public sealed class UserInstallation(IStartupRegistration startup,Func<string,CancellationToken,Task> stopApplication,
    Action<InstallCommitStage>? probe=null,Func<string,bool>? signatureVerifier=null)
{
    public const string ProductId="DLUTDesktopTimetable";
    public async Task<InstallationResult> InstallAsync(ReleasePackage package,InstallationRequest request,CancellationToken ct=default)
    {
        var root=InstallationPaths.Full(request.InstallDirectory);var data=InstallationPaths.Full(request.DataDirectory);
        InstallationPaths.NoLinks(root);InstallationPaths.NoLinks(data);
        if(Path.GetDirectoryName(root) is null || Nested(root,data) || Nested(data,root))throw new InvalidDataException("程序目录必须与个人数据目录分开。");
        var state=await ReadStateAsync(root,ct);
        var ownerPath=InstallationPaths.Child(root,".dlut-install-owner.json");
        InstallationOwner? owner=null;
        if(File.Exists(ownerPath))
        {
            if(new FileInfo(ownerPath).Length>32768)throw new InvalidDataException("安装所有权记录无效。");
            owner=JsonSerializer.Deserialize<InstallationOwner>(await File.ReadAllBytesAsync(ownerPath,ct),DatasetJson.Options);
            if(owner is null || owner.SchemaVersion!=1 || owner.ProductId!=ProductId || !InstallationPaths.Same(owner.DataDirectory,data)
                || owner.UserIntegration!=request.UserIntegration)throw new InvalidDataException("目标目录属于其他安装配置。");
        }
        if(System.IO.Directory.Exists(root) && state is null && owner is null && System.IO.Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidDataException("目标程序目录不为空且不属于本应用。");
        if(state is not null && !InstallationPaths.Same(state.DataDirectory,data))throw new InvalidDataException("更新不能暗中切换个人数据目录。");
        if(state is not null && state.UserIntegration!=request.UserIntegration)throw new InvalidDataException("更新不能暗中改变安装集成模式。");
        System.IO.Directory.CreateDirectory(root);
        var lockPath=InstallationPaths.Child(root,".install-lock");
        await using var lease=new FileStream(lockPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        if(owner is null)await ReleasePackage.WriteJsonAsync(ownerPath,new InstallationOwner(1,ProductId,data,request.UserIntegration),ct);
        // Re-read after acquiring the cross-process lease.
        state=await ReadStateAsync(root,ct);
        if(state is not null && (!InstallationPaths.Same(state.DataDirectory,data) || state.UserIntegration!=request.UserIntegration))
            throw new InvalidDataException("安装配置已被其他进程更改，请重新打开安装器。");
        System.IO.Directory.CreateDirectory(InstallationPaths.Child(root,"versions"));
        var canonical="versions/"+package.VersionDirectoryName;
        var verified=new Dictionary<string,ReleasePackage?>(StringComparer.OrdinalIgnoreCase);
        async Task<ReleasePackage?> ReadInstalledAsync(string? candidate,bool requireIncoming=false)
        {
            if(candidate is null)return null;
            if(verified.TryGetValue(candidate,out var cached))return !requireIncoming||cached?.Fingerprint==package.Fingerprint?cached:null;
            try
            {
                var path=InstallationPaths.Child(root,candidate);
                // The injected signature shortcut is usable only for byte-identical copies of
                // the already verified incoming package. It cannot authorize another release.
                var found=await ReleasePackage.VerifyAsync(path,requireIncoming&&package.Manifest.SignedRelease?_=>true:signatureVerifier,ct,allowExtraFiles:true);
                if(!found.OwnsVersionDirectoryName(Path.GetFileName(path)) || requireIncoming&&(found.Fingerprint!=package.Fingerprint || found.SignatureFingerprint!=package.SignatureFingerprint))return null;
                verified[candidate]=found;return found;
            }
            catch(Exception ex) when(ex is IOException or InvalidDataException or JsonException or ArgumentException)
            {verified[candidate]=null;return null;}
        }
        var relative=canonical;
        var canonicalHealthy=await ReadInstalledAsync(canonical,true);
        var canonicalPath=InstallationPaths.Child(root,canonical);
        var repairing=(System.IO.Directory.Exists(canonicalPath)||File.Exists(canonicalPath))&&canonicalHealthy is null;
        if(repairing)
        {
            // Never overwrite or rename a damaged tree: it can contain user-added files or
            // still-loaded binaries. Prefer a previously completed, verified repair copy.
            string? reusable=null;
            if(state is not null && await ReadInstalledAsync(state.CurrentVersion,true) is not null)reusable=state.CurrentVersion;
            if(reusable is null)
                foreach(var path in System.IO.Directory.EnumerateDirectories(InstallationPaths.Child(root,"versions")))
                {
                    if(!package.OwnsVersionDirectoryName(Path.GetFileName(path)))continue;
                    var candidate=Path.GetRelativePath(root,path).Replace('\\','/');
                    if(await ReadInstalledAsync(candidate,true) is not null){reusable=candidate;break;}
                }
            if(reusable is not null)relative=reusable;
            else do {relative=canonical+"-repair-"+Guid.NewGuid().ToString("N")[..8];}
                while(System.IO.Directory.Exists(InstallationPaths.Child(root,relative))||File.Exists(InstallationPaths.Child(root,relative)));
        }
        var version=InstallationPaths.Child(root,relative);
        if(!System.IO.Directory.Exists(version))
        {
            var stage=InstallationPaths.Child(root,".dlut-stage-"+Guid.NewGuid().ToString("N"));System.IO.Directory.CreateDirectory(stage);
            try
            {
                foreach(var entry in package.Manifest.Files)
                {
                    ct.ThrowIfCancellationRequested();var from=InstallationPaths.Child(package.Directory,entry.Path);var to=InstallationPaths.Child(stage,entry.Path);
                    System.IO.Directory.CreateDirectory(Path.GetDirectoryName(to)!);File.Copy(from,to,false);
                }
                File.Copy(InstallationPaths.Child(package.Directory,"release-manifest.json"),InstallationPaths.Child(stage,"release-manifest.json"));
                if(package.Manifest.SignedRelease)
                    File.Copy(InstallationPaths.Child(package.Directory,"release-manifest.p7s"),InstallationPaths.Child(stage,"release-manifest.p7s"));
                // The source object can only be constructed by VerifyAsync. Re-hashing every file
                // and comparing the raw manifest fingerprint carries its verified trust to the copy.
                var copied=await ReleasePackage.VerifyAsync(stage,package.Manifest.SignedRelease?_=>true:null,ct);
                if(copied.Fingerprint!=package.Fingerprint || copied.SignatureFingerprint!=package.SignatureFingerprint)throw new InvalidDataException("发布包复制后发生变化。");
                // Both resolved endpoints are checked beneath this exact install root before moving the tree.
                InstallationPaths.Child(root,Path.GetRelativePath(root,stage));InstallationPaths.Child(root,relative);
                System.IO.Directory.Move(stage,version);
            }
            finally {if(System.IO.Directory.Exists(stage))InstallationPaths.DeleteOwnedStage(root,stage);}
        }
        else if(await ReadInstalledAsync(relative,true) is null)throw new InvalidDataException("程序版本校验失败。");
        // Keep only a verified rollback target, never the damaged copy just replaced.
        var previous=state?.CurrentVersion==relative?state.PreviousVersion:state?.CurrentVersion;
        if(await ReadInstalledAsync(previous) is null)
            previous=state?.PreviousVersion is {} fallback && fallback!=relative && await ReadInstalledAsync(fallback) is not null?fallback:null;
        if(previous==relative)previous=null;
        probe?.Invoke(InstallCommitStage.FilesVerified);
        // Only now interrupt an existing session. No name-based process termination.
        await stopApplication(data,ct);
        if(request.MigrateFrom is {} migrateSource && !InstallationPaths.Same(migrateSource,data))await stopApplication(InstallationPaths.Full(migrateSource),ct);
        PreparedUserDataMigration? migration=null;byte[]? settingsBefore=null;
        string? startupBefore=null,startupAfter=null;var startupAttempted=false;var settingsWritten=false;var startupDesired=true;
        System.IO.Directory.CreateDirectory(data);
        var settingsPath=InstallationPaths.Child(data,"settings.json");
        try
        {
            if(request.MigrateFrom is {} source && !InstallationPaths.Same(source,data))migration=await UserDataMigration.PrepareAsync(source,data,ct);
            using var settingsStore=new SettingsStore(settingsPath);
            var load=await settingsStore.LoadAsync(ct);
            if(load.Status is not (SettingsLoadStatus.Missing or SettingsLoadStatus.Loaded))throw new InvalidDataException("个人设置损坏或版本不支持，未更新。");
            settingsBefore=File.Exists(settingsPath)?await File.ReadAllBytesAsync(settingsPath,ct):null;
            migration?.Commit();probe?.Invoke(InstallCommitStage.DataCommitted);
            var settings=migration?.Settings??load.Settings??new();
            startupDesired=settings.StartupDesired;
            var executable=InstallationPaths.Child(version,package.Manifest.EntryPoint);
            if(request.UserIntegration)
            {
                startupBefore=startup.Read();startupAfter=settings.StartupDesired?StartupCommand(executable):null;
                startupAttempted=true;startup.Write(startupAfter);
                if(!string.Equals(startup.Read(),startupAfter,StringComparison.OrdinalIgnoreCase))throw new IOException("启动登记读回不一致。");
                await settingsStore.SaveAsync(settings with{StartupInitialized=true},ct);settingsWritten=true;
            }
            probe?.Invoke(InstallCommitStage.StartupCommitted);
            var next=new InstallationState(1,ProductId,relative,previous,
                data,request.UserIntegration,DateTimeOffset.UtcNow);
            ct.ThrowIfCancellationRequested();
            await ReleasePackage.WriteJsonAsync(InstallationPaths.Child(root,"install-state.json"),next,ct);
            // Observers after the atomic pointer write cannot turn a committed install into a rollback.
            try {probe?.Invoke(InstallCommitStage.PointerCommitted);}catch { }
            return new(next,executable,migration is not null,repairing);
        }
        catch(Exception original)
        {
            var failures=new List<Exception>();
            try {if(startupAttempted && startupDesired && string.Equals(startup.Read(),startupAfter,StringComparison.OrdinalIgnoreCase))startup.Write(startupBefore);}
            catch(Exception ex){failures.Add(ex);}
            try
            {
                if(migration is not null)migration.Rollback();
                else if(settingsWritten)
                {
                    if(settingsBefore is null){if(File.Exists(settingsPath))File.Delete(settingsPath);}
                    else await RestoreBytesAsync(settingsPath,settingsBefore);
                }
            }
            catch(Exception ex){failures.Add(ex);}
            if(failures.Count>0)throw new AggregateException("安装未完成，部分回滚需要恢复；原备份已保留。",new[]{original}.Concat(failures));
            throw;
        }
        finally {migration?.Dispose();}
    }
    public static async Task<InstallationState?> ReadStateAsync(string root,CancellationToken ct=default)
    {
        var path=InstallationPaths.Child(root,"install-state.json");if(!File.Exists(path))return null;
        if(new FileInfo(path).Length>32768)throw new InvalidDataException("安装记录过大。");
        var state=JsonSerializer.Deserialize<InstallationState>(await File.ReadAllBytesAsync(path,ct),DatasetJson.Options)??throw new InvalidDataException("安装记录无效。");
        if(state.SchemaVersion!=1 || state.ProductId!=ProductId || !state.CurrentVersion.StartsWith("versions/",StringComparison.Ordinal)
            || state.PreviousVersion is {} previous && !previous.StartsWith("versions/",StringComparison.Ordinal))throw new InvalidDataException("安装记录不受支持。");
        InstallationPaths.Child(root,state.CurrentVersion);if(state.PreviousVersion is {} value)InstallationPaths.Child(root,value);
        if(!Path.IsPathFullyQualified(state.DataDirectory))throw new InvalidDataException("安装数据路径无效。");
        return state;
    }
    public static string StartupCommand(string executable)
    {
        if(!Path.IsPathFullyQualified(executable) || executable.IndexOfAny(['"','\r','\n','\0'])>=0 || Path.GetFileName(executable)!="DlutTimetable.Desktop.exe")throw new InvalidDataException("启动路径无效。");
        var command="\""+InstallationPaths.Full(executable)+"\" --background";
        if(command.Length>260)throw new InvalidDataException("启动路径超过 Windows 限制。");return command;
    }
    private static bool Nested(string parent,string child)=>InstallationPaths.Same(parent,child)||child.StartsWith(parent+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    private static async Task RestoreBytesAsync(string path,byte[] bytes)
    {
        var temp=path+".restore-"+Guid.NewGuid().ToString("N");await File.WriteAllBytesAsync(temp,bytes);
        if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
    }
}
