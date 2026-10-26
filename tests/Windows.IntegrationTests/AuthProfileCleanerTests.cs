using System.Diagnostics;
using System.IO;
using System.Text;
using DlutTimetable.Contracts;
using DlutTimetable.Infrastructure;
using DlutTimetable.Windows;

namespace DlutTimetable.Tests;

public sealed class AuthProfileCleanerTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"DlutProfileTests-"+Guid.NewGuid().ToString("N"));
    private string Profile=>Path.Combine(root,"auth-profile");
    public AuthProfileCleanerTests()=>Directory.CreateDirectory(Profile);
    public void Dispose()
    {
        var target=new DirectoryInfo(root);
        if(target.Parent?.FullName!=Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) || !target.Name.StartsWith("DlutProfileTests-",StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected test cleanup path.");
        var link=Path.Combine(Profile,"linked");if(Directory.Exists(link) && (File.GetAttributes(link)&FileAttributes.ReparsePoint)!=0)Directory.Delete(link,false);
        var alias=Path.Combine(root,"export-alias");if(Directory.Exists(alias) && (File.GetAttributes(alias)&FileAttributes.ReparsePoint)!=0)Directory.Delete(alias,false);
        target.Delete(true);
    }
    [Fact]
    public async Task ClearsOnlyOwnedProfileAndLeavesDataSettingsAndNotesUntouched()
    {
        foreach(var file in new[]{"current.db","settings.json","private-notes-sentinel"})await File.WriteAllTextAsync(Path.Combine(root,file),"unchanged");
        Directory.CreateDirectory(Path.Combine(Profile,"Default"));await File.WriteAllTextAsync(Path.Combine(Profile,"Default","FakeCookies"),"fake-session");
        var cleaner=new AuthProfileCleaner(root);Assert.Equal(ProfileClearOutcome.Cleared,await cleaner.ClearAsync());Assert.False(Directory.Exists(Profile));
        foreach(var file in new[]{"current.db","settings.json","private-notes-sentinel"})Assert.Equal("unchanged",await File.ReadAllTextAsync(Path.Combine(root,file)));
        Assert.Equal(ProfileClearOutcome.AlreadyEmpty,await cleaner.ClearAsync());
    }
    [Fact]
    public async Task ActiveProfileLeasePreventsDeletionWithoutKillingItsOwner()
    {
        using var ready=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        var thread=new Thread(()=>
        {
            using var mutex=new Mutex(false,AuthProfileLocation.MutexName(Profile));mutex.WaitOne();
            try {ready.Set();release.Wait();}finally {mutex.ReleaseMutex();}
        });thread.Start();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(5)));
        try {Assert.Equal(ProfileClearOutcome.Busy,await new AuthProfileCleaner(root).ClearAsync());Assert.True(Directory.Exists(Profile));Assert.True(thread.IsAlive);}
        finally {release.Set();Assert.True(thread.Join(TimeSpan.FromSeconds(5)));}
    }
    [Fact]
    public async Task CancellationAndLockedFilesDoNotReportSuccessfulRemoval()
    {
        var file=Path.Combine(Profile,"locked");await File.WriteAllTextAsync(file,"fake-session");
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new AuthProfileCleaner(root).ClearAsync(cancelled.Token));Assert.True(File.Exists(file));
        using var locked=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.None);
        Assert.Equal(ProfileClearOutcome.Failed,await new AuthProfileCleaner(root).ClearAsync());
    }
    [Fact]
    public async Task JunctionCannotEscapeProfileOrBypassProtectedExportDestination()
    {
        var outside=Path.Combine(root,"outside");Directory.CreateDirectory(outside);
        var sentinel=Path.Combine(outside,"keep");await File.WriteAllTextAsync(sentinel,"unchanged");
        await JunctionAsync(Path.Combine(Profile,"linked"),outside);
        Assert.Equal(ProfileClearOutcome.UnsafeDirectory,await new AuthProfileCleaner(root).ClearAsync());
        Assert.Equal("unchanged",await File.ReadAllTextAsync(sentinel));
        var alias=Path.Combine(root,"export-alias");await JunctionAsync(alias,outside);
        await Assert.ThrowsAsync<InvalidDataException>(()=>AtomicExport.WriteAsync(Path.Combine(alias,"keep"),"bad-overwrite"u8.ToArray(),[outside]));
        Assert.Equal("unchanged",await File.ReadAllTextAsync(sentinel));
    }
    private static async Task JunctionAsync(string link,string destination)
    {
        var shell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","WindowsPowerShell","v1.0","powershell.exe");
        var start=new ProcessStartInfo(shell){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true};
        foreach(var arg in new[]{"-NoProfile","-NonInteractive","-EncodedCommand",Convert.ToBase64String(Encoding.Unicode.GetBytes(
            "$ErrorActionPreference='Stop';New-Item -ItemType Junction -Path $env:DLUT_TEST_LINK -Target $env:DLUT_TEST_TARGET | Out-Null"))})start.ArgumentList.Add(arg);
        start.Environment["DLUT_TEST_LINK"]=link;start.Environment["DLUT_TEST_TARGET"]=destination;
        using var process=Process.Start(start)??throw new IOException("Junction fixture did not start.");
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(process.ExitCode==0,await process.StandardError.ReadToEndAsync());
        }
        finally {if(!process.HasExited){process.Kill(false);await process.WaitForExitAsync();}}
    }
}
