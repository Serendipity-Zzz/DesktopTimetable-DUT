using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using DlutTimetable.Infrastructure;
using DlutTimetable.Contracts;
using DlutTimetable.Infrastructure.Installation;
using DlutTimetable.Windows;

namespace DlutTimetable.Desktop;

internal sealed class UninstallerWindow : Window
{
    private readonly string directory;
    private readonly string? requestedData;
    private readonly bool quiet;
    private readonly CheckBox keep=new(){Content="保留课表、私人备注、设置和登录状态",IsChecked=true,Margin=new(0,20,0,8)};
    private readonly TextBlock status=new(){TextWrapping=TextWrapping.Wrap,Margin=new(0,12,0,18)};
    private readonly Button uninstall=new(){Content="卸载大工课表",Padding=new(16,8,16,8),IsEnabled=false};
    private bool busy;
    public UninstallerWindow(string[] args)
    {
        directory=InstallerWindow.PathOption(args,"--install-directory")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","DLUTDesktopTimetable");
        requestedData=InstallerWindow.PathOption(args,"--data-directory");quiet=args.Contains("--uninstall-quiet");
        Title="大工课表 · 卸载";Width=560;Height=450;MinWidth=520;MinHeight=360;WindowStartupLocation=WindowStartupLocation.CenterScreen;FontFamily=new("Microsoft YaHei UI");FontSize=13;
        AutomationProperties.SetAutomationId(keep,"UninstallKeepData");AutomationProperties.SetAutomationId(status,"UninstallStatus");AutomationProperties.SetAutomationId(uninstall,"UninstallConfirm");
        var panel=new StackPanel{Margin=new(28)};panel.Children.Add(new TextBlock{Text="卸载大工课表",FontSize=24,FontWeight=FontWeights.SemiBold});
        panel.Children.Add(keep);panel.Children.Add(new TextBlock{Text="取消勾选后，将删除这份安装的课程、备注、布局和专用登录状态，无法撤销。其它课表副本共用的登录状态和自行导出的文件会保留。",TextWrapping=TextWrapping.Wrap});
        panel.Children.Add(status);var buttons=new StackPanel{Orientation=Orientation.Horizontal};buttons.Children.Add(uninstall);
        var cancel=new Button{Content="取消",Padding=new(16,8,16,8),Margin=new(10,0,0,0)};buttons.Children.Add(cancel);panel.Children.Add(buttons);Content=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        cancel.Click+=(_,_)=>Close();uninstall.Click+=async(_,_)=>await UninstallAsync();Loaded+=async(_,_)=>await InitializeAsync();
        Closing+=(_,e)=>{if(busy)e.Cancel=true;};Closed+=(_,_)=>System.Windows.Application.Current.Shutdown();
    }
    private async Task InitializeAsync()
    {
        try
        {
            var state=await UserInstallation.ReadStateAsync(directory)??throw new InvalidDataException("找不到已安装的课表。");
            if(requestedData is not null&&!InstallationPaths.Same(requestedData,state.DataDirectory))throw new InvalidDataException("个人数据目录与安装记录不同。");
            status.Text="个人数据："+state.DataDirectory;uninstall.IsEnabled=true;
        }
        catch(Exception){await FailureAsync("安装记录无法校验，未删除数据。请使用匹配发布包修复后重试。");}
    }
    private async Task UninstallAsync()
    {
        if(busy)return;busy=true;keep.IsEnabled=uninstall.IsEnabled=false;
        try
        {
            var retain=keep.IsChecked==true;status.Text="正在等待课表正常退出并核对清理范围…";
            var plan=await Task.Run(async()=>
            {
                var state=await UserInstallation.ReadStateAsync(directory)??throw new InvalidDataException("安装记录已不存在。");
                await SingleInstance.StopExistingAsync(state.DataDirectory,CancellationToken.None);
                return await UserUninstallation.PrepareAsync(directory,retain,signatureVerifier:ReleaseSignatureVerifier.Verify);
            });
            var temporary=InstallationPaths.Full(Path.Combine(Path.GetTempPath(),"DlutUninstall-"+Guid.NewGuid().ToString("N")));InstallationPaths.NoLinks(temporary);Directory.CreateDirectory(temporary);
            var script=InstallationPaths.Child(temporary,"cleanup.ps1");var planPath=InstallationPaths.Child(temporary,"plan.json");
            await using(var embedded=typeof(UninstallerWindow).Assembly.GetManifestResourceStream("DlutTimetable.UninstallCleanup.ps1")??throw new IOException("缺少卸载清理组件。"))
            using(var reader=new StreamReader(embedded,Encoding.UTF8))await File.WriteAllTextAsync(script,await reader.ReadToEndAsync(),new UTF8Encoding(true));
            await File.WriteAllBytesAsync(planPath,JsonSerializer.SerializeToUtf8Bytes(plan,DatasetJson.Options));
            var powershell=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),@"System32\WindowsPowerShell\v1.0\powershell.exe");
            if(!File.Exists(powershell))throw new IOException("找不到系统 Windows PowerShell，未删除程序文件。");
            var start=new ProcessStartInfo(powershell){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=temporary};
            start.Environment["PSModulePath"]=Path.Combine(Path.GetDirectoryName(powershell)!,"Modules");
            foreach(var arg in new[]{"-NoProfile","-NonInteractive","-ExecutionPolicy","Bypass","-File",script,"-PlanPath",planPath,
                "-ParentProcessId",Environment.ProcessId.ToString(CultureInfo.InvariantCulture),"-ParentStartTicks",Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture)})start.ArgumentList.Add(arg);
            if(quiet)start.ArgumentList.Add("-Quiet");
            using var helper=Process.Start(start)??throw new IOException("无法启动系统清理组件。");
            busy=false;Close();
        }
        catch(Exception){await FailureAsync("卸载未完成，未验证的内容会保留，请修复后重试。");busy=false;keep.IsEnabled=uninstall.IsEnabled=true;}
    }
    private async Task FailureAsync(string message)
    {
        var id=await DiagnosticJournal.WriteFaultAsync(Path.Combine(requestedData??AuthProfileLocation.ApplicationDirectory,"logs"),DiagnosticCode.UninstallationFailed);
        status.Text=$"{message}（UninstallationFailed · {id:N}）";
    }
}
