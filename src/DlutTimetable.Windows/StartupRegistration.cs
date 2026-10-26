using System.IO;
using DlutTimetable.Application;
using Microsoft.Win32;

namespace DlutTimetable.Windows;

public sealed class StartupRegistration : IStartupRegistration
{
    public const string ValueName="DlutDesktopTimetable";
    private const string RunKey=@"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string keyPath;
    public StartupRegistration():this(RunKey){}
    internal StartupRegistration(string keyPath)=>this.keyPath=keyPath;
    public string? Read()
    {
        using var key=Registry.CurrentUser.OpenSubKey(keyPath);
        if(key?.GetValue(ValueName,null,RegistryValueOptions.DoNotExpandEnvironmentNames) is not { } value)return null;
        if(key.GetValueKind(ValueName)!=RegistryValueKind.String || value is not string text)
            throw new InvalidDataException("应用启动项不是预期的字符串，未覆盖原值。");
        return text;
    }
    public void Write(string? command)
    {
        if(command is null)
        { using var key=Registry.CurrentUser.OpenSubKey(keyPath,writable:true);key?.DeleteValue(ValueName,throwOnMissingValue:false); }
        else
        { using var key=Registry.CurrentUser.CreateSubKey(keyPath,writable:true);key.SetValue(ValueName,command,RegistryValueKind.String); }
    }
    public static string CommandFor(string executable)
    {
        if(!Path.IsPathFullyQualified(executable) || executable.IndexOfAny(['"','\r','\n','\0'])>=0
            || !string.Equals(Path.GetFileName(executable),"DlutTimetable.Desktop.exe",StringComparison.OrdinalIgnoreCase)
            || !File.Exists(executable))throw new ArgumentException("找不到有效的课表程序 EXE。");
        var command="\""+Path.GetFullPath(executable)+"\" --background";
        if(command.Length>260)throw new ArgumentException("程序路径超过 Windows 登录启动项长度限制。");
        return command;
    }
}
