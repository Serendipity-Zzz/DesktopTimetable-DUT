using DlutTimetable.Application;

namespace DlutTimetable.Tests;

public sealed class StartupTests
{
    private const string Command="\"C:\\App With Spaces\\DlutTimetable.Desktop.exe\" --background";
    private const string PrivateCanary="PrivateStartupCanary_DoNotExport";
    private sealed class UnreadableRegistration:IStartupRegistration
    {
        public string? Read()=>throw new IOException(PrivateCanary);
        public void Write(string? command)=>throw new InvalidOperationException("Unexpected write");
    }
    [Fact]
    public async Task FailureFeedbackPreservesStateWithoutLeakingExceptionContent()
    {
        var unreadable=new StartupManager(new UnreadableRegistration(),Command,true,false,_=>Task.CompletedTask);
        await unreadable.InitializeAsync(false);Assert.True(unreadable.State.OutOfSync);
        Assert.DoesNotContain(PrivateCanary,unreadable.State.Message);
        await unreadable.ApplyAsync(true);Assert.False(unreadable.State.Desired);
        Assert.DoesNotContain(PrivateCanary,unreadable.State.Message);
        var registration=new Registration();
        var unsaved=new StartupManager(registration,Command,true,false,_=>throw new IOException(PrivateCanary));
        await unsaved.ApplyAsync(true);
        Assert.Null(registration.Value);Assert.False(unsaved.State.Desired);
        Assert.Contains("未保存",unsaved.State.Message);Assert.DoesNotContain(PrivateCanary,unsaved.State.Message);
    }
    private sealed class Registration:IStartupRegistration
    {
        public string? Value;
        public int Writes;
        public bool DenyRead,DenyWrite,IgnoreWrite,FailRollback;
        public string? Read()=>DenyRead?throw new UnauthorizedAccessException("Denied"):Value;
        public void Write(string? command)
        {
            Writes++;
            if(DenyWrite || (FailRollback && Writes>1))throw new UnauthorizedAccessException("Denied");
            if(!IgnoreWrite)Value=command;
        }
    }
    [Fact]
    public async Task FirstManualInitializationDefaultsOnAndConfirmsOnlyAfterReadback()
    {
        var registration=new Registration();var saved=false;
        var manager=new StartupManager(registration,Command,false,true,value=>{Assert.Equal(Command,registration.Value);saved=value;return Task.CompletedTask;});
        await manager.InitializeAsync(true);
        Assert.True(saved);Assert.Equal(1,registration.Writes);Assert.True(manager.State.Desired);
        Assert.True(manager.State.RegistrationObserved);Assert.False(manager.State.OutOfSync);Assert.False(manager.State.Busy);
        await manager.InitializeAsync(true);Assert.Equal(1,registration.Writes);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackgroundInitializationNeverWritesOrCreatesRegistration(bool entryExists)
    {
        var registration=new Registration{Value=entryExists?Command:null};var saves=0;
        var manager=new StartupManager(registration,Command,false,true,_=>{saves++;return Task.CompletedTask;});
        await manager.InitializeAsync(false);
        Assert.Equal(0,registration.Writes);Assert.Equal(0,saves);
        Assert.Equal(!entryExists,manager.State.OutOfSync);
    }
    [Fact]
    public async Task DisabledPreferenceSurvivesManualRestartAndExecutableMigration()
    {
        var registration=new Registration();var manager=new StartupManager(registration,Command,true,false,_=>Task.CompletedTask);
        await manager.InitializeAsync(true);Assert.False(manager.State.Desired);Assert.Equal(0,registration.Writes);
        var migrated=new StartupManager(registration,Command.Replace("App With Spaces","New Directory"),true,false,_=>Task.CompletedTask);
        await migrated.InitializeAsync(true);Assert.False(migrated.State.Desired);Assert.Equal(0,registration.Writes);
    }
    [Fact]
    public async Task ExistingEntryAfterInterruptedInitializationIsConfirmedWithoutRewriting()
    {
        var registration=new Registration{Value=Command};var saves=0;
        var manager=new StartupManager(registration,Command,false,true,_=>{saves++;return Task.CompletedTask;});
        await manager.InitializeAsync(true);Assert.Equal(1,saves);Assert.Equal(0,registration.Writes);Assert.False(manager.State.OutOfSync);
    }
    [Fact]
    public async Task ExternalRemovalOrOldInstallPathRequiresExplicitRepair()
    {
        var registration=new Registration();var saves=0;
        var manager=new StartupManager(registration,Command,true,true,_=>{saves++;return Task.CompletedTask;});
        await manager.InitializeAsync(true);Assert.True(manager.State.OutOfSync);Assert.Equal(0,registration.Writes);
        registration.Value="\"C:\\Old\\DlutTimetable.Desktop.exe\" --background";
        await manager.InitializeAsync(true);Assert.True(manager.State.OutOfSync);Assert.Equal(0,registration.Writes);
        await manager.ApplyAsync(true);Assert.Equal(Command,registration.Value);Assert.False(manager.State.OutOfSync);Assert.Equal(1,saves);
    }
    [Fact]
    public async Task TurningOffDeletesOnlyRegistrationAndCommitsDisabledPreference()
    {
        var registration=new Registration{Value=Command};var confirmed=true;
        var manager=new StartupManager(registration,Command,true,true,value=>{confirmed=value;return Task.CompletedTask;});
        await manager.ApplyAsync(false);
        Assert.Null(registration.Value);Assert.False(confirmed);Assert.False(manager.State.Desired);Assert.False(manager.State.OutOfSync);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedPreferenceSaveRollsRegistrationBackAndKeepsConfirmedPreference(bool initiallyOn)
    {
        var registration=new Registration{Value=initiallyOn?Command:null};
        var manager=new StartupManager(registration,Command,true,initiallyOn,_=>throw new IOException("Save failed"));
        await manager.ApplyAsync(!initiallyOn);
        Assert.Equal(initiallyOn?Command:null,registration.Value);Assert.Equal(initiallyOn,manager.State.Desired);
        Assert.False(manager.State.OutOfSync);Assert.Contains("未保存",manager.State.Message);Assert.Equal(2,registration.Writes);
    }
    [Fact]
    public async Task RollbackFailureReportsOutOfSyncAndNextStartupDoesNotBlindlyReenable()
    {
        var registration=new Registration{FailRollback=true};
        var manager=new StartupManager(registration,Command,true,false,_=>throw new IOException("Save failed"));
        await manager.ApplyAsync(true);Assert.True(manager.State.OutOfSync);Assert.False(manager.State.Desired);
        Assert.Equal(Command,registration.Value);var writes=registration.Writes;
        var restarted=new StartupManager(registration,Command,true,false,_=>Task.CompletedTask);
        await restarted.InitializeAsync(true);Assert.Equal(writes,registration.Writes);Assert.True(restarted.State.OutOfSync);
    }
    [Fact]
    public async Task ConcurrentExternalChangeDuringFailedCommitIsNotOverwritten()
    {
        var registration=new Registration();
        var manager=new StartupManager(registration,Command,true,false,_=>{registration.Value="externally replaced";throw new IOException("Save failed");});
        await manager.ApplyAsync(true);Assert.Equal("externally replaced",registration.Value);Assert.True(manager.State.OutOfSync);Assert.Equal(1,registration.Writes);
    }
    [Theory]
    [InlineData(true,false,false)]
    [InlineData(false,true,false)]
    [InlineData(false,false,true)]
    public async Task ReadWriteOrReadbackFailureNeverReportsSuccess(bool denyRead,bool denyWrite,bool ignoreWrite)
    {
        var registration=new Registration{DenyRead=denyRead,DenyWrite=denyWrite,IgnoreWrite=ignoreWrite};var saves=0;
        var manager=new StartupManager(registration,Command,true,false,_=>{saves++;return Task.CompletedTask;});
        await manager.ApplyAsync(true);Assert.False(manager.State.Desired);Assert.Equal(0,saves);Assert.Contains("未保存",manager.State.Message);
    }
    [Fact]
    public async Task CommandsSerializeAcrossSlowPersistence()
    {
        var registration=new Registration();var hold=new TaskCompletionSource();var saves=new List<bool>();
        var manager=new StartupManager(registration,Command,true,false,async value=>{saves.Add(value);if(value)await hold.Task;});
        var enable=manager.ApplyAsync(true);Assert.True(manager.State.Busy);
        var disable=manager.ApplyAsync(false);Assert.Equal(1,registration.Writes);
        hold.SetResult();await Task.WhenAll(enable,disable);
        Assert.Equal(new[]{true,false},saves);Assert.Null(registration.Value);Assert.False(manager.State.Desired);
    }
}
