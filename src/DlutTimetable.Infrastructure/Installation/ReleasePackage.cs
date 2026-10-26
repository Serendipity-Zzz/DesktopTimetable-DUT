using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DlutTimetable.Infrastructure.Installation;

public sealed record ReleaseFile(string Path,long Bytes,string Sha256);
public sealed record ReleaseManifest(string Product,string Version,string Rid,bool SelfContained,string RuntimeVersion,
    DateTimeOffset CreatedUtc,string EntryPoint,string Worker,string WebView2Runtime,bool SignedRelease,ImmutableArray<ReleaseFile> Files);

public sealed class ReleasePackage
{
    public string Directory {get;}
    public ReleaseManifest Manifest {get;}
    public string Fingerprint {get;}
    public string? SignatureFingerprint {get;}
    public string VersionDirectoryName=>Manifest.Version+"-"+Fingerprint[..16];
    public bool OwnsVersionDirectoryName(string name)=>name==VersionDirectoryName ||
        name.StartsWith(VersionDirectoryName+"-repair-",StringComparison.Ordinal)
        && name.Length==VersionDirectoryName.Length+16 && name[(VersionDirectoryName.Length+8)..].All(Uri.IsHexDigit);
    private ReleasePackage(string directory,ReleaseManifest manifest,string fingerprint,string? signatureFingerprint)
    {Directory=directory;Manifest=manifest;Fingerprint=fingerprint;SignatureFingerprint=signatureFingerprint;}
    public static async Task<ReleasePackage> VerifyAsync(string directory,Func<string,bool>? signatureVerifier=null,CancellationToken ct=default,bool allowExtraFiles=false)
    {
        directory=InstallationPaths.Full(directory);InstallationPaths.NoLinks(directory);
        var manifestPath=InstallationPaths.Child(directory,"release-manifest.json");
        if(!File.Exists(manifestPath) || new FileInfo(manifestPath).Length>2*1024*1024)throw new InvalidDataException("缺少有效发布清单。");
        var bytes=await File.ReadAllBytesAsync(manifestPath,ct);
        using(var document=JsonDocument.Parse(bytes,new(){MaxDepth=16}))Unique(document.RootElement);
        var manifest=JsonSerializer.Deserialize<ReleaseManifest>(bytes,DatasetJson.Options)??throw new InvalidDataException("发布清单为空。");
        if(manifest.Product!="DLUT Desktop Timetable" || manifest.Rid!="win-x64" || !manifest.SelfContained
            || manifest.EntryPoint!="DlutTimetable.Desktop.exe" || manifest.Worker!="DlutTimetable.AuthWorker.exe"
            || !Regex.IsMatch(manifest.Version,"^\\d+\\.\\d+\\.\\d+(?:-[a-z0-9.-]+)?$",RegexOptions.CultureInvariant)
            || manifest.Files.IsDefaultOrEmpty || manifest.Files.Length>4096)throw new InvalidDataException("不支持的发布清单。");
        if(!manifest.SignedRelease && !Regex.IsMatch(manifest.Version,"^\\d+\\.\\d+\\.\\d+-(?:dev|preview)(?:[.-][a-z0-9.-]+)?$",RegexOptions.CultureInvariant))
            throw new InvalidDataException("正式版本必须通过发行签名验证。");
        var expected=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long total=0;
        var signaturePath=InstallationPaths.Child(directory,"release-manifest.p7s");
        if(manifest.SignedRelease && (!File.Exists(signaturePath) || new FileInfo(signaturePath).Length is <=0 or >65536))
            throw new InvalidDataException("缺少有效发行清单签名。");
        await using var signatureInput=manifest.SignedRelease?File.OpenRead(signaturePath):null;
        byte[]? signatureBytes=null;
        if(signatureInput is not null)
        {
            if(signatureInput.Length is <=0 or >65536)throw new InvalidDataException("发行清单签名大小无效。");
            signatureBytes=new byte[(int)signatureInput.Length];await signatureInput.ReadExactlyAsync(signatureBytes,ct);
        }
        foreach(var file in manifest.Files)
        {
            if(string.Equals(file.Path,"release-manifest.p7s",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("清单签名不得自包含。");
            ct.ThrowIfCancellationRequested();var path=InstallationPaths.Child(directory,file.Path);
            if(!expected.Add(file.Path.Replace('\\','/')) || file.Bytes<0 || file.Bytes>512L*1024*1024
                || file.Sha256 is not {Length:64} || !file.Sha256.All(Uri.IsHexDigit))throw new InvalidDataException("发布文件清单无效。");
            total=checked(total+file.Bytes);if(total>1024L*1024*1024)throw new InvalidDataException("发布包过大。");
            if(!File.Exists(path) || new FileInfo(path).Length!=file.Bytes)throw new InvalidDataException("发布文件缺失或大小不符。");
            await using var input=File.OpenRead(path);
            if(!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(input,ct)),file.Sha256,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("发布文件校验失败。");
        }
        foreach(var required in new[]{manifest.EntryPoint,manifest.Worker,"hostfxr.dll","hostpolicy.dll","coreclr.dll","DlutTimetable.Desktop.runtimeconfig.json","DlutTimetable.AuthWorker.runtimeconfig.json"})
            if(!expected.Contains(required))throw new InvalidDataException("发布包缺少运行组件。");
        foreach(var item in new DirectoryInfo(directory).EnumerateFileSystemInfos("*",SearchOption.AllDirectories))
        {
            InstallationPaths.NoLinks(item.FullName);
            if(!allowExtraFiles && item is FileInfo && Path.GetRelativePath(directory,item.FullName)!="release-manifest.json"
                && !(manifest.SignedRelease && Path.GetRelativePath(directory,item.FullName)=="release-manifest.p7s")
                && !expected.Contains(Path.GetRelativePath(directory,item.FullName).Replace('\\','/')))
                throw new InvalidDataException("发布目录存在未登记文件。");
        }
        if(manifest.SignedRelease && (signatureVerifier is null || !signatureVerifier(InstallationPaths.Child(directory,manifest.EntryPoint))
            || !signatureVerifier(InstallationPaths.Child(directory,manifest.Worker))))throw new InvalidDataException("发行签名未通过验证。");
        var signatureFingerprint=signatureBytes is not null?Convert.ToHexString(SHA256.HashData(signatureBytes)):null;
        return new(directory,manifest,Convert.ToHexString(SHA256.HashData(bytes)),signatureFingerprint);
    }
    internal static async Task WriteJsonAsync<T>(string path,T value,CancellationToken ct)
    {
        var temp=path+".tmp-"+Guid.NewGuid().ToString("N");
        try
        {
            await using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,8192,FileOptions.Asynchronous|FileOptions.WriteThrough))
            {await JsonSerializer.SerializeAsync(output,value,DatasetJson.Options,ct);await output.FlushAsync(ct);output.Flush(true);}
            if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
        }
        finally {if(File.Exists(temp))File.Delete(temp);}
    }
    private static void Unique(JsonElement value)
    {
        if(value.ValueKind==JsonValueKind.Object)
        {
            var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(var property in value.EnumerateObject()){if(!names.Add(property.Name))throw new InvalidDataException("发布清单有重复字段。");Unique(property.Value);}
        }
        else if(value.ValueKind==JsonValueKind.Array)foreach(var item in value.EnumerateArray())Unique(item);
    }
}
