using System.IO;
using DlutTimetable.Windows;
using DlutTimetable.Application;
using DlutTimetable.Infrastructure;
using Microsoft.Win32;

namespace DlutTimetable.Windows.IntegrationTests;

public sealed class StartupRegistrationTests
{
    [Fact]
    public async Task RealRegistryAndAtomicSettingsConfirmDisableAndRollbackOnCorruptFile()
    {
        var id=Guid.NewGuid().ToString("N");
        var keyPath=@"Software\DLUTDesktopTimetable\Validation\"+id;
        var directory=Path.Combine(Path.GetTempPath(),"Dlut-startup-validation-"+id);Directory.CreateDirectory(directory);
        var settingsPath=Path.Combine(directory,"settings.json");
        try
        {
            var registration=new StartupRegistration(keyPath);
            using var store=new SettingsStore(settingsPath);
            var preferences=(await store.LoadAsync()).Settings!;
            const string command="\"C:\\Validated App\\DlutTimetable.Desktop.exe\" --background";
            var manager=new StartupManager(registration,command,preferences.StartupInitialized,preferences.StartupDesired,async desired=>
            {
                var next=preferences with {StartupInitialized=true,StartupDesired=desired};
                await store.SaveAsync(next);preferences=next;
            });
            await manager.InitializeAsync(true);
            Assert.Equal(command,registration.Read());Assert.True((await store.LoadAsync()).Settings!.StartupInitialized);
            await manager.ApplyAsync(false);
            Assert.Null(registration.Read());Assert.False((await store.LoadAsync()).Settings!.StartupDesired);
            await File.WriteAllTextAsync(settingsPath,"broken");
            await manager.ApplyAsync(true);
            Assert.Null(registration.Read());Assert.False(manager.State.Desired);Assert.Contains("未保存",manager.State.Message);
            Assert.Equal("broken",await File.ReadAllTextAsync(settingsPath));
            Assert.Empty(Directory.GetFiles(directory,"*.tmp"));
        }
        finally {Registry.CurrentUser.DeleteSubKeyTree(keyPath,throwOnMissingSubKey:false);Directory.Delete(directory,recursive:true);}
    }
    [Fact]
    public void IsolatedCurrentUserRegistryWritesReadsAndDeletesOnlyItsOwnValue()
    {
        var path=@"Software\DLUTDesktopTimetable\Validation\"+Guid.NewGuid().ToString("N");
        try
        {
            using(var key=Registry.CurrentUser.CreateSubKey(path))key.SetValue("Unrelated","keep");
            var registration=new StartupRegistration(path);
            Assert.Null(registration.Read());registration.Write("\"C:\\Space Dir\\DlutTimetable.Desktop.exe\" --background");
            Assert.Equal("\"C:\\Space Dir\\DlutTimetable.Desktop.exe\" --background",registration.Read());
            registration.Write(null);Assert.Null(registration.Read());
            using var observed=Registry.CurrentUser.OpenSubKey(path);Assert.Equal("keep",observed!.GetValue("Unrelated"));
        }
        finally {Registry.CurrentUser.DeleteSubKeyTree(path,throwOnMissingSubKey:false);}
    }
    [Fact]
    public void UnexpectedRegistryTypeIsPreservedAndRejected()
    {
        var path=@"Software\DLUTDesktopTimetable\Validation\"+Guid.NewGuid().ToString("N");
        try
        {
            using(var key=Registry.CurrentUser.CreateSubKey(path))key.SetValue(StartupRegistration.ValueName,123,RegistryValueKind.DWord);
            Assert.Throws<InvalidDataException>(()=>new StartupRegistration(path).Read());
            using var observed=Registry.CurrentUser.OpenSubKey(path);Assert.Equal(123,observed!.GetValue(StartupRegistration.ValueName));
        }
        finally {Registry.CurrentUser.DeleteSubKeyTree(path,throwOnMissingSubKey:false);}
    }
    [Fact]
    public void StartupCommandQuotesVerifiedExecutableAndRejectsInjectionAndExcessLength()
    {
        var folder=Path.Combine(Path.GetTempPath(),"Dlut startup "+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        try
        {
            var executable=Path.Combine(folder,"DlutTimetable.Desktop.exe");File.WriteAllBytes(executable,[]);
            Assert.Equal("\""+executable+"\" --background",StartupRegistration.CommandFor(executable));
            Assert.Throws<ArgumentException>(()=>StartupRegistration.CommandFor("relative.exe"));
            Assert.Throws<ArgumentException>(()=>StartupRegistration.CommandFor(executable+"\" --other"));
            var longDirectory=Path.Combine(folder,new string('x',180));Directory.CreateDirectory(longDirectory);
            var longExe=Path.Combine(longDirectory,"DlutTimetable.Desktop.exe");File.WriteAllBytes(longExe,[]);
            Assert.Throws<ArgumentException>(()=>StartupRegistration.CommandFor(longExe));
        }
        finally {Directory.Delete(folder,recursive:true);}
    }
}
