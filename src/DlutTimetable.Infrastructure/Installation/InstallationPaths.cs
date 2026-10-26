namespace DlutTimetable.Infrastructure.Installation;

public static class InstallationPaths
{
    public static string Full(string path)=>Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    public static bool Same(string a,string b)=>string.Equals(Full(a),Full(b),StringComparison.OrdinalIgnoreCase);
    public static string Child(string root,string relative)
    {
        var parts=relative.Replace('\\','/').Split('/');
        if(string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || parts.Any(p=>p is "" or "." or ".."
            || p.EndsWith(' ') || p.EndsWith('.') || p.IndexOfAny(Path.GetInvalidFileNameChars())>=0))throw new InvalidDataException("安装路径无效。");
        var value=Full(Path.Combine(root,Path.Combine(parts)));
        if(!value.StartsWith(Full(root)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("安装路径超出目标目录。");
        NoLinks(value);return value;
    }
    public static void NoLinks(string path)
    {
        var value=Full(path);
        while(!string.IsNullOrEmpty(value))
        {
            if((File.Exists(value)||Directory.Exists(value)) && (File.GetAttributes(value)&FileAttributes.ReparsePoint)!=0)
                throw new InvalidDataException("安装或迁移目录包含链接，未执行操作。");
            value=Path.GetDirectoryName(value)!;
        }
    }
    internal static void DeleteOwnedStage(string root,string stage)
    {
        var relative=Path.GetRelativePath(Full(root),Full(stage));
        var checkedPath=Child(root,relative);
        if(!Path.GetFileName(checkedPath).StartsWith(".dlut-",StringComparison.Ordinal))throw new InvalidDataException("不是本次临时目录。");
        if(!Directory.Exists(checkedPath))return;
        foreach(var item in new DirectoryInfo(checkedPath).EnumerateFileSystemInfos("*",SearchOption.AllDirectories))NoLinks(item.FullName);
        Directory.Delete(checkedPath,true);
    }
}
