using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Immutable;
using System.Diagnostics;
using DlutTimetable.Application;
using DlutTimetable.Contracts;
using DlutTimetable.Domain;
using DlutTimetable.Infrastructure;
using DlutTimetable.Infrastructure.Installation;

namespace DlutTimetable.Tests;

public sealed class InstallationTests : IDisposable
{
    [Fact]
    public async Task UninstallResultRejectsUntrustedWarningsAndDoesNotRevealUnverifiedFolderNames()
    {
        var original=await InstalledRemovalAsync(true);
        Directory.CreateDirectory(Path.Combine(original.InstallDirectory,"versions","PrivateCookieSecret"));
        var plan=await UserUninstallation.PrepareAsync(original.InstallDirectory,true);
        Assert.DoesNotContain(plan.Warnings,w=>w.Contains("PrivateCookieSecret",StringComparison.Ordinal));
        using var result=await CleanupAsync(plan with {Warnings=plan.Warnings.Add("PrivatePlanWarningSecret")});
        Assert.True(result.RootElement.GetProperty("passed").GetBoolean());
        Assert.DoesNotContain("PrivateCookieSecret",result.RootElement.GetRawText());
        Assert.DoesNotContain("PrivatePlanWarningSecret",result.RootElement.GetRawText());
    }
    private readonly string root=Path.Combine(Path.GetTempPath(),"DlutInstallTests-"+Guid.NewGuid().ToString("N"));
    private sealed class Startup : IStartupRegistration
    {public string? Value;public int Writes;public string? Read()=>Value;public void Write(string? value){Value=value;Writes++;}}
    public InstallationTests()=>Directory.CreateDirectory(root);
    public void Dispose()
    {
        SqliteClear();
        if(!Path.GetFullPath(root).StartsWith(Path.GetTempPath()+"DlutInstallTests-",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Unexpected cleanup root.");
        if(Directory.Exists(root))Directory.Delete(root,true);
    }
    private static void SqliteClear()=>Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    private async Task<ReleasePackage> PackageAsync(string version="0.2.0-dev")
    {
        var directory=Path.Combine(root,"package-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        var files=ImmutableArray.CreateBuilder<ReleaseFile>();
        foreach(var name in new[]{"DlutTimetable.Desktop.exe","DlutTimetable.AuthWorker.exe","hostfxr.dll","hostpolicy.dll","coreclr.dll","DlutTimetable.Desktop.runtimeconfig.json","DlutTimetable.AuthWorker.runtimeconfig.json"})
        {
            // Metadata/copy tests only; no fixture executable is launched.
            var bytes=Encoding.UTF8.GetBytes("fictional-binary:"+name+version);await File.WriteAllBytesAsync(Path.Combine(directory,name),bytes);
            files.Add(new(name,bytes.Length,Convert.ToHexString(SHA256.HashData(bytes))));
        }
        var manifest=new ReleaseManifest("DLUT Desktop Timetable",version,"win-x64",true,"10.0.13",DateTimeOffset.UtcNow,
            "DlutTimetable.Desktop.exe","DlutTimetable.AuthWorker.exe","Required",false,files.ToImmutable());
        await File.WriteAllBytesAsync(Path.Combine(directory,"release-manifest.json"),JsonSerializer.SerializeToUtf8Bytes(manifest,DatasetJson.Options));
        return await ReleasePackage.VerifyAsync(directory);
    }
    private async Task<ReleasePackage> SignedFixtureAsync(string version)
    {
        var package=await PackageAsync(version+"-dev");
        var manifest=package.Manifest with{Version=version,SignedRelease=true};
        await File.WriteAllBytesAsync(Path.Combine(package.Directory,"release-manifest.json"),JsonSerializer.SerializeToUtf8Bytes(manifest,DatasetJson.Options));
        // Structural/copy fixture only. Actual CMS/Windows trust is tested separately;
        // no fixture EXE runs and no certificate is installed in a trust store.
        await File.WriteAllTextAsync(Path.Combine(package.Directory,"release-manifest.p7s"),"synthetic-signature:"+version);
        return await ReleasePackage.VerifyAsync(package.Directory,_=>true);
    }
    [Fact]
    public async Task SignedPackageCopiesBoundSidecarAndRetainsVerifiedRollbackAndRemoval()
    {
        var first=await SignedFixtureAsync("1.0.0");
        await Assert.ThrowsAsync<InvalidDataException>(()=>ReleasePackage.VerifyAsync(first.Directory));
        var startup=new Startup();var service=new UserInstallation(startup,(_,_)=>Task.CompletedTask,signatureVerifier:_=>true);
        var request=new InstallationRequest(Path.Combine(root,"signed-app"),Path.Combine(root,"signed-data"),UserIntegration:false);
        var installed=await service.InstallAsync(first,request);
        Assert.Equal(first.SignatureFingerprint,Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(Path.GetDirectoryName(installed.Executable)!,"release-manifest.p7s")))));
        var updated=await service.InstallAsync(await SignedFixtureAsync("2.0.0"),request);
        Assert.Equal(installed.State.CurrentVersion,updated.State.PreviousVersion);
        var removal=await UserUninstallation.PrepareAsync(request.InstallDirectory,true,signatureVerifier:_=>true);
        Assert.Equal(2,removal.Files.Count(f=>f.Path.EndsWith("/release-manifest.p7s",StringComparison.Ordinal)));
        using var removed=await CleanupAsync(removal);
        Assert.True(removed.RootElement.GetProperty("passed").GetBoolean());
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(installed.Executable)!,"release-manifest.p7s")));
        Assert.True(Directory.Exists(request.DataDirectory));
    }
    [Fact]
    public async Task SignatureChangedAfterVerificationCannotBeCopiedAsTrusted()
    {
        var package=await SignedFixtureAsync("1.0.0");var stopped=0;
        await File.AppendAllTextAsync(Path.Combine(package.Directory,"release-manifest.p7s"),"changed");
        var service=new UserInstallation(new Startup(),(_,_)=>{stopped++;return Task.CompletedTask;},signatureVerifier:_=>true);
        var request=new InstallationRequest(Path.Combine(root,"changed-app"),Path.Combine(root,"changed-data"),UserIntegration:false);
        await Assert.ThrowsAsync<InvalidDataException>(()=>service.InstallAsync(package,request));
        Assert.Equal(0,stopped);Assert.False(File.Exists(Path.Combine(request.InstallDirectory,"installation.json")));
    }
    private async Task<string> DataAsync(string name,bool data=true,bool startup=false)
    {
        var directory=Path.Combine(root,name);Directory.CreateDirectory(directory);
        using(var store=new SqliteStore(Path.Combine(directory,"timetable.db")))
        {await store.InitializeAsync();if(data)await store.ReplaceAsync(ValidatedDataset.Create(Samples.Dataset));}
        using(var settings=new SettingsStore(Path.Combine(directory,"settings.json")))await settings.SaveAsync(new(){StartupInitialized=true,StartupDesired=startup,Scale=1.2});
        return directory;
    }
    [Fact]
    public async Task InstallsVersionAndPreservesExplicitStartupOptOutAcrossUpdate()
    {
        var data=await DataAsync("data");var startup=new Startup{Value="legacy-app --background"};var stopped=new List<string>();
        var service=new UserInstallation(startup,(path,_)=>{stopped.Add(path);return Task.CompletedTask;});
        var request=new InstallationRequest(Path.Combine(root,"安装 目录"),data);
        var first=await service.InstallAsync(await PackageAsync(),request);Assert.Null(startup.Value);Assert.True(File.Exists(first.Executable));
        var hash=SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(data,"timetable.db")));
        var second=await service.InstallAsync(await PackageAsync("0.3.0-dev"),request);
        Assert.Equal(first.State.CurrentVersion,second.State.PreviousVersion);Assert.Null(startup.Value);
        Assert.Equal(hash,SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(data,"timetable.db"))));
        using var settings=new SettingsStore(Path.Combine(data,"settings.json"));Assert.False((await settings.LoadAsync()).Settings!.StartupDesired);
        Assert.Equal(2,stopped.Count);
    }
    [Fact]
    public async Task FirstInstallRegistersQuotedExeAndConfirmsSettings()
    {
        var data=Path.Combine(root,"new-data");var startup=new Startup();
        var result=await new UserInstallation(startup,(_,_)=>Task.CompletedTask).InstallAsync(await PackageAsync(),new(Path.Combine(root,"app path"),data));
        Assert.Equal("\""+result.Executable+"\" --background",startup.Value);
        using var settings=new SettingsStore(Path.Combine(data,"settings.json"));Assert.True((await settings.LoadAsync()).Settings!.StartupInitialized);
    }
    [Fact]
    public async Task FailureBeforePointerPreservesOldInstallDataAndStartup()
    {
        var data=await DataAsync("data",startup:true);var request=new InstallationRequest(Path.Combine(root,"app"),data);var startup=new Startup();
        var old=await new UserInstallation(startup,(_,_)=>Task.CompletedTask).InstallAsync(await PackageAsync(),request);
        var oldRun=startup.Value;var before=await File.ReadAllBytesAsync(Path.Combine(data,"settings.json"));
        var failing=new UserInstallation(startup,(_,_)=>Task.CompletedTask,stage=>{if(stage==InstallCommitStage.StartupCommitted)throw new IOException("fixture failure");});
        var candidate=await PackageAsync("0.3.0-dev");
        await Assert.ThrowsAsync<IOException>(()=>failing.InstallAsync(candidate,request));
        Assert.Equal(oldRun,startup.Value);
        Assert.Equal(before,await File.ReadAllBytesAsync(Path.Combine(data,"settings.json")));
        Assert.Equal(old.State.CurrentVersion,(await UserInstallation.ReadStateAsync(request.InstallDirectory))!.CurrentVersion);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepairUsesFreshVerifiedCopyWithoutOverwritingDamagedFilesOrData(bool missing)
    {
        var package=await PackageAsync();var data=await DataAsync("data");var startup=new Startup();
        var request=new InstallationRequest(Path.Combine(root,"app"),data);var service=new UserInstallation(startup,(_,_)=>Task.CompletedTask);
        var first=await service.InstallAsync(package,request);var damaged=Path.Combine(Path.GetDirectoryName(first.Executable)!,"coreclr.dll");
        if(missing)File.Delete(damaged);else await File.WriteAllTextAsync(damaged,"preserve damaged original");
        var extra=Path.Combine(Path.GetDirectoryName(first.Executable)!,"user-file.txt");await File.WriteAllTextAsync(extra,"keep");
        var database=await File.ReadAllBytesAsync(Path.Combine(data,"timetable.db"));var settings=await File.ReadAllBytesAsync(Path.Combine(data,"settings.json"));
        var repaired=await service.InstallAsync(package,request);
        Assert.True(repaired.Repaired);Assert.NotEqual(first.State.CurrentVersion,repaired.State.CurrentVersion);Assert.Null(repaired.State.PreviousVersion);
        Assert.True(package.OwnsVersionDirectoryName(Path.GetFileName(Path.GetDirectoryName(repaired.Executable)!)));
        Assert.Equal(package.Fingerprint,(await ReleasePackage.VerifyAsync(Path.GetDirectoryName(repaired.Executable)!)).Fingerprint);
        if(missing)Assert.False(File.Exists(damaged));else Assert.Equal("preserve damaged original",await File.ReadAllTextAsync(damaged));
        Assert.Equal("keep",await File.ReadAllTextAsync(extra));Assert.Equal(database,await File.ReadAllBytesAsync(Path.Combine(data,"timetable.db")));
        Assert.Equal(settings,await File.ReadAllBytesAsync(Path.Combine(data,"settings.json")));Assert.Null(startup.Value);
    }
    [Fact]
    public async Task RepairReusesHealthyCopyAndPreservesOnlyVerifiedPreviousVersion()
    {
        var data=await DataAsync("data");var request=new InstallationRequest(Path.Combine(root,"app"),data,UserIntegration:false);
        var service=new UserInstallation(new Startup(),(_,_)=>Task.CompletedTask);var first=await service.InstallAsync(await PackageAsync("0.1.0-dev"),request);
        var package=await PackageAsync();var second=await service.InstallAsync(package,request);
        File.Delete(Path.Combine(Path.GetDirectoryName(second.Executable)!,"hostpolicy.dll"));
        var repair=await service.InstallAsync(package,request);Assert.Equal(first.State.CurrentVersion,repair.State.PreviousVersion);
        var extra=Path.Combine(Path.GetDirectoryName(repair.Executable)!,"extra.txt");await File.WriteAllTextAsync(extra,"preserve");
        var again=await service.InstallAsync(package,request);
        Assert.Equal(repair.State.CurrentVersion,again.State.CurrentVersion);Assert.Equal(first.State.CurrentVersion,again.State.PreviousVersion);
        Assert.Equal("preserve",await File.ReadAllTextAsync(extra));
        Assert.Equal(3,Directory.EnumerateDirectories(Path.Combine(request.InstallDirectory,"versions")).Count());
    }
    [Fact]
    public async Task FailedRepairKeepsOldPointerAndStartupThenReusesCompletedFilesOnRetry()
    {
        var data=await DataAsync("data",startup:true);var startup=new Startup();var request=new InstallationRequest(Path.Combine(root,"app"),data);
        var package=await PackageAsync();var service=new UserInstallation(startup,(_,_)=>Task.CompletedTask);var initial=await service.InstallAsync(package,request);
        var before=startup.Value;var settings=await File.ReadAllBytesAsync(Path.Combine(data,"settings.json"));File.Delete(Path.Combine(Path.GetDirectoryName(initial.Executable)!,"coreclr.dll"));
        var failing=new UserInstallation(startup,(_,_)=>Task.CompletedTask,stage=>{if(stage==InstallCommitStage.StartupCommitted)throw new IOException("fixture repair failure");});
        await Assert.ThrowsAsync<IOException>(()=>failing.InstallAsync(package,request));
        Assert.Equal(initial.State,(await UserInstallation.ReadStateAsync(request.InstallDirectory))!);Assert.Equal(before,startup.Value);
        Assert.Equal(settings,await File.ReadAllBytesAsync(Path.Combine(data,"settings.json")));
        var completed=Directory.EnumerateDirectories(Path.Combine(request.InstallDirectory,"versions")).Single(x=>Path.GetFileName(x).Contains("-repair-"));
        var retried=await service.InstallAsync(package,request);Assert.Equal(completed,Path.GetDirectoryName(retried.Executable));
        Assert.Equal(2,Directory.EnumerateDirectories(Path.Combine(request.InstallDirectory,"versions")).Count());
    }
    [Fact]
    public async Task SamePackageCanReinstallAfterUninstallRetainedAnExtraFile()
    {
        var data=await DataAsync("data");var request=new InstallationRequest(Path.Combine(root,"app"),data,UserIntegration:false);
        var package=await PackageAsync();var service=new UserInstallation(new Startup(),(_,_)=>Task.CompletedTask);var initial=await service.InstallAsync(package,request);
        var extra=Path.Combine(Path.GetDirectoryName(initial.Executable)!,"personal.txt");await File.WriteAllTextAsync(extra,"keep");
        using(var result=await CleanupAsync(await UserUninstallation.PrepareAsync(request.InstallDirectory,true)))Assert.True(result.RootElement.GetProperty("passed").GetBoolean());
        var restored=await service.InstallAsync(package,request);Assert.True(restored.Repaired);Assert.True(File.Exists(restored.Executable));Assert.Equal("keep",await File.ReadAllTextAsync(extra));
        var removal=await UserUninstallation.PrepareAsync(request.InstallDirectory,true);Assert.Contains(removal.Warnings,x=>x.Contains("旧版本"));
        using(var result=await CleanupAsync(removal))Assert.True(result.RootElement.GetProperty("passed").GetBoolean());
        Assert.Equal("keep",await File.ReadAllTextAsync(extra));Assert.True(File.Exists(Path.Combine(data,"timetable.db")));
    }
    [Fact]
    public async Task MigrationKeepsOriginalAndDestinationNotesAndLoginScope()
    {
        var source=await DataAsync("source");var target=await DataAsync("target",data:false);
        var key=new PrivateNoteKey(Samples.Dataset.AccountId,Samples.Dataset.Term.Id,Samples.Dataset.Meetings[0].Id);
        using(var notes=new SqliteStore(Path.Combine(source,"timetable.db")))await notes.SaveNoteAsync(new(key,"fictional source note",Samples.Dataset.FetchedAt));
        var targetKey=key with{MeetingId="destination-only"};
        using(var notes=new SqliteStore(Path.Combine(target,"timetable.db")))await notes.SaveNoteAsync(new(targetKey,"destination note",Samples.Dataset.FetchedAt));
        var before=await File.ReadAllBytesAsync(Path.Combine(source,"timetable.db"));
        using(var migration=await UserDataMigration.PrepareAsync(source,target)){Assert.False(migration.Settings.StartupDesired);migration.Commit();}
        Assert.Equal(before,await File.ReadAllBytesAsync(Path.Combine(source,"timetable.db")));
        using(var settings=new SettingsStore(Path.Combine(target,"settings.json")))
            Assert.Equal(AuthProfileLocation.ScopeForDataDirectory(source),(await settings.LoadAsync()).Settings!.AuthenticationScope(target));
        using(var notes=new SqliteStore(Path.Combine(target,"timetable.db")))
        {Assert.Equal("fictional source note",(await notes.ReadNoteAsync(key))!.Text);Assert.Equal("destination note",(await notes.ReadNoteAsync(targetKey))!.Text);}
    }
    [Fact]
    public async Task MigrationFailureAfterDataCommitRestoresTargetAndCanBeRetried()
    {
        var source=await DataAsync("source",startup:true);var target=await DataAsync("target",data:false);
        var settingsBefore=await File.ReadAllBytesAsync(Path.Combine(target,"settings.json"));
        var dbBefore=await File.ReadAllBytesAsync(Path.Combine(target,"timetable.db"));
        var package=await PackageAsync();var request=new InstallationRequest(Path.Combine(root,"app"),target,source,UserIntegration:false);
        var startup=new Startup{Value="leave-me-alone"};
        var failing=new UserInstallation(startup,(_,_)=>Task.CompletedTask,stage=>{if(stage==InstallCommitStage.DataCommitted)throw new IOException("fixture");});
        await Assert.ThrowsAsync<IOException>(()=>failing.InstallAsync(package,request));
        Assert.Null(await UserInstallation.ReadStateAsync(request.InstallDirectory));
        Assert.Equal(settingsBefore,await File.ReadAllBytesAsync(Path.Combine(target,"settings.json")));
        Assert.Equal(dbBefore,await File.ReadAllBytesAsync(Path.Combine(target,"timetable.db")));
        var result=await new UserInstallation(startup,(_,_)=>Task.CompletedTask).InstallAsync(package,request);
        Assert.True(result.DataMigrated);Assert.Equal(0,startup.Writes);Assert.Equal("leave-me-alone",startup.Value);
        using var settings=new SettingsStore(Path.Combine(target,"settings.json"));Assert.False((await settings.LoadAsync()).Settings!.StartupDesired);
    }
    [Fact]
    public async Task ConflictingNotesRefuseMigrationWithoutChangingEitherDatabase()
    {
        var source=await DataAsync("source");var target=await DataAsync("target",data:false);
        var key=new PrivateNoteKey(Samples.Dataset.AccountId,Samples.Dataset.Term.Id,"shared");
        using(var notes=new SqliteStore(Path.Combine(source,"timetable.db")))await notes.SaveNoteAsync(new(key,"source",Samples.Dataset.FetchedAt));
        using(var notes=new SqliteStore(Path.Combine(target,"timetable.db")))await notes.SaveNoteAsync(new(key,"target",Samples.Dataset.FetchedAt));
        var sourceBefore=await File.ReadAllBytesAsync(Path.Combine(source,"timetable.db"));var targetBefore=await File.ReadAllBytesAsync(Path.Combine(target,"timetable.db"));
        await Assert.ThrowsAsync<InvalidDataException>(()=>UserDataMigration.PrepareAsync(source,target));
        Assert.Equal(sourceBefore,await File.ReadAllBytesAsync(Path.Combine(source,"timetable.db")));
        Assert.Equal(targetBefore,await File.ReadAllBytesAsync(Path.Combine(target,"timetable.db")));
        Assert.Empty(Directory.EnumerateDirectories(target,".dlut-migration-*"));
    }
    [Fact]
    public async Task UnknownNonemptyInstallDirectoryIsPreserved()
    {
        var install=Path.Combine(root,"unknown");Directory.CreateDirectory(install);var file=Path.Combine(install,"personal.txt");await File.WriteAllTextAsync(file,"keep");
        var startup=new Startup();var package=await PackageAsync();
        await Assert.ThrowsAsync<InvalidDataException>(()=>new UserInstallation(startup,(_,_)=>throw new Exception("must not stop"))
            .InstallAsync(package,new(install,Path.Combine(root,"data"))));
        Assert.Equal("keep",await File.ReadAllTextAsync(file));Assert.Equal(0,startup.Writes);
    }
    [Fact]
    public async Task ExplicitDefaultLoginProfileSurvivesMigrationToAnotherDirectory()
    {
        var source=await DataAsync("source");var target=Path.Combine(root,"target");
        using(var settings=new SettingsStore(Path.Combine(source,"settings.json")))
            await settings.SaveAsync(new(){UseDefaultAuthProfile=true,StartupDesired=false,Scale=1.35});
        using(var migration=await UserDataMigration.PrepareAsync(source,target))migration.Commit();
        using var reopened=new SettingsStore(Path.Combine(target,"settings.json"));var result=(await reopened.LoadAsync()).Settings!;
        Assert.True(result.UseDefaultAuthProfile);Assert.Null(result.AuthenticationScope(target));Assert.Equal(1.35,result.Scale);
        Assert.False(Directory.Exists(Path.Combine(target,"auth-profile")));
    }
    [Fact]
    public async Task InvalidOrAmbiguousLoginProfileSettingsAreRejectedBeforeWriting()
    {
        var directory=await DataAsync("data");var path=Path.Combine(directory,"settings.json");var before=await File.ReadAllBytesAsync(path);
        using var settings=new SettingsStore(path);
        await Assert.ThrowsAsync<ArgumentException>(()=>settings.SaveAsync(new(){AuthProfileScopeId="../unsafe"}));
        await Assert.ThrowsAsync<ArgumentException>(()=>settings.SaveAsync(new(){UseDefaultAuthProfile=true,AuthProfileScopeId=new('A',64)}));
        Assert.Equal(before,await File.ReadAllBytesAsync(path));
    }
    [Fact]
    public async Task MigrationRefusesAnExistingTargetDataset()
    {
        var source=await DataAsync("source");var target=await DataAsync("target");var before=await File.ReadAllBytesAsync(Path.Combine(target,"timetable.db"));
        await Assert.ThrowsAsync<InvalidDataException>(()=>UserDataMigration.PrepareAsync(source,target));
        Assert.Equal(before,await File.ReadAllBytesAsync(Path.Combine(target,"timetable.db")));
    }
    private async Task<UninstallationPlan> InstalledRemovalAsync(bool keep)
    {
        var data=await DataAsync("data");
        await new UserInstallation(new Startup(),(_,_)=>Task.CompletedTask).InstallAsync(await PackageAsync(),new(Path.Combine(root,"app"),data,UserIntegration:false));
        return await UserUninstallation.PrepareAsync(Path.Combine(root,"app"),keep);
    }
    private async Task<JsonDocument> CleanupAsync(UninstallationPlan plan)
    {
        var work=Path.Combine(root,"cleanup-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(work);
        var script=Path.Combine(work,"cleanup.ps1");await File.WriteAllTextAsync(script,await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,"uninstall-cleanup.ps1")),new UTF8Encoding(true));
        var path=Path.Combine(work,"plan.json");await File.WriteAllBytesAsync(path,JsonSerializer.SerializeToUtf8Bytes(plan,DatasetJson.Options));
        var ps=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),@"System32\WindowsPowerShell\v1.0\powershell.exe");
        // A real, already-finished parent handle. Never wait on/terminate the test host.
        using var parent=Process.Start(new ProcessStartInfo(ps){UseShellExecute=false,CreateNoWindow=true,ArgumentList={"-NoProfile","-NonInteractive","-Command","exit 0"}})!;
        var parentId=parent.Id;var ticks=parent.StartTime.ToUniversalTime().Ticks;await parent.WaitForExitAsync();
        var start=new ProcessStartInfo(ps){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
        start.Environment["PSModulePath"]=Path.Combine(Path.GetDirectoryName(ps)!,"Modules");
        foreach(var arg in new[]{"-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",script,"-PlanPath",path,"-ParentProcessId",parentId.ToString(),"-ParentStartTicks",ticks.ToString(),"-Quiet"})start.ArgumentList.Add(arg);
        using var child=Process.Start(start)!;var error=child.StandardError.ReadToEndAsync();var output=child.StandardOutput.ReadToEndAsync();
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
        var result=Path.Combine(work,"result.json");Assert.True(File.Exists(result),await error+await output);
        return JsonDocument.Parse(await File.ReadAllTextAsync(result));
    }
    [Fact]
    public async Task UninstallKeepsDatasetSettingsAndUnlistedProgramFiles()
    {
        var plan=await InstalledRemovalAsync(true);var database=Path.Combine(plan.DataDirectory,"timetable.db");var before=await File.ReadAllBytesAsync(database);
        var settings=await File.ReadAllBytesAsync(Path.Combine(plan.DataDirectory,"settings.json"));
        var unknown=Path.Combine(plan.InstallDirectory,plan.Files[0].Path.Replace('/',Path.DirectorySeparatorChar)+".personal");await File.WriteAllTextAsync(unknown,"keep");
        using var result=await CleanupAsync(plan);Assert.True(result.RootElement.GetProperty("passed").GetBoolean(),result.RootElement.GetProperty("message").GetString());
        Assert.Equal(before,await File.ReadAllBytesAsync(database));Assert.Equal(settings,await File.ReadAllBytesAsync(Path.Combine(plan.DataDirectory,"settings.json")));
        Assert.Equal("keep",await File.ReadAllTextAsync(unknown));Assert.False(File.Exists(Path.Combine(plan.InstallDirectory,"install-state.json")));
        Assert.False(File.Exists(Path.Combine(plan.InstallDirectory,plan.Executables[0])));
    }
    [Fact]
    public async Task UninstallDeletesOnlySelectedLocalDataAndDedicatedProfile()
    {
        var plan=await InstalledRemovalAsync(false);Assert.NotNull(plan.DedicatedProfile);Assert.False(Directory.Exists(plan.DedicatedProfile));
        Directory.CreateDirectory(plan.DedicatedProfile);var marker=Path.Combine(plan.DedicatedProfile,"fictional-only.txt");await File.WriteAllTextAsync(marker,"fixture, no cookies");
        var export=Path.Combine(plan.DataDirectory,"personal-export.json");await File.WriteAllTextAsync(export,"keep export");
        try
        {
            using var result=await CleanupAsync(plan);Assert.True(result.RootElement.GetProperty("passed").GetBoolean(),result.RootElement.GetProperty("message").GetString());
            Assert.False(File.Exists(Path.Combine(plan.DataDirectory,"timetable.db")));Assert.False(File.Exists(Path.Combine(plan.DataDirectory,"settings.json")));
            Assert.False(Directory.Exists(plan.DedicatedProfile));Assert.Equal("keep export",await File.ReadAllTextAsync(export));Assert.False(Directory.Exists(plan.InstallDirectory));
        }
        finally {if(File.Exists(marker))File.Delete(marker);if(Directory.Exists(plan.DedicatedProfile)&&!Directory.EnumerateFileSystemEntries(plan.DedicatedProfile).Any())Directory.Delete(plan.DedicatedProfile);}
    }
    [Fact]
    public async Task UninstallPreservesSharedLoginProfileWithoutRequestingItsDeletion()
    {
        var plan=await InstalledRemovalAsync(false);
        using(var settings=new SettingsStore(Path.Combine(plan.DataDirectory,"settings.json")))await settings.SaveAsync(new(){UseDefaultAuthProfile=true});
        var shared=await UserUninstallation.PrepareAsync(plan.InstallDirectory,false);
        Assert.Null(shared.DedicatedProfile);Assert.Contains(shared.Warnings,x=>x.Contains("共用"));
    }
    [Fact]
    public async Task UninstallOriginalCopyKeepsProfileAfterAnotherCopyMigratesFromIt()
    {
        var original=await InstalledRemovalAsync(false);var target=Path.Combine(root,"migrated-copy");
        using(var migration=await UserDataMigration.PrepareAsync(original.DataDirectory,target))migration.Commit();
        var plan=await UserUninstallation.PrepareAsync(original.InstallDirectory,false);
        Assert.Null(plan.DedicatedProfile);Assert.Contains(plan.Warnings,x=>x.Contains("共用"));
        Assert.True(File.Exists(Path.Combine(original.DataDirectory,UserDataMigration.SharedProfileMarker)));
    }
    [Theory]
    [InlineData("traversal")]
    [InlineData("changed-state")]
    [InlineData("wrong-profile")]
    public async Task UninstallRejectsChangedOrUnsafePlanBeforeDeletingAnything(string scenario)
    {
        var plan=await InstalledRemovalAsync(false);var outside=Path.Combine(root,"keep.txt");await File.WriteAllTextAsync(outside,"outside");
        if(scenario=="traversal")plan=plan with{Files=plan.Files.SetItem(0,plan.Files[0] with{Path="../keep.txt"})};
        if(scenario=="changed-state")await File.AppendAllTextAsync(Path.Combine(plan.InstallDirectory,"install-state.json")," ");
        if(scenario=="wrong-profile")plan=plan with{DedicatedProfile=root};
        using var result=await CleanupAsync(plan);Assert.False(result.RootElement.GetProperty("passed").GetBoolean());
        Assert.Equal(scenario switch{"traversal"=>"CleanupUnsafePath","changed-state"=>"CleanupStateChanged",_=>"CleanupProfileMismatch"},result.RootElement.GetProperty("errorCode").GetString());
        Assert.True(Guid.TryParseExact(result.RootElement.GetProperty("correlationId").GetString(),"N",out _));
        Assert.Contains(scenario switch{"traversal"=>"路径","changed-state"=>"安装记录已改变",_=>"专用登录资料目录无效"},result.RootElement.GetProperty("message").GetString());
        Assert.Equal("outside",await File.ReadAllTextAsync(outside));Assert.True(File.Exists(Path.Combine(plan.InstallDirectory,plan.Executables[0])));
        Assert.True(File.Exists(Path.Combine(plan.DataDirectory,"timetable.db")));
    }
    [Fact]
    public async Task StableUnsignedAndTamperedPackagesAreRejected()
    {
        await Assert.ThrowsAsync<InvalidDataException>(()=>PackageAsync("1.0.0"));
        var package=await PackageAsync();var exe=Path.Combine(package.Directory,"DlutTimetable.Desktop.exe");var bytes=await File.ReadAllBytesAsync(exe);bytes[0]^=1;await File.WriteAllBytesAsync(exe,bytes);
        await Assert.ThrowsAsync<InvalidDataException>(()=>ReleasePackage.VerifyAsync(package.Directory));
    }
    [Theory]
    [InlineData("../escape")]
    [InlineData("folder/../../escape")]
    [InlineData("C:/outside")]
    [InlineData("folder/alias. ")]
    public void UnsafeRelativePathsNeverLeaveTheIntendedRoot(string relative)=>Assert.Throws<InvalidDataException>(()=>InstallationPaths.Child(root,relative));
}
