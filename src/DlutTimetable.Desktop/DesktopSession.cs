using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DlutTimetable.Application;
using DlutTimetable.Contracts;
using DlutTimetable.Domain;
using DlutTimetable.Infrastructure;
using DlutTimetable.Windows;
using DlutTimetable.Windows.Materials;

namespace DlutTimetable.Desktop;

internal sealed class DesktopSession
{
    private readonly SqliteStore store;
    private readonly SettingsStore settingsStore;
    private readonly SingleInstance instance;
    private readonly Dispatcher dispatcher;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim settingsWriter = new(1, 1);
    private readonly ManualRefreshGate manualRefresh=new();
    private readonly ExitCoordinator exit=new();
    private readonly ITimer minuteTimer;
    private readonly NativeHostGroup host;
    private readonly TrayService tray;
    private readonly AppCommandRouter router;
    private readonly string dataRoot;
    private readonly DiagnosticJournal diagnostics;
    private TimetableState? diagnosticState;
    private readonly AuthWorkerSupervisor authWorker;
    private readonly string workerExecutable=ResolveWorkerExecutable();
    private static string ResolveWorkerExecutable()
    {
        var development=Path.Combine(AppContext.BaseDirectory,"AuthWorker","DlutTimetable.AuthWorker.exe");
        // A self-contained release shares one runtime, with two independent apphosts.
        return File.Exists(development)?development:Path.Combine(AppContext.BaseDirectory,"DlutTimetable.AuthWorker.exe");
    }
    private AccountGeneration accountGeneration=new(0);
    private Task authOperation=Task.CompletedTask;
    private CancellationTokenSource? activeAuthCancellation;
    private Task clearSessionOperation=Task.CompletedTask;
    private bool clearingSession;
    private bool cacheReadable=true;
    public bool CanImportData=>!exiting && cacheReadable;
    public bool CanRetryCache=>!exiting && !cacheReadable && operation.IsCompleted;
    private bool committingSchoolImport;
    private bool cancellingSchool;
    public bool CanCancelSchool=>!exiting && activeAuthCancellation is not null && !committingSchoolImport && !cancellingSchool;
    public async Task CancelSchoolAsync()
    {
        if(!CanCancelSchool || activeAuthCancellation is not {} cancellation)return;
        var pending=authOperation;
        cancellingSchool=true;LoginStateChanged?.Invoke();
        try
        {
            diagnostics.Record(DiagnosticCode.SchoolCancelRequested);
            SetStatus("正在取消学校读取；现有课表保留。");
            await cancellation.CancelAsync();await pending.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch(TimeoutException){if(!exiting)SetStatus("已请求取消，仍在等待学校窗口结束；请稍后重试，当前不启动新窗口。");}
        catch(OperationCanceledException) when(exiting){}
        finally{cancellingSchool=false;LoginStateChanged?.Invoke();}
    }
    private readonly DispatcherTimer schoolRetryTimer=new();
    private bool calendarSaving;
    private Task calendarOperation=Task.CompletedTask;
    private CalendarEditorWindow? calendarWindow;
    public bool CanLogin=>!exiting && cacheReadable && !cancellingSchool && !calendarSaving && operation.IsCompleted && !clearingSession && !(settings.SchoolRetryAfterUtc>DateTimeOffset.UtcNow) && validationLog is null && !manualRefresh.Busy && !authWorker.Busy && File.Exists(workerExecutable);
    public bool CanEditCalendar=>dataset is not null && canSave && !exiting && !calendarSaving && !clearingSession && !manualRefresh.Busy && operation.IsCompleted;
    private CalendarAdjustment? ActiveCalendar=>dataset is not null && settings.CalendarAdjustment is {} value && value.AppliesTo(dataset.Value) && value.Errors(dataset.Value).Count==0?value:null;
    private void UpdateTimetable(int? week=null)=>host.View.SetDataset(dataset,week,ActiveCalendar);
    public void OpenCalendar()
    {
        if(calendarWindow is not null){calendarWindow.Activate();return;}
        if(!CanEditCalendar)return;
        var snapshot=dataset!;
        calendarWindow=new(snapshot,ActiveCalendar,adjustment=>
        {var task=SaveCalendarAsync(snapshot,adjustment);calendarOperation=task;return task;});
        SettingsChrome.Apply(calendarWindow);
        var window=calendarWindow;details.Add(window);
        window.Closed+=(_,_)=>{details.Remove(window);calendarWindow=null;UpdatePopupState();};
        UpdatePopupState();window.Show();NativeHostGroup.ApplyPopupLevel(window,Scene.Current.EffectiveLevel);
    }
    private async Task<string?> SaveCalendarAsync(ValidatedDataset snapshot,CalendarAdjustment? adjustment)
    {
        if(!CanEditCalendar || !ReferenceEquals(snapshot,dataset))return "课表已更新或正忙，请关闭后重新打开校历设置。";
        adjustment?.ValidateFor(snapshot);
        calendarSaving=true;LoginStateChanged?.Invoke();
        try
        {
            await settingsWriter.WaitAsync(lifetime.Token);
            try
            {
                await settingsStore.SaveAsync(settings with{CalendarAdjustment=adjustment},lifetime.Token);
                settings=settings with{CalendarAdjustment=adjustment};
            }
            finally {settingsWriter.Release();}
            if(!exiting){UpdateTimetable(host.View.Week);SetStatus(adjustment is null?"已恢复课表原始校历和节次。":"已保存本机校历和节次；学校原始数据保持不变。");}
            return null;
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException or OperationCanceledException)
        {var id=diagnostics.Record(DiagnosticCode.CalendarSaveFailed);return $"校历设置未能保存，原设置保持不变。CalendarSaveFailed · {id:N}";}
        finally {calendarSaving=false;LoginStateChanged?.Invoke();}
    }
    public string DataSummary=>"本科 · 开发区校区\n"+(!cacheReadable?"缓存无法读取，已进入只读恢复。原文件保留；可导出诊断，修复文件后重新读取。":dataset is null?"尚无缓存课表，可从学校刷新或导入课表文件。":
        dataset.Value.Synthetic?"正在预览虚构示例。":$"已保存课表：{dataset.Value.GetCourses().Count()} 门已排课课程，{dataset.Value.Meetings.Sum(m=>m.Weeks.Length)} 次课程。可离线查看。")
        +(ActiveCalendar is not null?"\n本机校历与节次设置已应用。":settings.CalendarAdjustment is {} pending && dataset is not null && pending.AppliesTo(dataset.Value)?"\n本机校历与当前课程不符，暂用课表原值，请重新核对。":"")
        +(settings.SchoolRetryAfterUtc>DateTimeOffset.UtcNow?$"\n学校暂时限制刷新，请在 {settings.SchoolRetryAfterUtc.Value.ToLocalTime():MM-dd HH:mm:ss} 后重试。":"");
    private void ScheduleSchoolRetryExpiry()
    {
        schoolRetryTimer.Stop();
        var now=DateTimeOffset.UtcNow;
        if(settings.SchoolRetryAfterUtc is not {} until || until<=now)return;
        var remaining=until-now;
        schoolRetryTimer.Interval=remaining<TimeSpan.FromDays(1)?remaining:TimeSpan.FromDays(1);
        schoolRetryTimer.Start();
    }
    public bool CanClearSession=>!exiting && !clearingSession && validationLog is null;
    public event Action? LoginStateChanged;
    public Task OpenLoginAsync(CommandOrigin origin=CommandOrigin.Settings)
    {
        if(!CanLogin)return Task.CompletedTask;
        authOperation=RunManualRefreshAsync(origin);return authOperation;
    }
    private async Task RunManualRefreshAsync(CommandOrigin origin)
    {
        try {await manualRefresh.RunAsync(origin,_=>LoginCoreAsync(),lifetime.Token);}
        finally {LoginStateChanged?.Invoke();}
    }
    private async Task LoginCoreAsync()
    {
        var generation=accountGeneration;
        var correlation=diagnostics.Record(DiagnosticCode.SchoolRefreshStarted);
        using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        activeAuthCancellation=cancellation;
        try
        {
            SetStatus("已打开学校课表页面，请完成登录或二次验证。拉取完成前保留当前课表。");
            var run=authWorker.RunAsync(WorkerTaskKind.InteractiveLogin,generation,TimeSpan.FromMinutes(15),cancellation.Token,correlation);
            LoginStateChanged?.Invoke();
            var result=await run;
            // Worker shutdown may yield after its response was validated. A
            // cleared account or exit must also reject late retry metadata.
            if(exiting || generation!=accountGeneration)return;
            if(result.Response?.Diagnostics?.RetryAfterUtc is {} until && until>DateTimeOffset.UtcNow
                && (settings.SchoolRetryAfterUtc is null || until>settings.SchoolRetryAfterUtc))
            {
                settings=settings with{SchoolRetryAfterUtc=until};
                pendingSave=SaveSettingsAsync();await pendingSave;
                if(!exiting)ScheduleSchoolRetryExpiry();
            }
            if(exiting || generation!=accountGeneration)return;
            if(result.Response?.Status==WorkerStatus.Completed && result.ExitCode!=0)
            {ReportFailure(DiagnosticCode.SchoolRefreshFailed,"学校窗口异常结束，当前课表保持不变。",correlation);return;}
            if(!result.Cancelled && !result.TimedOut && result.Response is {Status:WorkerStatus.Completed,DatasetJson:{} json})
            {
                var imported=DatasetJson.Parse(System.Text.Encoding.UTF8.GetBytes(json));
                if(imported.Value.Synthetic || imported.Value.Source!="dlut-academic")throw new InvalidDataException("Unexpected school dataset.");
                cancellation.Token.ThrowIfCancellationRequested();
                committingSchoolImport=true;LoginStateChanged?.Invoke();
                await store.ReplaceAsync(imported,cancellation.Token);
                if(exiting || generation!=accountGeneration)return;
                dataset=imported;UpdateTimetable();
                diagnostics.Record(DiagnosticCode.SchoolRefreshSucceeded,correlation,count:imported.Value.Meetings.Length);
                SetStatus($"学校课表已更新：{imported.Value.GetCourses().Count()} 门已排课课程，{imported.Value.Meetings.Length} 组安排。离线可查看。");
                return;
            }
            if(result.Response?.Diagnostics?.Observation is {} observation && !result.Cancelled && !result.TimedOut)
            {
                var destination=Path.Combine(dataRoot,"auth-observation.json");
                await File.WriteAllBytesAsync(destination,JsonSerializer.SerializeToUtf8Bytes(observation,new JsonSerializerOptions{WriteIndented=true}));
                SetStatus("已保存脱敏结构和周次/节次样本，未保存姓名、课程名或凭据；尚未同步或覆盖课表。");return;
            }
            diagnostics.Record(result.TimedOut?DiagnosticCode.SchoolRefreshTimedOut:result.Cancelled || result.Response?.Status==WorkerStatus.Cancelled?DiagnosticCode.SchoolRefreshCancelled:DiagnosticCode.SchoolRefreshFailed,correlation);
            SetStatus(settings.SchoolRetryAfterUtc>DateTimeOffset.UtcNow?$"学校暂时限制刷新，请在 {settings.SchoolRetryAfterUtc.Value.ToLocalTime():MM-dd HH:mm:ss} 后重试。当前课表保留。":result.TimedOut?"学校登录超时，当前课表保持不变。":result.Cancelled || result.Response?.Status==WorkerStatus.Cancelled
                ?"已取消学校登录，当前课表保持不变。":result.Response?.Status==WorkerStatus.UnsupportedSchema
                ?"无法识别当前课表结构，请进入本人课表后重试；已有课程保留。":"学校登录未完成。请检查网络或 WebView2 Runtime；当前课表保持不变。");
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException or JsonException or DatasetValidationException or Microsoft.Data.Sqlite.SqliteException)
        {
            var cancelled=ex is OperationCanceledException && cancellation.IsCancellationRequested;
            var code=cancelled?DiagnosticCode.SchoolRefreshCancelled:DiagnosticCode.SchoolRefreshFailed;
            if(!exiting && generation==accountGeneration)ReportFailure(code,cancelled?"已取消学校读取，当前课表保持不变。":"学校登录进程未完成，当前课表保持不变。",correlation);
            else diagnostics.Record(code,correlation);
        }
        finally {committingSchoolImport=false;activeAuthCancellation=null;LoginStateChanged?.Invoke();}
    }
    public Task ClearSessionAsync()
    {
        if(!CanClearSession)return Task.CompletedTask;
        clearSessionOperation=ClearCoreAsync();return clearSessionOperation;
    }
    private async Task ClearCoreAsync()
    {
        clearingSession=true;accountGeneration=new(accountGeneration.Value+1);LoginStateChanged?.Invoke();
        try
        {
            if(activeAuthCancellation is {} cancellation)await cancellation.CancelAsync();
            await authOperation;
            var outcome=await new AuthProfileCleaner(AuthProfileLocation.ApplicationDirectoryForScope(settings.AuthenticationScope(dataRoot))).ClearAsync(lifetime.Token);
            if(exiting)return;
            if(outcome is not (ProfileClearOutcome.Cleared or ProfileClearOutcome.AlreadyEmpty))diagnostics.Record(DiagnosticCode.SessionClearFailed);
            SetStatus(outcome switch
            {
                ProfileClearOutcome.Cleared or ProfileClearOutcome.AlreadyEmpty=>"已删除学校登录会话；缓存课表、私人备注、布局和自启设置已保留。下次访问学校需要重新登录。",
                ProfileClearOutcome.Busy=>"另一个学校窗口仍在使用会话，请关闭后重试。",
                ProfileClearOutcome.UnsafeDirectory=>"会话目录包含异常链接或文件，未执行清理。",
                _=>"会话未能完整清除，部分文件可能已删除，请关闭学校窗口后重试。"
            });
        }
        catch(OperationCanceledException) when(lifetime.IsCancellationRequested){}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {if(!exiting)ReportFailure(DiagnosticCode.SessionClearFailed,"学校登录会话未能完整清除，请稍后重试。");}
        finally {clearingSession=false;LoginStateChanged?.Invoke();}
    }
    private readonly StartupManager? startup;
    private Task startupOperation=Task.CompletedTask;
    public StartupState Startup { get; private set; }=new(false,null,false,false,"演示或独立数据目录不修改登录启动项。");
    public bool CanSetStartup=>startup is not null && canSave && !exiting;
    public event Action<StartupState>? StartupChanged;
    private void UpdateStartup(StartupState state)
    { if(state.OutOfSync && !Startup.OutOfSync)diagnostics.Record(DiagnosticCode.StartupOutOfSync);Startup=state;tray.UpdateStartup(state,CanSetStartup);StartupChanged?.Invoke(state); }
    public Task SetStartupAsync(bool desired)
    {
        if(!CanSetStartup || Startup.Busy)return Task.CompletedTask;
        startupOperation=startup!.ApplyAsync(desired);return startupOperation;
    }
    public Task RefreshStartupAsync()
    {
        if(!CanSetStartup || Startup.Busy)return Task.CompletedTask;
        startupOperation=startup!.InitializeAsync(false);return startupOperation;
    }
    private async Task PersistStartupAsync(bool desired)
    {
        await settingsWriter.WaitAsync();
        var beforeInitialized=settings.StartupInitialized;var beforeDesired=settings.StartupDesired;
        try
        {
            settings=settings with {StartupInitialized=true,StartupDesired=desired};
            await settingsStore.SaveAsync(settings);
        }
        catch
        { settings=settings with {StartupInitialized=beforeInitialized,StartupDesired=beforeDesired};throw; }
        finally {settingsWriter.Release();}
    }
    private SettingsWindow? settingsWindow;
    private readonly List<Window> details = [];
    private AppSettings settings;
    private readonly bool canSave;
    private readonly string? validationLog;
    private ValidatedDataset? dataset;
    private Task operation = Task.CompletedTask, pendingSave = Task.CompletedTask;
    private bool exiting;
    public WindowStateController Scene { get; }
    public string Status { get; private set; } = "离线模式。可导入课表，查看当前缓存。";
    public MaterialParameters Material => host.Material.Parameters;
    public string MaterialStatus => host.Material.Status;
    public event Action<string>? MaterialStatusChanged;
    public bool ReducedMotion => settings.ReducedMotion;
    public void SetReducedMotion(bool value)
    {
        Scene.StabilizeBallMotion();host.ReduceMotion=value;host.Show();
        settings=settings with {ReducedMotion=value};pendingSave=SaveSettingsAsync();
    }
    public void SetMaterial(MaterialParameters value)
    {
        host.ConfigureMaterial(value);
        settings = settings with { Material = value.Kind.ToString(), MaterialDark = value.Dark, MaterialDisplacement = value.Displacement,
            BackgroundTransparency=value.BackgroundTransparency,ForegroundTransparency=value.ForegroundTransparency,
            MaterialDispersion=value.Dispersion,MaterialLightAngle=value.LightAngle };
        pendingSave = SaveSettingsAsync();
    }
    public void RecoverDesktop()
    {
        if(exiting)return;
        var id=diagnostics.Record(DiagnosticCode.ShellRecoveryRequested);
        var desktopReady=host.TryRecoverDesktop();var trayReady=tray.RestoreRegistration();
        if(desktopReady && trayReady){diagnostics.Record(DiagnosticCode.ShellRecoverySucceeded,id);SetStatus("已修复桌面显示；课表数据保留。");}
        else ReportFailure(DiagnosticCode.RecoveryFailed,"桌面显示暂未恢复，请确认 Windows 桌面正常后再试。",id);
    }
    public void RetryMaterial()
    {
        if(exiting)return;
        var id=diagnostics.Record(DiagnosticCode.MaterialRecoveryRequested);
        try{host.RetryMaterial();diagnostics.Record(DiagnosticCode.MaterialRecoveryFinished,id);SetStatus("已重新尝试材质："+host.Material.Status+"。");}
        catch(Exception ex) when(ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {ReportFailure(DiagnosticCode.RecoveryFailed,"材质暂不可用，可切换轻透磨砂或实色后重试。",id);}
    }

    private DesktopSession(SqliteStore store, SettingsStore settingsStore, SingleInstance instance, SettingsLoad load, string? validationLog,bool startupAllowed,string root)
    {
        this.store = store; this.settingsStore = settingsStore; this.instance = instance;
        this.validationLog = validationLog;
        dataRoot=root;
        diagnostics=new(Path.Combine(root,"logs"));diagnostics.Record(DiagnosticCode.SessionStarted);
        dispatcher = Dispatcher.CurrentDispatcher;
        settings = load.Settings ?? new(); canSave = load.Status is SettingsLoadStatus.Loaded or SettingsLoadStatus.Missing;
        if(!canSave)diagnostics.Record(DiagnosticCode.SettingsUnreadable);
        authWorker=new(workerExecutable,()=>accountGeneration,settings.AuthenticationScope(root));
        schoolRetryTimer.Tick+=(_,_)=>{ScheduleSchoolRetryExpiry();LoginStateChanged?.Invoke();};
        ScheduleSchoolRetryExpiry();
        if (!canSave) Status = "配置损坏或版本不支持，原文件已保留；本次布局变更不覆盖原文件。";
        var display = DisplayLayout.Select(settings.DisplayId);
        Scene = new(new(settings.State, EdgePhase.Collapsed,
            new TimetableGeometry(new(settings.X, settings.Y), settings.Scale).Fit(display.WorkArea),
            new(settings.BallX, settings.BallY), settings.EdgeDirection, settings.EdgeOffset, settings.EdgeLevel, settings.BallDiameter)
            { StripThickness = settings.StripThickness, StripLength = settings.StripLength },
            TimeProvider.System, action => dispatcher.BeginInvoke(action));
        Scene.HoverTriggered += (opening, elapsed) => WriteValidation("HOVER " + (opening ? "Open " : "Close ")
            + elapsed.TotalMilliseconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
        router = new(Scene, action => dispatcher.BeginInvoke(action), ExternalCommand);
        host = new(Scene, kind => Route(kind, CommandOrigin.Toolbar), settings.DisplayId, settings.BallDisplayId);
        host.ReduceMotion=settings.ReducedMotion;
        host.MotionDiagnostic+=WriteValidation;
        host.Material.Configure(new(Enum.Parse<MaterialKind>(settings.Material),settings.MaterialDark,Displacement:settings.MaterialDisplacement,
            BackgroundTransparency:settings.BackgroundTransparency,ForegroundTransparency:settings.ForegroundTransparency,
            Dispersion:settings.MaterialDispersion,LightAngle:settings.MaterialLightAngle));
        host.Material.StatusChanged += value => { MaterialStatusChanged?.Invoke(value); WriteValidation("MATERIAL " + value); };
        host.Material.ValidatedFrame += (kind,bytes,ms,displacement) =>
        {
            diagnostics.Record(DiagnosticCode.MaterialSample,elapsedMilliseconds:ms,bytes:bytes,persist:false);
            WriteValidation(FormattableString.Invariant($"MATERIAL_FRAME {kind} cache={bytes} renderMs={ms:F3} maxDisplacement={displacement:F3}"));
        };
        host.Failed += ex => ReportFailure(DiagnosticCode.ShellRecoveryFailed,"桌面布局恢复失败，可从设置恢复可见位置。");
        host.View.CourseInvoked += OpenDetails;
        host.View.ConflictsInvoked += courses =>
        {
            if (exiting || !host.View.Interactive) return;
            var window = new Window { Title = "课程冲突", Width = 420, Height = 420, FontFamily = new("Microsoft YaHei UI") };
            var list = new StackPanel { Margin = new(20) };
            window.Content = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            foreach (var course in courses)
            {
                var item = new Button { Content = course.Meeting.Name + " · " + course.Meeting.Location,
                    Padding = new(6), Margin = new(0, 4, 0, 4) };
                item.Click += (_, _) => OpenDetails(course); list.Children.Add(item);
            }
            details.Add(window); window.Closed += (_, _) => { details.Remove(window); UpdatePopupState(); };
            UpdatePopupState(); window.Show();NativeHostGroup.ApplyPopupLevel(window,Scene.Current.EffectiveLevel);
        };
        host.View.WeekChanged += week => { settings = settings with { LastSelectedWeek = week }; pendingSave = SaveSettingsAsync(); };
        tray = new(kind => Route(kind, CommandOrigin.Tray));
        if(startupAllowed && canSave)
        {
            try
            {
                var command=StartupRegistration.CommandFor(Path.Combine(AppContext.BaseDirectory,"DlutTimetable.Desktop.exe"));
                startup=new(new StartupRegistration(),command,settings.StartupInitialized,settings.StartupDesired,PersistStartupAsync);
                startup.Changed+=UpdateStartup;
            }
            catch(Exception ex) when(ex is ArgumentException or IOException or UnauthorizedAccessException)
            { diagnostics.Record(DiagnosticCode.StartupConfigFailed);Startup=new(settings.StartupDesired,null,true,false,"无法配置登录启动，请检查程序路径与当前用户权限。StartupConfigFailed"); }
        }
        else if(!canSave)Startup=new(settings.StartupDesired,null,true,false,"配置损坏或版本不支持，未修改启动项。");
        tray.UpdateStartup(Startup,CanSetStartup);
        Scene.Changed += OnScene;
        minuteTimer = TimeProvider.System.CreateTimer(_ => dispatcher.BeginInvoke(() =>
        { if (!exiting) host.View.RefreshClock(); }), null,
            TimeSpan.FromTicks(TimeSpan.TicksPerMinute - DateTimeOffset.UtcNow.UtcTicks % TimeSpan.TicksPerMinute), TimeSpan.FromMinutes(1));
        instance.Listen(() => Route(AppCommandKind.RestoreTimetable, CommandOrigin.System),
            () => dispatcher.BeginInvoke(() => _ = ExitAsync()));
    }
    public static async Task<DesktopSession> CreateAsync(SqliteStore store, string root, SingleInstance instance, bool demo,bool startupAllowed=false,bool background=false)
    {
        var settingsStore = new SettingsStore(Path.Combine(root, "settings.json"));
        var load = await settingsStore.LoadAsync();
        var session = new DesktopSession(store, settingsStore, instance, load, demo ? Path.Combine(root, "desktop-validation.log") : null,startupAllowed,root);
        if(session.startup is not null)await session.startup.InitializeAsync(!background);
        try { session.dataset = await store.ReadAsync(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or JsonException or DatasetValidationException)
        { session.cacheReadable=false;session.ReportFailure(DiagnosticCode.DatabaseReadFailed,"缓存无法读取，已进入只读恢复；原数据库保留，可导出诊断或修复文件后重新读取。"); }
        session.UpdateTimetable(session.settings.LastSelectedWeek);
        session.host.Show();
        if (demo) { await session.LoadDemoAsync(); _ = session.TraceRenderingAsync(); }
        else if (session.dataset is null && !background) session.OpenSettings();
        return session;
    }
    private async Task TraceRenderingAsync()
    {
        // Finite diagnostics for the explicit synthetic demo only, not a resident sampling loop.
        try
        {
            for (var i=0;i<3;i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(3),lifetime.Token);
                if(i==0 && validationLog is not null)
                {
                    try { if(host.SaveOwnFrame(Path.Combine(Path.GetDirectoryName(validationLog)!,"desktop-own-frame.png")))WriteValidation("OWN_FRAME saved"); }
                    catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidOperationException){WriteValidation("OWN_FRAME failed "+ex.GetType().Name);}
                }
                WriteValidation($"VIEW_RENDER count={host.View.RenderCount}");
            }
        }
        catch (OperationCanceledException) { }
    }
    public void Route(AppCommandKind kind, CommandOrigin origin = CommandOrigin.Settings) => router.Route(kind, origin);
    private void OnScene(SceneSnapshot state, bool stable)
    {
        if (!exiting)
        {
            if (settingsWindow is not null) NativeHostGroup.ApplyPopupLevel(settingsWindow, state.EffectiveLevel);
            foreach (var detail in details) NativeHostGroup.ApplyPopupLevel(detail, state.EffectiveLevel);
        }
        if (!stable || exiting) return;
        if(diagnosticState!=state.State){diagnosticState=state.State;diagnostics.Record(DiagnosticCode.SceneChanged,state:state.State);}
        settings = settings with
        {
            State = state.State, X = state.Expanded.TopLeft.X, Y = state.Expanded.TopLeft.Y, Scale = state.Expanded.Scale,
            BallX = state.BallCenter.X, BallY = state.BallCenter.Y, EdgeDirection = state.Edge, EdgeOffset = state.EdgeOffset,
            EdgeLevel = state.EdgeLevel, StripThickness = state.StripThickness, StripLength = state.StripLength,
            DisplayId = host.DisplayId, BallDisplayId = host.BallDisplayId
        };
        pendingSave = SaveSettingsAsync();
    }
    private async Task SaveSettingsAsync()
    {
        if (!canSave) return;
        await settingsWriter.WaitAsync();
        try
        {
            var snapshot = settings;
            await Task.Run(() => settingsStore.SaveAsync(snapshot));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { ReportFailure(DiagnosticCode.SettingsWriteFailed,"设置保存失败，请检查数据目录是否可写。"); }
        finally { settingsWriter.Release(); }
    }
    private void ExternalCommand(AppCommand command)
    {
        switch (command.Kind)
        {
            case AppCommandKind.OpenSettings: OpenSettings(); break;
            case AppCommandKind.SetStartup: _=SetStartupAsync(!Startup.Desired);break;
            case AppCommandKind.LoginAndRetry:
            case AppCommandKind.ManualRefresh:
                if(CanLogin)_=OpenLoginAsync(command.Origin);
                else if(command.Origin!=CommandOrigin.System){SetStatus(settings.SchoolRetryAfterUtc>DateTimeOffset.UtcNow?DataSummary:"刷新窗口已打开，或当前模式无法打开学校页面；现有课表保持不变。");OpenSettings();}
                break;
            case AppCommandKind.ExitApplication: _ = ExitAsync(); break;
        }
    }
    private void UpdatePopupState() => Scene.SetSettingsOpen(settingsWindow is not null || details.Count > 0, host.PointerInside());
    private void OpenSettings()
    {
        if (exiting) return;
        if (settingsWindow is not null) { settingsWindow.Activate(); return; }
        settingsWindow = new(this);
        settingsWindow.Closed += (_, _) => { settingsWindow = null; UpdatePopupState(); };
        UpdatePopupState(); settingsWindow.Show(); NativeHostGroup.ApplyPopupLevel(settingsWindow, Scene.Current.EffectiveLevel);
    }
    private void OpenDetails(Occurrence occurrence)
    {
        if (exiting || !host.View.Interactive) return;
        var window = new CourseDetailsWindow(occurrence);
        details.Add(window); window.Closed += (_, _) => { details.Remove(window); UpdatePopupState(); };
        UpdatePopupState(); window.Show(); NativeHostGroup.ApplyPopupLevel(window, Scene.Current.EffectiveLevel);
    }
    private void SetStatus(string value)
    {
        Status = value; settingsWindow?.SetStatus(value);settingsWindow?.RefreshDataSummary();
        WriteValidation(value);
    }
    private void ReportFailure(DiagnosticCode code,string message,Guid? correlation=null)
    {
        var id=diagnostics.Record(code,correlation);
        SetStatus($"{message}（{code} · {id:N}）");
    }
    private void WriteValidation(string value)
    {
        if (validationLog is not null)
        {
            try
            {
                if (File.Exists(validationLog) && new FileInfo(validationLog).Length >= 64 * 1024)
                {
                    if(File.Exists(validationLog+".1"))File.Move(validationLog+".1",validationLog+".2",true);
                    File.Move(validationLog,validationLog+".1",true);
                }
                File.AppendAllText(validationLog, DateTimeOffset.UtcNow.ToString("O") + " " + value + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
    private Task RunOperationAsync(Func<CancellationToken, Task> action)
    {
        if (exiting || !operation.IsCompleted || calendarSaving) return Task.CompletedTask;
        operation = Run(); LoginStateChanged?.Invoke();return operation;
        async Task Run()
        {
            try { await action(lifetime.Token); }
            catch (OperationCanceledException) when (exiting) { }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException
                or DatasetValidationException or Microsoft.Data.Sqlite.SqliteException)
            { ReportFailure(DiagnosticCode.DataOperationFailed,"操作失败，已保存课表未被清空。请检查文件格式、权限或可用空间。"); }
            finally {_ = dispatcher.BeginInvoke(()=>LoginStateChanged?.Invoke());}
        }
    }
    public Task ImportAsync(string path) => RunOperationAsync(async ct =>
    {
        if(!cacheReadable){ReportFailure(DiagnosticCode.DatabaseReadFailed,"缓存仍处于只读恢复，请先修复文件并重新读取。");return;}
        var parsed = await DatasetJson.ReadFileAsync(path, ct);
        var imported = ValidatedDataset.Create(parsed.Value with { Source = "local-import" });
        accountGeneration=new(accountGeneration.Value+1);
        if(activeAuthCancellation is {} refreshing)await refreshing.CancelAsync();
        await authOperation;
        await store.ReplaceAsync(imported, ct); ct.ThrowIfCancellationRequested();
        dataset = imported; UpdateTimetable(); SetStatus("已导入当前课表；未联网，旧集合已整批替换。");
    });
    public Task RetryCacheAsync()
    {
        if(!CanRetryCache)return Task.CompletedTask;
        return RunOperationAsync(async ct=>
        {
            var restored=await store.ReadAsync(ct:ct);ct.ThrowIfCancellationRequested();
            dataset=restored;cacheReadable=true;UpdateTimetable();
            SetStatus(restored is null?"缓存已恢复可读，当前没有课程，可手动导入。":"缓存已恢复读取，离线课程已显示。");
        });
    }
    public Task LoadDemoAsync() => RunOperationAsync(async ct =>
    {
        var demo = await DatasetJson.ReadFileAsync(Path.Combine(AppContext.BaseDirectory, "fixtures", "synthetic-term.json"), ct);
        ct.ThrowIfCancellationRequested(); dataset = demo; UpdateTimetable();
        SetStatus("正在预览虚构示例，未覆盖已保存课表；示例日期和节次不是学校已确认参数。");
    });
    public Task ExportAsync(string path) => RunOperationAsync(async ct =>
    {
        if (dataset is null) { SetStatus("尚无可导出的课表。"); return; }
        await AtomicExport.WriteAsync(path,DatasetJson.Serialize(dataset),[dataRoot,AuthProfileLocation.ApplicationDirectory],ct);
        SetStatus("个人课程数据已导出，请妥善保管文件。");
    });
    public Task ExportDiagnosticsAsync(string path)=>RunOperationAsync(async ct=>
    {
        var snapshot=new DiagnosticUiState(Scene.Current.State,Scene.Current.Expanded.Scale,settings.ReducedMotion,
            Material.Kind!=MaterialKind.Solid,Material.Dark,(int)Math.Round(Material.BackgroundTransparency*100),
            (int)Math.Round(Material.ForegroundTransparency*100),authWorker.Busy,Startup.Desired,Startup.RegistrationObserved,Startup.OutOfSync,cacheReadable);
        await AtomicExport.WriteAsync(path,DiagnosticExport.Create(dataset,snapshot,diagnostics.Snapshot()),[dataRoot,AuthProfileLocation.ApplicationDirectory],ct);
        SetStatus("脱敏诊断已导出；不包含课程内容、私人备注、账号、路径或浏览器凭据。");
    });
    public Task ExportCalendarAsync(string path)=>RunOperationAsync(async ct=>
    {
        if(dataset is null){SetStatus("尚无可导出的课表。");return;}
        var snapshot=dataset;var calendar=ActiveCalendar;
        byte[] bytes;
        // These in-memory producers emit fixed validation text only. File errors
        // remain outside this boundary and use the generic, path-free handler.
        try{bytes=await Task.Run(()=>ICalendarExport.Create(snapshot,calendar,cancellationToken:ct),ct);}
        catch(InvalidDataException invalid){SetStatus(invalid.Message);return;}
        await AtomicExport.WriteAsync(path,bytes,[dataRoot,AuthProfileLocation.ApplicationDirectory],ct);
        SetStatus("ICS 日历已导出，包含个人课程、地点和教师；这是当前课表快照。");
    });
    public Task ExportCsesAsync(string path,bool currentWeek)=>RunOperationAsync(async ct=>
    {
        if(dataset is null){SetStatus("尚无可导出的课表。");return;}
        var snapshot=dataset;var calendar=ActiveCalendar;int? week=currentWeek?host.View.Week:null;
        byte[] bytes;
        try{bytes=await Task.Run(()=>CsesExport.Create(snapshot,calendar,week),ct);}
        catch(InvalidDataException invalid){SetStatus(invalid.Message);return;}
        await AtomicExport.WriteAsync(path,bytes,[dataRoot,AuthProfileLocation.ApplicationDirectory],ct);
        SetStatus(currentWeek?"已导出当前周 CSES 模板；不包含原始日期和教学周范围。":"已导出 CSES 周期模板；请在接收软件中核对校历日期。");
    });
    private Task ExitAsync()=>exit.RunAsync(ExitCoreAsync);
    private bool faultExit;
    public Task ExitAfterFaultAsync(DiagnosticCode code)
    {
        faultExit=true;diagnostics.Record(code);return ExitAsync();
    }
    private async Task ExitCoreAsync()
    {
        if (exiting) return;
        // Commit the last coalesced slider value before awaiting writes and disposing storage.
        settingsWindow?.FlushMaterialPreview();
        exiting = true;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            diagnostics.Record(DiagnosticCode.SessionExiting);Scene.BeginExit();
            schoolRetryTimer.Stop();
            accountGeneration=new(accountGeneration.Value+1);
            // A fault in one participant must not skip shutdown of the others.
            // Start every cleanup action even if the shared observation deadline expired.
            async Task Drain(DiagnosticCode code,Func<Task> action)
            {
                try {await action().WaitAsync(deadline.Token);}
                catch(Exception)
                {ReportFailure(code,"退出收尾未完成，将继续清理其它资源。");}
            }
            await Drain(DiagnosticCode.ExitCancellationFailed,()=>lifetime.CancelAsync());
            await Drain(DiagnosticCode.ExitClockFailed,()=>minuteTimer.DisposeAsync().AsTask());
            await Drain(DiagnosticCode.ExitWorkerFailed,()=>authWorker.DisposeAsync().AsTask());
            await Drain(DiagnosticCode.ExitSchoolReadFailed,()=>authOperation);
            await Drain(DiagnosticCode.ExitSessionClearFailed,()=>clearSessionOperation);
            await Drain(DiagnosticCode.ExitDataFailed,()=>operation);
            await Drain(DiagnosticCode.ExitCalendarFailed,()=>calendarOperation);
            await Drain(DiagnosticCode.ExitStartupFailed,()=>startupOperation);
            await Drain(DiagnosticCode.ExitSettingsFailed,()=>pendingSave);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { ReportFailure(DiagnosticCode.ExitDeadlineFailed,"退出收尾超时，将继续释放资源。"); }
        finally
        {
            void Release(Action dispose)
            { try { dispose(); } catch (Exception) { ReportFailure(DiagnosticCode.ExitResourceFailed,"部分资源未能正常释放。"); } }
            Release(() => settingsWindow?.Close()); foreach (var detail in details.ToArray()) Release(detail.Close);
            Release(tray.Dispose); Release(host.Dispose); Release(Scene.Dispose);
            try { await instance.DisposeAsync(); }
            catch(Exception){ReportFailure(DiagnosticCode.ExitInstanceFailed,"退出实例释放未完成。");}
            finally
            {
                Release(settingsStore.Dispose); Release(settingsWriter.Dispose); Release(lifetime.Dispose);
                try{await diagnostics.DisposeAsync().AsTask().WaitAsync(deadline.Token);}catch(Exception ex) when(ex is OperationCanceledException or IOException){}
                System.Windows.Application.Current.Shutdown(faultExit?1:0);
            }
        }
    }
}
