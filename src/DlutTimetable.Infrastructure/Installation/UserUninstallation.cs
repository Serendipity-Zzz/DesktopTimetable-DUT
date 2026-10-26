using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using DlutTimetable.Contracts;

namespace DlutTimetable.Infrastructure.Installation;

public sealed record RemovalFile(string Path,long Bytes,string Sha256);
public sealed record UninstallationPlan(int SchemaVersion,string ProductId,string InstallDirectory,string DataDirectory,
    bool UserIntegration,bool KeepData,string StateSha256,string OwnerSha256,ImmutableArray<RemovalFile> Files,
    ImmutableArray<string> Executables,string? DedicatedProfile,ImmutableArray<string> Warnings);

/// <summary>Builds a bounded removal list. A detached OS helper rechecks it after the UI exits.</summary>
public static class UserUninstallation
{
    public static async Task<UninstallationPlan> PrepareAsync(string directory,bool keepData,CancellationToken ct=default,Func<string,bool>? signatureVerifier=null)
    {
        directory=InstallationPaths.Full(directory);InstallationPaths.NoLinks(directory);
        if(Path.GetDirectoryName(directory) is null)throw new InvalidDataException("不能卸载磁盘根目录。");
        var state=await UserInstallation.ReadStateAsync(directory,ct)??throw new InvalidDataException("目标没有本应用安装记录。");
        var data=InstallationPaths.Full(state.DataDirectory);InstallationPaths.NoLinks(data);
        if(Path.GetDirectoryName(data) is null || Under(directory,data)||Under(data,directory))throw new InvalidDataException("个人数据与程序目录边界异常，未卸载。");
        var ownerPath=InstallationPaths.Child(directory,".dlut-install-owner.json");
        if(!File.Exists(ownerPath)||new FileInfo(ownerPath).Length>32768)throw new InvalidDataException("缺少有效安装所有权记录。");
        var owner=JsonSerializer.Deserialize<InstallationOwner>(await File.ReadAllBytesAsync(ownerPath,ct),DatasetJson.Options);
        if(owner is null||owner.SchemaVersion!=1||owner.ProductId!=UserInstallation.ProductId||!InstallationPaths.Same(owner.DataDirectory,data)||owner.UserIntegration!=state.UserIntegration)
            throw new InvalidDataException("安装记录与所有权记录不一致。");
        var files=ImmutableArray.CreateBuilder<RemovalFile>();var executables=ImmutableArray.CreateBuilder<string>();var warnings=ImmutableArray.CreateBuilder<string>();
        var versions=InstallationPaths.Child(directory,"versions");
        if(!Directory.Exists(versions))throw new InvalidDataException("缺少程序版本目录。");
        foreach(var version in Directory.EnumerateDirectories(versions))
        {
            InstallationPaths.NoLinks(version);
            ReleasePackage package;
            try {package=await ReleasePackage.VerifyAsync(version,signatureVerifier,ct,allowExtraFiles:true);}
            catch(Exception ex) when(ex is IOException or InvalidDataException or JsonException or ArgumentException)
            {
                if(InstallationPaths.Same(version,InstallationPaths.Child(directory,state.CurrentVersion)))throw new InvalidDataException("当前版本损坏，请用完整发布包修复安装后再卸载。",ex);
                warnings.Add("保留未通过校验的旧版本目录。");continue;
            }
            if(!package.OwnsVersionDirectoryName(Path.GetFileName(version)))throw new InvalidDataException("版本目录与发布清单不匹配。");
            var prefix=Path.GetRelativePath(directory,version).Replace('\\','/')+"/";
            foreach(var file in package.Manifest.Files)files.Add(new(prefix+file.Path.Replace('\\','/'),file.Bytes,file.Sha256));
            var manifest=InstallationPaths.Child(version,"release-manifest.json");
            files.Add(new(prefix+"release-manifest.json",new FileInfo(manifest).Length,await HashAsync(manifest,ct)));
            if(package.Manifest.SignedRelease)
            {
                var signature=InstallationPaths.Child(version,"release-manifest.p7s");
                files.Add(new(prefix+"release-manifest.p7s",new FileInfo(signature).Length,await HashAsync(signature,ct)));
            }
            executables.Add(prefix+package.Manifest.EntryPoint);
        }
        if(!executables.Contains(state.CurrentVersion+"/DlutTimetable.Desktop.exe"))throw new InvalidDataException("当前版本未通过校验。");
        string? dedicatedProfile=null;
        if(!keepData)
        {
            using var settingsStore=new SettingsStore(InstallationPaths.Child(data,"settings.json"));var load=await settingsStore.LoadAsync(ct);
            if(load.Status is not (SettingsLoadStatus.Loaded or SettingsLoadStatus.Missing))throw new InvalidDataException("无法确认登录资料归属，请先修复设置或选择保留数据。");
            var ownScope=AuthProfileLocation.ScopeForDataDirectory(data);var scope=(load.Settings??new()).AuthenticationScope(data);
            var markedShared=File.Exists(InstallationPaths.Child(data,UserDataMigration.SharedProfileMarker));
            if(!markedShared&&string.Equals(scope,ownScope,StringComparison.OrdinalIgnoreCase))dedicatedProfile=AuthProfileLocation.ProfileDirectoryForScope(scope);
            else warnings.Add("其它课表副本共用的学校登录状态将保留。");
        }
        return new(1,UserInstallation.ProductId,directory,data,state.UserIntegration,keepData,
            await HashAsync(InstallationPaths.Child(directory,"install-state.json"),ct),await HashAsync(ownerPath,ct),
            files.ToImmutable(),executables.ToImmutable(),dedicatedProfile,warnings.ToImmutable());
    }
    private static bool Under(string parent,string child)=>InstallationPaths.Same(parent,child)||child.StartsWith(parent+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    private static async Task<string> HashAsync(string path,CancellationToken ct)
    {await using var file=File.OpenRead(path);return Convert.ToHexString(await SHA256.HashDataAsync(file,ct));}
}
