using System.IO;
using System.Windows;
using System.Windows.Controls;
using DlutTimetable.Contracts;
using DlutTimetable.Domain;
using DlutTimetable.Windows.Materials;
using DlutTimetable.Windows;
using Microsoft.Win32;

namespace DlutTimetable.Desktop;

internal sealed class SettingsWindow : Window
{
    private readonly DesktopSession session;
    private Action flushMaterialPreview=()=>{};
    private Action flushLayoutPreview=()=>{};
    public void FlushMaterialPreview(){flushLayoutPreview();flushMaterialPreview();}
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 0), Foreground = System.Windows.Media.Brushes.SlateGray };
    private readonly TextBlock dataSummary=new(){TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,12)};
    public void RefreshDataSummary()=>dataSummary.Text=session.DataSummary;
    public SettingsWindow(DesktopSession session)
    {
        this.session = session;
        System.Windows.Automation.AutomationProperties.SetAutomationId(status,"SettingsStatus");
        SettingsChrome.Apply(this);
        Title = "大工课表 · 设置"; Width = 660; Height = 510; MinWidth = 580; MinHeight = 400;
        FontSize = 13;
        var root = new DockPanel { Margin = new(22) }; Content = root;
        var heading = new TextBlock { Text = "课表设置", FontSize = 23, FontWeight = FontWeights.Medium, Margin = new(0, 0, 0, 18) };
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
        var tabs = new TabControl(); root.Children.Add(tabs);
        var layout = Page(tabs, "布局与四态");
        var scale = new ComboBox { ItemsSource = new[] { 0.6, 0.7, 0.8, 1d, 1.2 }, SelectedItem = session.Scene.Current.Expanded.Scale, Width = 170, HorizontalAlignment = HorizontalAlignment.Left };
        Field(layout, "等比缩放（780×468，固定 5:3）", scale);
        var edge = new ComboBox { ItemsSource = new[] { new KeyValuePair<EdgeDirection,string>(EdgeDirection.Left,"左侧"),
            new(EdgeDirection.Top,"顶部"), new(EdgeDirection.Right,"右侧"), new(EdgeDirection.Bottom,"底部") },
            SelectedValuePath = "Key", DisplayMemberPath = "Value", SelectedValue = session.Scene.Current.Edge, Width = 170, HorizontalAlignment = HorizontalAlignment.Left };
        Field(layout, "侧边方向", edge);
        var level = new ComboBox { ItemsSource = new[] { new KeyValuePair<WindowLevel,string>(WindowLevel.Desktop,"桌面层"), new(WindowLevel.Topmost,"置顶层") },
            SelectedValuePath = "Key", DisplayMemberPath = "Value", SelectedValue = session.Scene.Current.EdgeLevel, Width = 170, HorizontalAlignment = HorizontalAlignment.Left };
        Field(layout, "侧边层级：桌面层被应用遮挡时不响应；置顶层仅可见区域响应", level);
        var stripThickness = DimensionSlider(session.Scene.Current.StripThickness,4,12,"侧边条厚度");
        var stripLength = DimensionSlider(session.Scene.Current.StripLength,48,120,"侧边条长度");
        var stripSize = new TextBlock { Margin = new(0,6,0,6) };
        void UpdateStripSize() => stripSize.Text = $"侧边条：厚 {stripThickness.Value:0} × 长 {stripLength.Value:0} DIP";
        UpdateStripSize(); layout.Children.Add(stripSize);
        Field(layout,"条厚度（4–12 DIP）",stripThickness);
        Field(layout,"条长度（48–120 DIP）",stripLength);
        void ApplyLayout()
        {
            if (scale.SelectedItem is double s && edge.SelectedValue is EdgeDirection e && level.SelectedValue is WindowLevel l)
                session.Scene.Configure(e, session.Scene.Current.EdgeOffset, l, s,stripThickness.Value,stripLength.Value);
        }
        var layoutPreview = new System.Windows.Threading.DispatcherTimer { Interval=TimeSpan.FromMilliseconds(80) };
        layoutPreview.Tick+=(_,_)=>{layoutPreview.Stop();ApplyLayout();};
        void QueueStripSize(){UpdateStripSize();layoutPreview.Stop();layoutPreview.Start();}
        stripThickness.ValueChanged+=(_,_)=>QueueStripSize();stripLength.ValueChanged+=(_,_)=>QueueStripSize();
        flushLayoutPreview=()=>{if(layoutPreview.IsEnabled){layoutPreview.Stop();ApplyLayout();}};
        scale.SelectionChanged += (_, _) => ApplyLayout(); edge.SelectionChanged += (_, _) => ApplyLayout(); level.SelectionChanged += (_, _) => ApplyLayout();
        Button(layout, "恢复可见课表", () => session.Route(AppCommandKind.RestoreTimetable));
        Button(layout,"修复桌面显示",()=>{FlushMaterialPreview();session.RecoverDesktop();});
        var material = Page(tabs, "材质与配色");
        var materialKind = new ComboBox { ItemsSource = new[] {
            new KeyValuePair<MaterialKind,string>(MaterialKind.ClearFrosted,"轻透磨砂"),new(MaterialKind.LiquidEdge,"液态边缘"),new(MaterialKind.Solid,"实色") },
            SelectedValuePath="Key", DisplayMemberPath="Value", SelectedValue=session.Material.Kind, Width=170,HorizontalAlignment=HorizontalAlignment.Left };
        Field(material,"背景材质",materialKind);
        System.Windows.Automation.AutomationProperties.SetName(materialKind,"背景材质");
        var dark = new CheckBox { Content="加深玻璃染色",IsChecked=session.Material.Dark, Margin=new(0,12,0,6) };
        material.Children.Add(dark);
        var backgroundTransparency = TransparencySlider(session.Material.BackgroundTransparency,40,95,"背景透明度");
        var foregroundTransparency = TransparencySlider(session.Material.ForegroundTransparency,0,45,"前景透明度");
        var backgroundLabel=new TextBlock();var foregroundLabel=new TextBlock();
        material.Children.Add(backgroundLabel);material.Children.Add(backgroundTransparency);
        material.Children.Add(foregroundLabel);material.Children.Add(foregroundTransparency);
        void UpdateTransparencyLabels()
        {
            backgroundLabel.Text=$"背景透明度 · {backgroundTransparency.Value:F0}%";
            foregroundLabel.Text=$"前景透明度 · {foregroundTransparency.Value:F0}%";
            backgroundTransparency.IsEnabled=materialKind.SelectedValue is not MaterialKind.Solid;
        }
        UpdateTransparencyLabels();
        var displacement = new Slider { Minimum=0,Maximum=12,TickFrequency=1,IsSnapToTickEnabled=true,
            Value=session.Material.Displacement,Width=220,HorizontalAlignment=HorizontalAlignment.Left,IsEnabled=session.Material.Kind==MaterialKind.LiquidEdge };
        Field(material,"边缘折射强度",displacement);
        System.Windows.Automation.AutomationProperties.SetName(displacement,"液态边缘位移强度");
        var dispersion=new Slider{Minimum=0,Maximum=12,TickFrequency=1,IsSnapToTickEnabled=true,Value=session.Material.Dispersion*100,
            Width=220,HorizontalAlignment=HorizontalAlignment.Left,IsEnabled=session.Material.Kind==MaterialKind.LiquidEdge};
        Field(material,"边缘色散（0 关闭，默认 4）",dispersion);
        System.Windows.Automation.AutomationProperties.SetName(dispersion,"液态边缘色散强度");
        var lightAngle=new Slider{Minimum=0,Maximum=360,TickFrequency=15,IsSnapToTickEnabled=true,Value=session.Material.LightAngle,
            Width=220,HorizontalAlignment=HorizontalAlignment.Left,IsEnabled=session.Material.Kind==MaterialKind.LiquidEdge};
        Field(material,"光线方向（度）",lightAngle);
        System.Windows.Automation.AutomationProperties.SetName(lightAngle,"液态边缘光线方向");
        var materialStatus = new TextBlock { Text=session.MaterialStatus,TextWrapping=TextWrapping.Wrap,Margin=new(0,12,0,0) };
        material.Children.Add(materialStatus);
        Button(material,"重建当前材质",()=>{FlushMaterialPreview();session.RetryMaterial();});
        material.Children.Add(new TextBlock { Text="玻璃透出并虚化背后的内容。液态边缘可调整折射、色散和光线方向，折射强度设为 0 可关闭增强。动态壁纸或视频背景的边缘效果可能无法同步，可选轻透磨砂。系统关闭透明效果时使用实色。",
            TextWrapping=TextWrapping.Wrap,Margin=new(0,12,0,0) });
        void ApplyMaterial()
        {
            if (materialKind.SelectedValue is MaterialKind kind)
            {
                UpdateTransparencyLabels();
                displacement.IsEnabled=kind==MaterialKind.LiquidEdge;
                dispersion.IsEnabled=lightAngle.IsEnabled=kind==MaterialKind.LiquidEdge;
                session.SetMaterial(session.Material with { Kind=kind,Dark=dark.IsChecked==true,Displacement=displacement.Value,
                    BackgroundTransparency=backgroundTransparency.Value/100,ForegroundTransparency=foregroundTransparency.Value/100,
                    Dispersion=dispersion.Value/100,LightAngle=lightAngle.Value });
            }
        }
        var previewTimer=new System.Windows.Threading.DispatcherTimer { Interval=TimeSpan.FromMilliseconds(80) };
        previewTimer.Tick+=(_,_)=>{previewTimer.Stop();ApplyMaterial();};
        void PreviewTransparency(object sender,RoutedPropertyChangedEventArgs<double> args)
        { UpdateTransparencyLabels();previewTimer.Stop();previewTimer.Start(); }
        backgroundTransparency.ValueChanged+=PreviewTransparency;foregroundTransparency.ValueChanged+=PreviewTransparency;
        flushMaterialPreview=()=>{if(previewTimer.IsEnabled){previewTimer.Stop();ApplyMaterial();}};
        Closed+=(_,_)=>FlushMaterialPreview();
        materialKind.SelectionChanged += (_,_) => ApplyMaterial(); dark.Checked += (_,_) => ApplyMaterial(); dark.Unchecked += (_,_) => ApplyMaterial();
        displacement.ValueChanged += (_,_) => {previewTimer.Stop();previewTimer.Start();};
        dispersion.ValueChanged+=(_,_)=>{previewTimer.Stop();previewTimer.Start();};
        lightAngle.ValueChanged+=(_,_)=>{previewTimer.Stop();previewTimer.Start();};
        void MaterialChanged(string value) => materialStatus.Text=value;
        session.MaterialStatusChanged += MaterialChanged;
        Closed += (_,_) => session.MaterialStatusChanged -= MaterialChanged;
        var data = Page(tabs, "数据与账号");
        dataSummary.Text=session.DataSummary;
        System.Windows.Automation.AutomationProperties.SetAutomationId(dataSummary,"SchoolDataSummary");
        data.Children.Add(dataSummary);
        var login=new Button {Content="从学校刷新课表",Padding=new(14,7,14,7),Margin=new(0,6,0,0),HorizontalAlignment=HorizontalAlignment.Left,IsEnabled=session.CanLogin};
        System.Windows.Automation.AutomationProperties.SetAutomationId(login,"SchoolManualRefresh");
        login.Click+=async (_,_)=>await session.OpenLoginAsync();data.Children.Add(login);
        var cancelSchool=new Button {Content="取消学校读取",Padding=new(14,7,14,7),Margin=new(0,6,0,0),HorizontalAlignment=HorizontalAlignment.Left,IsEnabled=session.CanCancelSchool};
        System.Windows.Automation.AutomationProperties.SetAutomationId(cancelSchool,"SchoolCancelRefresh");
        cancelSchool.Click+=async (_,_)=>await session.CancelSchoolAsync();data.Children.Add(cancelSchool);
        var clearSession=new Button {Content="删除学校登录会话",Padding=new(14,7,14,7),Margin=new(0,6,0,0),HorizontalAlignment=HorizontalAlignment.Left,IsEnabled=session.CanClearSession};
        void LoginStateChanged(){login.IsEnabled=session.CanLogin;cancelSchool.IsEnabled=session.CanCancelSchool;clearSession.IsEnabled=session.CanClearSession;RefreshDataSummary();}
        session.LoginStateChanged+=LoginStateChanged;Closed+=(_,_)=>session.LoginStateChanged-=LoginStateChanged;
        data.Children.Add(new TextBlock {Text="点击后打开学校页面，完成登录或二次验证并进入本人课表，读取成功后更新桌面课表。取消或失败会保留已有课表；启动、唤醒和跨日不会自动抓取。",TextWrapping=TextWrapping.Wrap,Margin=new(0,6,0,10)});
        clearSession.Click+=async (_,_)=>
        {
            if(MessageBox.Show(this,"将关闭本程序的学校登录窗口并清除保存的登录状态，下次访问学校需要重新登录。\n\n已缓存课表、私人备注、布局和自启设置会保留。","删除学校登录会话",MessageBoxButton.OKCancel,MessageBoxImage.Question,MessageBoxResult.Cancel)==MessageBoxResult.OK)
                await session.ClearSessionAsync();
        };
        data.Children.Add(clearSession);
        var calendar=new Button{Content="校历与节次…",Padding=new(14,7,14,7),Margin=new(0,6,0,0),HorizontalAlignment=HorizontalAlignment.Left,IsEnabled=session.CanEditCalendar};
        System.Windows.Automation.AutomationProperties.SetAutomationId(calendar,"OpenCalendarSettings");
        calendar.Click+=(_,_)=>session.OpenCalendar();data.Children.Add(calendar);
        void CalendarAvailability(){calendar.IsEnabled=session.CanEditCalendar;}
        session.LoginStateChanged+=CalendarAvailability;Closed+=(_,_)=>session.LoginStateChanged-=CalendarAvailability;
        var import=new Button{Content="导入 JSON",Padding=new(14,7,14,7),Margin=new(0,6,0,0),HorizontalAlignment=HorizontalAlignment.Left,IsEnabled=session.CanImportData};
        import.Click+=async (_,_)=>
        {
            var dialog = new OpenFileDialog {Title="导入个人课表", Filter = "标准课表 JSON|*.json" };
            if (dialog.ShowDialog(this) == true) await session.ImportAsync(dialog.FileName);
        };
        data.Children.Add(import);
        var retryCache=new Button{Content="重新读取缓存",Padding=new(14,7,14,7),Margin=new(0,6,0,0),HorizontalAlignment=HorizontalAlignment.Left,IsEnabled=session.CanRetryCache};
        retryCache.Click+=async (_,_)=>await session.RetryCacheAsync();data.Children.Add(retryCache);
        void CacheChanged(){import.IsEnabled=session.CanImportData;retryCache.IsEnabled=session.CanRetryCache;}
        session.LoginStateChanged+=CacheChanged;Closed+=(_,_)=>session.LoginStateChanged-=CacheChanged;
        Button(data, "导出当前 JSON", async () =>
        {
            var dialog = new SaveFileDialog {Title="导出个人课表",Filter = "标准课表 JSON|*.json", FileName = "timetable.json" };
            if (dialog.ShowDialog(this) == true) await session.ExportAsync(dialog.FileName);
        });
        data.Children.Add(new TextBlock {Text="导出的 JSON 包含个人课程、教师、地点等信息。脱敏诊断仅包含版本、状态与数量。",TextWrapping=TextWrapping.Wrap,Margin=new(0,8,0,4)});
        Button(data,"导出脱敏诊断",async ()=>
        {
            var dialog=new SaveFileDialog{Title="导出脱敏诊断",Filter="脱敏诊断 JSON|*.json",FileName="timetable-diagnostics.json"};
            if(dialog.ShowDialog(this)==true)await session.ExportDiagnosticsAsync(dialog.FileName);
        });
        Button(data, "预览虚构示例", async () => await session.LoadDemoAsync());
        var general = Page(tabs, "通用");
        var reducedMotion=new CheckBox {Content="减少动画（≤120 ms）",IsChecked=session.ReducedMotion,Margin=new(0,0,0,12)};
        reducedMotion.Checked+=(_,_)=>session.SetReducedMotion(true);
        reducedMotion.Unchecked+=(_,_)=>session.SetReducedMotion(false);
        general.Children.Add(reducedMotion);
        var startup=new CheckBox {Content="登录 Windows 后启动",IsChecked=session.CanSetStartup && session.Startup.Desired,
            IsEnabled=session.CanSetStartup && !session.Startup.Busy};
        general.Children.Add(startup);
        var startupStatus=new TextBlock {Text=session.Startup.Message,TextWrapping=TextWrapping.Wrap,Margin=new(0,8,0,8)};
        general.Children.Add(startupStatus);
        var repairStartup=new Button {Content="重新应用当前偏好",HorizontalAlignment=HorizontalAlignment.Left,Padding=new(10,5,10,5),
            Visibility=session.Startup.OutOfSync?Visibility.Visible:Visibility.Collapsed,IsEnabled=session.CanSetStartup};
        general.Children.Add(repairStartup);
        var updatingStartup=false;
        void StartupChanged(DlutTimetable.Application.StartupState state)
        {
            updatingStartup=true;
            try {startup.IsChecked=session.CanSetStartup && state.Desired;startup.IsEnabled=session.CanSetStartup && !state.Busy;
                startupStatus.Text=state.Message;repairStartup.Visibility=state.OutOfSync?Visibility.Visible:Visibility.Collapsed;
                repairStartup.IsEnabled=session.CanSetStartup && !state.Busy;}
            finally {updatingStartup=false;}
        }
        startup.Checked+=async (_,_)=>{if(!updatingStartup)await session.SetStartupAsync(true);};
        startup.Unchecked+=async (_,_)=>{if(!updatingStartup)await session.SetStartupAsync(false);};
        repairStartup.Click+=async (_,_)=>await session.SetStartupAsync(session.Startup.Desired);
        session.StartupChanged+=StartupChanged;
        Activated+=async (_,_)=>await session.RefreshStartupAsync();
        Closed+=(_,_)=>session.StartupChanged-=StartupChanged;
        general.Children.Add(new TextBlock { Text = "退出当前程序；下次登录启动由自启设置决定。", Margin = new(0, 18, 0, 12), TextWrapping = TextWrapping.Wrap });
        Button(general, "退出程序", () => session.Route(AppCommandKind.ExitApplication));
        SetStatus(session.Status);
        WindowFrame.Apply(this);
    }
    private static StackPanel Page(TabControl tabs, string title)
    {
        var panel = new StackPanel { Margin = new(22) };
        tabs.Items.Add(new TabItem { Header = title, Content = new ScrollViewer
        {
            Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Visible,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,
            CanContentScroll=false,PanningMode=PanningMode.VerticalOnly
        } }); return panel;
    }
    private static void Field(Panel panel, string label, UIElement control)
    { panel.Children.Add(new TextBlock { Text = label, Margin = new(0, 8, 0, 6), TextWrapping = TextWrapping.Wrap }); panel.Children.Add(control); }
    private static Slider TransparencySlider(double value,double minimum,double maximum,string name)
    {
        var slider=new Slider {Minimum=minimum,Maximum=maximum,Value=value*100,TickFrequency=1,IsSnapToTickEnabled=true,
            Width=260,HorizontalAlignment=HorizontalAlignment.Left,Margin=new(0,6,0,10)};
        System.Windows.Automation.AutomationProperties.SetName(slider,name);
        System.Windows.Automation.AutomationProperties.SetHelpText(slider,"数值越高越透明，调整后自动保存");
        return slider;
    }
    private static Slider DimensionSlider(double value,double minimum,double maximum,string name)
    {
        var slider=new Slider {Minimum=minimum,Maximum=maximum,Value=value,TickFrequency=1,IsSnapToTickEnabled=true,
            Width=260,HorizontalAlignment=HorizontalAlignment.Left};
        System.Windows.Automation.AutomationProperties.SetName(slider,name);
        System.Windows.Automation.AutomationProperties.SetHelpText(slider,"单位 DIP，调整后自动保存");
        return slider;
    }
    private static void Button(Panel panel, string label, Action action)
    { var button = new Button { Content = label, Padding = new(14, 7, 14, 7), Margin = new(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left }; button.Click += (_, _) => action(); panel.Children.Add(button); }
    private static void Button(Panel panel, string label, Func<Task> action)
    { var button = new Button { Content = label, Padding = new(14, 7, 14, 7), Margin = new(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left }; button.Click += async (_, _) => await action(); panel.Children.Add(button); }
    public void SetStatus(string value) => status.Text = value;
}
