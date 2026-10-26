using System.IO;
using DlutTimetable.Contracts;

namespace DlutTimetable.Windows;

public enum ProfileClearOutcome { Cleared, AlreadyEmpty, Busy, UnsafeDirectory, Failed }

public sealed class AuthProfileCleaner
{
    private readonly string root,profile;
    public AuthProfileCleaner(string applicationDirectory)
    {
        root=Path.GetFullPath(applicationDirectory).TrimEnd(Path.DirectorySeparatorChar);
        profile=Path.GetFullPath(Path.Combine(root,"auth-profile"));
    }
    // The mutex is acquired and released on this same background thread.
    public Task<ProfileClearOutcome> ClearAsync(CancellationToken ct=default)=>Task.Run(()=>Clear(ct),ct);
    private ProfileClearOutcome Clear(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var lease=new Mutex(false,AuthProfileLocation.MutexName(profile));var acquired=false;
        try
        {
            try {acquired=lease.WaitOne(0);}catch(AbandonedMutexException){acquired=true;}
            if(!acquired)return ProfileClearOutcome.Busy;
            if(!Directory.Exists(root))return ProfileClearOutcome.AlreadyEmpty;
            if((File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0 || Path.GetDirectoryName(profile)!=root)
                return ProfileClearOutcome.UnsafeDirectory;
            if(!Directory.Exists(profile))return File.Exists(profile)?ProfileClearOutcome.UnsafeDirectory:ProfileClearOutcome.AlreadyEmpty;
            var directories=new Stack<string>();directories.Push(profile);
            while(directories.TryPop(out var directory))
            {
                ct.ThrowIfCancellationRequested();
                if(!IsOwned(directory) || (File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)return ProfileClearOutcome.UnsafeDirectory;
                foreach(var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    ct.ThrowIfCancellationRequested();
                    if(!IsOwned(entry.FullName) || (entry.Attributes&FileAttributes.ReparsePoint)!=0)return ProfileClearOutcome.UnsafeDirectory;
                    if((entry.Attributes&FileAttributes.Directory)!=0)directories.Push(entry.FullName);
                }
            }
            ct.ThrowIfCancellationRequested();
            // Every resolved deletion target was checked within the exact owned profile.
            Directory.Delete(profile,recursive:true);
            return ProfileClearOutcome.Cleared;
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){return ProfileClearOutcome.Failed;}
        finally {if(acquired)lease.ReleaseMutex();}
    }
    private bool IsOwned(string path)
    {
        var full=Path.GetFullPath(path);
        return string.Equals(full,profile,StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(profile+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    }
}
