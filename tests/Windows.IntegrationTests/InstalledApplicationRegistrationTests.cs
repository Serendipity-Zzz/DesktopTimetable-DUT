using System.IO;
using DlutTimetable.Windows;
using Microsoft.Win32;

namespace DlutTimetable.Windows.IntegrationTests;

public sealed class InstalledApplicationRegistrationTests
{
    [Fact]
    public void RealIsolatedRegistryEntryQuotesPathsAndRemovesOnlyOwnedRecord()
    {
        var id=Guid.NewGuid().ToString("N");var keyPath=@"Software\DLUTDesktopTimetable\Validation\"+id;
        var directory=Path.Combine(Path.GetTempPath(),"Dlut-uninstall-registry-"+id);var install=Path.Combine(directory,"安装 目录");
        var exe=Path.Combine(install,"versions","0.3.0-dev-test","DlutTimetable.Desktop.exe");Directory.CreateDirectory(Path.GetDirectoryName(exe)!);File.WriteAllText(exe,"fixture, never executed");
        try
        {
            var data=Path.Combine(directory,"个人 数据");var registration=new InstalledApplicationRegistration(keyPath);
            registration.Register(exe,install,data,"0.3.0-dev");
            using(var key=Registry.CurrentUser.OpenSubKey(keyPath))
            {
                Assert.Equal("\""+exe+"\" --uninstall --install-directory \""+install+"\" --data-directory \""+data+"\"",key!.GetValue("UninstallString"));
                Assert.Equal(1,key.GetValue("NoModify"));Assert.Equal(1,key.GetValue("NoRepair"));
            }
            Assert.False(registration.RemoveIfOwned(Path.Combine(directory,"other")));
            var otherExe=Path.Combine(directory,"other","versions","x","DlutTimetable.Desktop.exe");Directory.CreateDirectory(Path.GetDirectoryName(otherExe)!);File.WriteAllText(otherExe,"fixture");
            Assert.Throws<InvalidOperationException>(()=>registration.Register(otherExe,Path.Combine(directory,"other"),data,"0.3.0-dev"));
            Assert.True(registration.RemoveIfOwned(install));Assert.Null(Registry.CurrentUser.OpenSubKey(keyPath));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath,false);
            var checkedRoot=Path.GetFullPath(directory);
            if(Path.GetDirectoryName(checkedRoot)!=Path.TrimEndingDirectorySeparator(Path.GetTempPath())||!Path.GetFileName(checkedRoot).StartsWith("Dlut-uninstall-registry-",StringComparison.Ordinal))throw new InvalidOperationException("Unexpected cleanup target.");
            Directory.Delete(checkedRoot,true);
        }
    }
}
