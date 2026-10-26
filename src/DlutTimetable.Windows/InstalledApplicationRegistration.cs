using System.IO;
using Microsoft.Win32;

namespace DlutTimetable.Windows;

/// <summary>HKCU installed-app entry only; no service, scheduled task or second startup mechanism.</summary>
public sealed class InstalledApplicationRegistration
{
    private readonly string keyPath;
    public InstalledApplicationRegistration():this(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\DLUTDesktopTimetable"){}
    internal InstalledApplicationRegistration(string keyPath)=>this.keyPath=keyPath;
    public void Register(string executable,string installDirectory,string dataDirectory,string version)
    {
        var root=CheckedPath(installDirectory);var exe=CheckedPath(executable);var data=CheckedPath(dataDirectory);
        if(!exe.StartsWith(root+@"\versions\",StringComparison.OrdinalIgnoreCase)||Path.GetFileName(exe)!="DlutTimetable.Desktop.exe"||!File.Exists(exe))
            throw new ArgumentException("卸载入口不是已安装的主程序。");
        if(string.IsNullOrWhiteSpace(version)||version.Length>64||version.Any(char.IsControl))throw new ArgumentException("版本无效。");
        using var before=Registry.CurrentUser.OpenSubKey(keyPath);
        if(before is not null && (before.GetValue("InstallLocation") is not string old || !Same(old,root)))
            throw new InvalidOperationException("已存在其它安装的卸载记录，未覆盖。");
        using var key=Registry.CurrentUser.CreateSubKey(keyPath,true);
        key.SetValue("DisplayName","大工课表（开发预览）",RegistryValueKind.String);
        key.SetValue("DisplayVersion",version,RegistryValueKind.String);
        key.SetValue("InstallLocation",root,RegistryValueKind.String);
        key.SetValue("DisplayIcon",exe,RegistryValueKind.String);
        key.SetValue("UninstallString",Quote(exe)+" --uninstall --install-directory "+Quote(root)+" --data-directory "+Quote(data),RegistryValueKind.String);
        key.SetValue("NoModify",1,RegistryValueKind.DWord);key.SetValue("NoRepair",1,RegistryValueKind.DWord);
    }
    public bool RemoveIfOwned(string installDirectory)
    {
        var root=CheckedPath(installDirectory);bool owned;
        using(var key=Registry.CurrentUser.OpenSubKey(keyPath))owned=key?.GetValue("InstallLocation") is string actual && Same(actual,root);
        if(owned)Registry.CurrentUser.DeleteSubKeyTree(keyPath,false);return owned;
    }
    private static string CheckedPath(string path)
    {
        if(!Path.IsPathFullyQualified(path)||path.IndexOfAny(['"','\r','\n','\0'])>=0)throw new ArgumentException("安装入口路径无效。");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
    private static string Quote(string value)=>"\""+value+"\"";
    private static bool Same(string a,string b)=>string.Equals(CheckedPath(a),CheckedPath(b),StringComparison.OrdinalIgnoreCase);
}
