using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using DlutTimetable.Contracts;
using DlutTimetable.Infrastructure;
using DlutTimetable.Infrastructure.Installation;
using DlutTimetable.Windows;

namespace DlutTimetable.Desktop;

/// <summary>Runs before opening application storage, so installing never opens a second live database.</summary>
internal sealed class InstallerWindow : Window
{
    private readonly string packageDirectory,installDirectory,dataDirectory;
    private readonly bool integrated;
    private readonly TextBlock status=new(){TextWrapping=TextWrapping.Wrap,Margin=new(0,18,0,12)};
    private readonly TextBox source=new(){MinHeight=32,Padding=new(7)};
    private readonly Button install=new(){Content="安装 / 更新 / 修复",MinWidth=140,Padding=new(12,8,12,8)};
    private readonly Button rollback=new(){Content="恢复上一版本",MinWidth=110,Padding=new(12,8,12,8),Margin=new(8,0,0,0),IsEnabled=false};
    private readonly Button launch=new(){Content="打开课表",MinWidth=100,Padding=new(12,8,12,8),Margin=new(8,0,0,0),IsEnabled=false};
    private readonly Button uninstall=new(){Content="卸载…",Padding=new(12,6,12,6),Margin=new(0,14,0,0),HorizontalAlignment=HorizontalAlignment.Left,IsEnabled=false};
    private bool busy;
    private string? executable;
    private ReleasePackage? package;

    internal static string? PathOption(string[] args,string key)
    {
        var positions=args.Select((value,index)=>(value,index)).Where(x=>x.value==key).ToArray();
        if(positions.Length>1 || positions.Length==1 && (positions[0].index+1>=args.Length || args[positions[0].index+1].StartsWith("--",StringComparison.Ordinal)))
            throw new ArgumentException("安装参数无效："+key);
        return positions.Length==0?null:Path.GetFullPath(args[positions[0].index+1]);
    }
    public InstallerWindow(string[] args)
    {
        packageDirectory=PathOption(args,"--package-directory")??AppContext.BaseDirectory;
        var defaultInstall=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","DLUTDesktopTimetable");
        installDirectory=PathOption(args,"--install-directory")??defaultInstall;
        dataDirectory=PathOption(args,"--data-directory")??AuthProfileLocation.ApplicationDirectory;
        integrated=InstallationPaths.Same(installDirectory,defaultInstall)&&InstallationPaths.Same(dataDirectory,AuthProfileLocation.ApplicationDirectory);
        Title="大工课表 · 安装与更新";Width=640;Height=580;MinWidth=580;MinHeight=450;
        WindowStartupLocation=WindowStartupLocation.CenterScreen;Background=new SolidColorBrush(Color.FromRgb(246,247,249));
        FontFamily=new("Microsoft YaHei UI");FontSize=13;
        AutomationProperties.SetAutomationId(this,"InstallerWindow");
        AutomationProperties.SetAutomationId(status,"InstallerStatus");AutomationProperties.SetAutomationId(source,"MigrationSource");
        AutomationProperties.SetAutomationId(install,"InstallUpdate");AutomationProperties.SetAutomationId(rollback,"InstallRollback");AutomationProperties.SetAutomationId(launch,"InstallLaunch");
        var panel=new StackPanel{Margin=new(28)};
        panel.Children.Add(new TextBlock{Text="安装大工课表",FontSize=25,FontWeight=FontWeights.SemiBold,Margin=new(0,0,0,12)});
        panel.Children.Add(new TextBlock{Text="按用户安装，无需管理员权限。更新保留课表和自启偏好。",TextWrapping=TextWrapping.Wrap});
        panel.Children.Add(new TextBlock{Text="程序目录\n"+installDirectory+"\n\n个人数据\n"+dataDirectory,TextWrapping=TextWrapping.Wrap,Margin=new(0,16,0,14),Foreground=Brushes.DimGray});
        panel.Children.Add(new TextBlock{Text="迁移已有课表（可选，选择含 timetable.db 的目录）",Margin=new(0,0,0,6)});
        var row=new DockPanel();var browse=new Button{Content="选择…",Margin=new(8,0,0,0),Padding=new(12,6,12,6)};
        DockPanel.SetDock(browse,Dock.Right);row.Children.Add(browse);row.Children.Add(source);panel.Children.Add(row);
        source.Text=PathOption(args,"--migrate-from")??"";
        browse.Click+=(_,_)=>{var dialog=new Microsoft.Win32.OpenFolderDialog{Title="选择已有课表数据目录"};if(dialog.ShowDialog(this)==true)source.Text=dialog.FolderName;};
        panel.Children.Add(status);var actions=new StackPanel{Orientation=Orientation.Horizontal};actions.Children.Add(install);actions.Children.Add(rollback);actions.Children.Add(launch);panel.Children.Add(actions);
        AutomationProperties.SetAutomationId(uninstall,"InstallUninstall");panel.Children.Add(uninstall);
        Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        install.IsEnabled=false;Loaded+=async(_,_)=>await InitializeAsync();
        install.Click+=async(_,_)=>await RunAsync(false);rollback.Click+=async(_,_)=>await RunAsync(true);
        launch.Click+=async(_,_)=>await LaunchAsync();
        uninstall.Click+=async(_,_)=>await OpenUninstallerAsync();
        Closing+=(_,e)=>{if(busy){e.Cancel=true;status.Text="正在完成安装事务，请稍候再关闭。";}};
        Closed+=(_,_)=>System.Windows.Application.Current.Shutdown();
    }
    private async Task InitializeAsync()
    {
        try
        {
            status.Text="正在校验发布包…";
            package=await ReleasePackage.VerifyAsync(packageDirectory,ReleaseSignatureVerifier.Verify);
            status.Text=(package.Manifest.SignedRelease?"已验证发行签名 ":"开发预览 ")+package.Manifest.Version
                +(package.Manifest.SignedRelease?"\n":"（未签名）\n")+(integrated?"安装后可从开始菜单打开。":"隔离安装：不会修改系统启动项或开始菜单。");
            var state=await UserInstallation.ReadStateAsync(installDirectory);
            if(state is not null && (!InstallationPaths.Same(state.DataDirectory,dataDirectory)||state.UserIntegration!=integrated))
                throw new InvalidDataException("已有安装的数据位置与当前选择不同，请使用原安装配置。");
            if(state is not null)
            {executable=InstallationPaths.Child(installDirectory,state.CurrentVersion+"/DlutTimetable.Desktop.exe");launch.IsEnabled=uninstall.IsEnabled=true;rollback.IsEnabled=state.PreviousVersion is not null;}
            install.IsEnabled=true;
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
        {await FailureAsync(DiagnosticCode.InstallationFailed,"安装包或安装记录无法校验，请检查完整发布包、目录与权限。");}
    }
    private async Task RunAsync(bool restore)
    {
        if(busy||package is null)return;
        busy=true;install.IsEnabled=rollback.IsEnabled=launch.IsEnabled=source.IsEnabled=uninstall.IsEnabled=false;
        try
        {
            var selected=package;
            if(restore)
            {
                var state=await UserInstallation.ReadStateAsync(installDirectory);
                if(state?.PreviousVersion is not {} previous)throw new InvalidDataException("没有可恢复的上一版本。");
                selected=await ReleasePackage.VerifyAsync(InstallationPaths.Child(installDirectory,previous),ReleaseSignatureVerifier.Verify,allowExtraFiles:true);
            }
            status.Text=restore?"正在恢复上一版本，保留当前课表和设置…":"正在安装，原始迁移目录会保留…";
            var service=new UserInstallation(new StartupRegistration(),SingleInstance.StopExistingAsync,
                phase=>Dispatcher.BeginInvoke(()=>status.Text=phase switch
                {InstallCommitStage.FilesVerified=>"程序文件已校验，正在等待课表正常退出…",InstallCommitStage.DataCommitted=>"课表已就绪，正在保留自启偏好…",_=>"正在完成版本切换…"}),ReleaseSignatureVerifier.Verify);
            var request=new InstallationRequest(installDirectory,dataDirectory,
                restore||string.IsNullOrWhiteSpace(source.Text)?null:Path.GetFullPath(source.Text.Trim()),integrated);
            // Copying/verifying the bundled runtime can take seconds on a slow disk.
            // Keep all file work off the dispatcher so the window remains responsive.
            var result=await Task.Run(()=>service.InstallAsync(selected,request));
            executable=result.Executable;
            var warning="";
            if(integrated)
            {
                try{InstallShortcuts.Create(executable);new InstalledApplicationRegistration().Register(executable,installDirectory,dataDirectory,selected.Manifest.Version);}
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Security.SecurityException or System.Runtime.InteropServices.COMException)
                {warning="\n系统入口未完整登记，可直接打开程序目录中的 EXE。";}
            }
            status.Text=(restore?"已恢复上一版本。":result.Repaired?"程序已修复，原损坏目录及额外文件保留。":"安装完成。")+(result.DataMigrated?" 已迁移课表、私人备注和设置，来源文件保持不变。":" 个人数据保持原位。")+warning;
            launch.IsEnabled=uninstall.IsEnabled=true;rollback.IsEnabled=result.State.PreviousVersion is not null;
        }
        catch(Exception)
        {await FailureAsync(DiagnosticCode.InstallationFailed,"安装未完成，请检查目录、迁移数据是否冲突及权限；已有课表保留。");}
        finally {busy=false;install.IsEnabled=source.IsEnabled=true;}
    }
    private async Task FailureAsync(DiagnosticCode code,string message)
    {
        var id=await DiagnosticJournal.WriteFaultAsync(Path.Combine(dataDirectory,"logs"),code);
        status.Text=$"{message}（{code} · {id:N}）";
    }
    private async Task LaunchAsync()
    {
        if(executable is null)return;
        try
        {
            var start=new ProcessStartInfo(executable){UseShellExecute=false,WorkingDirectory=Path.GetDirectoryName(executable)!};
            if(!integrated){start.ArgumentList.Add("--data-directory");start.ArgumentList.Add(dataDirectory);}
            Process.Start(start);Close();
        }
        catch(Exception ex) when(ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException){await FailureAsync(DiagnosticCode.InstallationLaunchFailed,"程序未能启动，请修复安装后重试。");}
    }
    private async Task OpenUninstallerAsync()
    {
        try
        {
            var start=new ProcessStartInfo(Environment.ProcessPath??throw new IOException("找不到当前程序。")){UseShellExecute=false};
            if(Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet",StringComparison.OrdinalIgnoreCase))start.ArgumentList.Add(typeof(App).Assembly.Location);
            foreach(var arg in new[]{"--uninstall","--install-directory",installDirectory,"--data-directory",dataDirectory})start.ArgumentList.Add(arg);
            Process.Start(start);Close();
        }
        catch(Exception){await FailureAsync(DiagnosticCode.UninstallationFailed,"无法打开卸载窗口，请使用完整发布包修复后重试。");}
    }
}

internal static class InstallShortcuts
{
    public static void Create(string executable)
    {
        var directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),"大工课表");Directory.CreateDirectory(directory);
        var type=Type.GetTypeFromProgID("WScript.Shell")??throw new IOException("系统缺少快捷方式组件。");
        dynamic shell=Activator.CreateInstance(type)!;
        try
        {
            foreach(var (name,args) in new[]{("大工课表.lnk",""),("安装与版本恢复.lnk","--install"),("卸载大工课表.lnk","--uninstall")})
            {
                var temp=Path.Combine(directory,".dlut-"+Guid.NewGuid().ToString("N")+".lnk");
                try
                {
                    dynamic shortcut=shell.CreateShortcut(temp);
                    try {shortcut.TargetPath=executable;shortcut.Arguments=args;shortcut.WorkingDirectory=Path.GetDirectoryName(executable);shortcut.Description="大工课表";shortcut.Save();}
                    finally {System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);}
                    File.Move(temp,Path.Combine(directory,name),true);
                }
                finally{if(File.Exists(temp))File.Delete(temp);}
            }
        }
        finally{System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);}
    }
}
