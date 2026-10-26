namespace DlutTimetable.Infrastructure;

public static class AtomicExport
{
    public static async Task WriteAsync(string destination,ReadOnlyMemory<byte> content,IReadOnlyCollection<string> protectedDirectories,CancellationToken ct=default)
    {
        var target=ResolveLinks(destination);
        foreach(var directory in protectedDirectories)
        {
            var owned=ResolveLinks(directory).TrimEnd(Path.DirectorySeparatorChar);
            if(string.Equals(target,owned,StringComparison.OrdinalIgnoreCase) || target.StartsWith(owned+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("请选择程序数据目录之外的位置保存导出文件。");
        }
        if(content.Length>5*1024*1024)throw new InvalidDataException("导出文件超过 5 MiB 上限。");
        var temporary=target+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            await using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,8192,FileOptions.Asynchronous|FileOptions.WriteThrough))
            {await file.WriteAsync(content,ct);await file.FlushAsync(ct);file.Flush(true);}
            ct.ThrowIfCancellationRequested();
            if(File.Exists(target))File.Replace(temporary,target,null);else File.Move(temporary,target);
        }
        finally {if(File.Exists(temporary))File.Delete(temporary);}
    }
    private static string ResolveLinks(string path)
    {
        var full=Path.GetFullPath(path);var current=Path.GetPathRoot(full)!;
        foreach(var part in full[current.Length..].Split(Path.DirectorySeparatorChar,StringSplitOptions.RemoveEmptyEntries))
        {
            current=Path.Combine(current,part);
            FileSystemInfo? entry=Directory.Exists(current)?new DirectoryInfo(current):File.Exists(current)?new FileInfo(current):null;
            if(entry is not null && (entry.Attributes&FileAttributes.ReparsePoint)!=0)
                current=(entry.ResolveLinkTarget(true)??throw new IOException("无法验证导出目录链接。")).FullName;
        }
        return current;
    }
}
