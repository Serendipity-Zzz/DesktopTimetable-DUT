using System.Security.Cryptography;
using System.Text;

namespace DlutTimetable.Contracts;

public static class AuthProfileLocation
{
    public static string ApplicationDirectory=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DLUTDesktopTimetable");
    public static string ProfileDirectory=>Path.Combine(ApplicationDirectory,"auth-profile");
    public static string? ScopeForDataDirectory(string dataDirectory)=>
        string.Equals(Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar),ApplicationDirectory,StringComparison.OrdinalIgnoreCase)?null:
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())));
    public static bool ValidScope(string? scope)=>scope is null || scope.Length==64 && scope.All(Uri.IsHexDigit);
    public static string ApplicationDirectoryForScope(string? scope)
    {
        if(!ValidScope(scope))throw new ArgumentException("Invalid authentication profile scope.");
        return scope is null?ApplicationDirectory:Path.Combine(ApplicationDirectory,"profiles",scope.ToUpperInvariant());
    }
    public static string ProfileDirectoryForScope(string? scope)=>Path.Combine(ApplicationDirectoryForScope(scope),"auth-profile");
    public static string MutexName(string profileDirectory)=>"Local\\DlutAuthProfile-"+
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(profileDirectory).ToUpperInvariant())))[..24];
}
