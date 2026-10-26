using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DlutTimetable.Contracts;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using DlutTimetable.Infrastructure;
using DlutTimetable.Infrastructure.Academic;

namespace DlutTimetable.AuthWorker;

internal sealed class LoginWindow : Window
{
    private readonly WorkerRequest request;
    private readonly SchoolAccessPolicy access;
    private bool viaWebVpn;
    private readonly WebView2 browser=new();
    private readonly StackPanel sources=new(){Orientation=Orientation.Horizontal,Margin=new(12,8,12,0)};
    private readonly TextBlock status=new(){Text="正在启动学校登录页面…",TextWrapping=TextWrapping.Wrap,Margin=new(12)};
    private readonly TaskCompletionSource<WorkerStatus> finished=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool browserExitEventObserved;
    private readonly BrowserProcessTracker processTracker=new(TimeProvider.System);
    private CoreWebView2Environment? environment;
    private Mutex? profileLock;
    private bool ownsProfile,closing;
    private bool academicSignInAttempted;
    private readonly Action<CoreWebView2>? configureForFixture;
    private readonly TimeSpan sourceReadTimeout;
    private readonly DispatcherTimer feedbackTimer=new(){Interval=TimeSpan.FromSeconds(1)};
    private DateTimeOffset? retryAfterUtc,readingDeadline;
    private string? importFailure;
    private bool automaticImportAttempted;
    private int browserPid;
    private ResponseObserver? responseObserver;
    private readonly AcademicBrowserImport schoolImport=new();
    private readonly TextBox studentNumber=new(){Width=150,Margin=new(6,0,12,0),VerticalContentAlignment=VerticalAlignment.Center};
    public string? DatasetJson {get;private set;}
    private readonly Button observe=new(){Content="重新读取",Padding=new(12,7,12,7),Margin=new(0,0,10,0),IsEnabled=false};
    private readonly TextBlock observationStatus=new(){Text="请登录并进入本人课表。",TextWrapping=TextWrapping.Wrap,Margin=new(12,8,12,0)};
    private readonly Button done=new(){Content="导入课表并关闭",Padding=new(12,7,12,7),IsEnabled=false};
    public LoginWindow(WorkerRequest request,Action<CoreWebView2>? configureForFixture=null,TimeSpan? sourceReadTimeout=null)
    {
        this.request=request;access=new(request.AllowedHosts);this.configureForFixture=configureForFixture;Title="大工课表 · 学校登录";Width=1100;Height=780;MinWidth=760;MinHeight=540;
        this.sourceReadTimeout=sourceReadTimeout??TimeSpan.FromSeconds(20);
        FontFamily=new("Microsoft YaHei UI");FontSize=13;
        feedbackTimer.Tick+=(_,_)=>UpdateImportUi();
        System.Windows.Automation.AutomationProperties.SetAutomationId(status,"AuthBrowserState");
        var root=new DockPanel();Content=root;
        DockPanel.SetDock(sources,Dock.Top);root.Children.Add(sources);
        foreach(var source in new[]{("本科教务课表",false),("校外 WebVPN 课表",true)})
        {
            var button=new Button{Content=source.Item1,Tag=source.Item2,Padding=new(12,7,12,7),Margin=new(0,0,10,0),IsEnabled=false};
            System.Windows.Automation.AutomationProperties.SetAutomationId(button,source.Item2?"SchoolWebVpnSource":"SchoolCampusSource");
            button.Click+=(_,_)=>
            {
                if(retryAfterUtc>DateTimeOffset.UtcNow)return;
                if(source.Item2 && !access.WebVpnGranted)return;
                if(responseObserver?.PendingCount>0){status.Text="当前读取尚未结束，请稍后切换。";return;}
                viaWebVpn=source.Item2;responseObserver?.Reset();responseObserver?.Arm();
                academicSignInAttempted=false;studentNumber.Clear();schoolImport.Reset();importFailure=null;readingDeadline=null;automaticImportAttempted=false;
                browser.CoreWebView2.Navigate(access.TransportFor(new(SchoolNavigation.AcademicCourseTable),viaWebVpn));UpdateImportUi();
            };
            sources.Children.Add(button);
        }
        var actions=new WrapPanel{Orientation=Orientation.Horizontal,Margin=new(12)};DockPanel.SetDock(actions,Dock.Bottom);root.Children.Add(actions);
        actions.Children.Add(new TextBlock{Text="学号",VerticalAlignment=VerticalAlignment.Center});
        System.Windows.Automation.AutomationProperties.SetAutomationId(studentNumber,"SchoolStudentNumber");
        actions.Children.Add(studentNumber);
        studentNumber.TextChanged+=(_,_)=>{if(schoolImport.HasResponses)importFailure=null;UpdateImportUi();};
        actions.Children.Add(observe);
        System.Windows.Automation.AutomationProperties.SetAutomationId(observe,"SchoolReadAgain");
        System.Windows.Automation.AutomationProperties.SetAutomationId(done,"SchoolImportConfirm");
        done.Click+=(_,_)=>TryImport();actions.Children.Add(done);
        var cancel=new Button{Content="取消",Padding=new(12,7,12,7),Margin=new(10,0,0,0)};
        System.Windows.Automation.AutomationProperties.SetAutomationId(cancel,"AuthCancel");
        cancel.Click+=(_,_)=>Cancel();actions.Children.Add(cancel);
        DockPanel.SetDock(observationStatus,Dock.Bottom);root.Children.Add(observationStatus);
        System.Windows.Automation.AutomationProperties.SetAutomationId(observationStatus,"AuthObservationState");
        observe.Click+=(_,_)=>
        {
            if(responseObserver is null || responseObserver.PendingCount!=0 || retryAfterUtc>DateTimeOffset.UtcNow || closing || finished.Task.IsCompleted)return;
            responseObserver.Reset();schoolImport.Reset();importFailure=null;automaticImportAttempted=false;
            readingDeadline=DateTimeOffset.UtcNow.Add(this.sourceReadTimeout);
            responseObserver.Arm();UpdateImportUi();browser.CoreWebView2.Reload();
        };
        DockPanel.SetDock(status,Dock.Top);root.Children.Add(status);root.Children.Add(browser);
        Closing+=(_,e)=>{if(!closing){e.Cancel=true;Cancel();}};
    }
    private void UpdateImportUi()
    {
        if(closing || finished.Task.IsCompleted){feedbackTimer.Stop();return;}
        var now=DateTimeOffset.UtcNow;var limited=retryAfterUtc>now;
        if(readingDeadline<=now && !schoolImport.HasResponses)
        {readingDeadline=null;importFailure="读取课表超时。请检查校园网并重新读取，已有课表保留。";}
        if(schoolImport.HasResponses)readingDeadline=null;
        var pending=responseObserver?.PendingCount??0;
        observe.IsEnabled=responseObserver is not null && !limited && pending==0;
        done.IsEnabled=schoolImport.HasResponses && !limited && pending==0 && importFailure is null;
        foreach(var button in sources.Children.OfType<Button>())button.IsEnabled=responseObserver is not null && !limited && pending==0 && (button.Tag is not true || access.WebVpnGranted);
        observationStatus.Text=limited?$"学校暂时限制访问，请在 {retryAfterUtc!.Value.ToLocalTime():MM-dd HH:mm:ss} 后重试。已有课表保留。":
            importFailure??(schoolImport.HasResponses?"课表已读取。确认学号后点击“导入课表并关闭”。":readingDeadline.HasValue?"正在读取课程与节次时间表…":"请登录并进入本人课表；已有课表会保留到导入成功。");
        if(limited || readingDeadline.HasValue)feedbackTimer.Start();else feedbackTimer.Stop();
    }
    private void ReportProblem(SchoolResponseProblem problem)
    {
        if(problem.RetryAfterUtc is {} until && (retryAfterUtc is null || until>retryAfterUtc))retryAfterUtc=until;
        schoolImport.Reset();readingDeadline=null;
        importFailure=problem.Failure switch
        {
            SchoolResponseFailure.NeedsLogin=>"学校登录已失效，请重新登录并进入本人课表，再点击“重新读取”。已有课表保留。",
            SchoolResponseFailure.RateLimited=>"学校暂时限制访问，请稍后重新读取。已有课表保留。",
            SchoolResponseFailure.TooLarge=>"学校响应超过大小限制，未导入。已有课表保留。",
            SchoolResponseFailure.Timeout=>"读取学校响应超时，请检查校园网后重试。已有课表保留。",
            SchoolResponseFailure.Unavailable=>"学校服务暂时不可用，请稍后重新读取。已有课表保留。",
            _=>"学校课表响应无法读取，请重新进入本人课表后重试。已有课表保留。"
        };
        UpdateImportUi();
    }
    private void TryImport()
    {
        if(retryAfterUtc>DateTimeOffset.UtcNow || !schoolImport.HasResponses)return;
        automaticImportAttempted=true;
        try
        {
            var value=schoolImport.Build(string.IsNullOrWhiteSpace(studentNumber.Text)?null:studentNumber.Text);
            DatasetJson=System.Text.Encoding.UTF8.GetString(DlutTimetable.Infrastructure.DatasetJson.Serialize(value));
            finished.TrySetResult(WorkerStatus.Completed);
        }
        catch(Exception ex) when(ex is InvalidDataException or AcademicReadException or AcademicMappingException or DlutTimetable.Domain.DatasetValidationException or System.Text.Json.JsonException)
        {importFailure=ex is InvalidDataException?ex.Message:"学校课表格式或课程时间无法校验，请重新读取。已有课表保留。";UpdateImportUi();WorkerTrace.Stage(WorkerTraceStage.ImportRejected);}
    }
    public void Cancel()=>finished.TrySetResult(WorkerStatus.Cancelled);
    public async Task<(WorkerStatus,WorkerDiagnostics)> RunAsync()
    {
        Show();
        WorkerTrace.Stage(WorkerTraceStage.WindowShown);
        var initialization=InitializeAsync();
        var outcome=await finished.Task;
        feedbackTimer.Stop();
        WorkerTrace.Stage(WorkerTraceStage.ClosingBrowser);
        if(responseObserver is not null)await responseObserver.DisposeAsync();
        var observation=outcome==WorkerStatus.UnsupportedSchema?responseObserver?.Report():null;
        RememberProcesses();
        // Initialization may still be awaiting WebView2 when cancellation arrives. Dispose on
        // the owning Dispatcher and allow its continuation to unwind before final teardown.
        browser.Dispose();
        try {await initialization.WaitAsync(TimeSpan.FromSeconds(2));}
        catch(TimeoutException){}
        browser.Dispose();closing=true;Close();
        var exited=processTracker.ProcessIds.Length==0;
        if(!exited)
        {
            try
            {
                // Some runtimes deliver BrowserProcessExited late. Actual termination of
                // every observed group process is independent evidence, never a name-based kill.
                await WaitForObservedExitAsync();exited=true;
            }
            catch(TimeoutException){}
        }
        if(environment is not null)
        {environment.ProcessInfosChanged-=OnProcessInfos;environment.BrowserProcessExited-=OnBrowserExited;}
        if(ownsProfile)profileLock!.ReleaseMutex();profileLock?.Dispose();
        exited&=processTracker.Snapshot().All(p=>p.ExitObservedMilliseconds is not null);
        return(outcome,new(browserPid,processTracker.ProcessIds,exited,browserExitEventObserved,observation,retryAfterUtc,
            processTracker.Snapshot(),processTracker.Truncated));
    }
    private async Task InitializeAsync()
    {
        try
        {
            var profile=AuthProfileLocation.ProfileDirectoryForScope(request.ProfileScopeId);
            profileLock=new Mutex(false,AuthProfileLocation.MutexName(profile));
            try {ownsProfile=profileLock.WaitOne(0);}catch(AbandonedMutexException){ownsProfile=true;}
            if(!ownsProfile)throw new IOException("另一个学校登录窗口正在使用会话。");
            environment=await CoreWebView2Environment.CreateAsync(userDataFolder:profile,
                options:new CoreWebView2EnvironmentOptions {ExclusiveUserDataFolderAccess=true});
            WorkerTrace.Stage(WorkerTraceStage.EnvironmentCreated);
            environment.BrowserProcessExited+=OnBrowserExited;environment.ProcessInfosChanged+=OnProcessInfos;
            if(finished.Task.IsCompleted)return;
            await browser.EnsureCoreWebView2Async(environment);
            WorkerTrace.Stage(WorkerTraceStage.ControllerCreated);
            if(finished.Task.IsCompleted)return;
            var core=browser.CoreWebView2;browserPid=checked((int)core.BrowserProcessId);
            processTracker.Observe(browserPid,BrowserProcessRole.Browser);RememberProcesses();
            core.Settings.AreDevToolsEnabled=false;core.Settings.AreHostObjectsAllowed=false;core.Settings.IsWebMessageEnabled=false;
            core.Settings.IsPasswordAutosaveEnabled=false;core.Settings.IsGeneralAutofillEnabled=false;
            SsoHttpsUpgrade.Attach(core,request.AllowedHosts,access);
            core.AddWebResourceRequestedFilter("http://jxgl.dlut.edu.cn/student/*",CoreWebView2WebResourceContext.All);
            core.AddWebResourceRequestedFilter("https://jxgl.dlut.edu.cn/student/*",CoreWebView2WebResourceContext.All);
            if(access.WebVpnGranted)core.AddWebResourceRequestedFilter("https://webvpn.dlut.edu.cn/*",CoreWebView2WebResourceContext.All);
            core.WebResourceRequested+=(_,e)=>
            {
                if(retryAfterUtc is not {} until || until<=DateTimeOffset.UtcNow || !Uri.TryCreate(e.Request.Uri,UriKind.Absolute,out var raw) || CanonicalResponse(raw) is not {} address
                    || !AcademicEndpoints.IsData(address))return;
                // Stop the school's own page scripts from bypassing Retry-After. No request
                // body or credential is inspected; this response is entirely local.
                var seconds=(long)Math.Ceiling((until-DateTimeOffset.UtcNow).TotalSeconds);
                e.Response=environment.CreateWebResourceResponse(new MemoryStream("{}"u8.ToArray()),429,"Too Many Requests",
                    "Content-Type: application/json\r\nRetry-After: "+Math.Max(0,seconds).ToString(System.Globalization.CultureInfo.InvariantCulture));
            };
            responseObserver=new(core,CanonicalResponse);
            responseObserver.SchoolProblem+=ReportProblem;
            responseObserver.SchoolResponse+=(uri,bytes)=>
            {
                try
                {
                    if(retryAfterUtc>DateTimeOffset.UtcNow || importFailure is not null)return;
                    if(AcademicEndpoints.IsData(uri) && readingDeadline is null)readingDeadline=DateTimeOffset.UtcNow.Add(sourceReadTimeout);
                    if(AcademicEndpoints.IsData(uri) && System.Text.Encoding.UTF8.GetString(bytes.AsSpan(0,Math.Min(bytes.Length,64))).TrimStart().StartsWith('<'))
                    {ReportProblem(new(SchoolResponseFailure.NeedsLogin));return;}
                    schoolImport.Accept(uri,bytes);
                    if(string.IsNullOrWhiteSpace(studentNumber.Text) && schoolImport.StudentIdentifier is {} identity)studentNumber.Text=identity;
                }
                catch(Exception ex) when(ex is System.Text.Json.JsonException or InvalidDataException or AcademicReadException or System.Text.RegularExpressions.RegexMatchTimeoutException)
                {
                    ReportProblem(new(SchoolResponseFailure.Unreadable));
                    if(ex is InvalidDataException){importFailure=ex.Message;UpdateImportUi();}
                    WorkerTrace.Stage(WorkerTraceStage.ImportResponseRejected);
                }
            };
            responseObserver.Changed+=_=>
            {
                UpdateImportUi();
                if(done.IsEnabled && !automaticImportAttempted && !string.IsNullOrWhiteSpace(studentNumber.Text))TryImport();
            };
            responseObserver.Arm();
            core.NavigationStarting+=(_,e)=>
            {
                if(access.SecureSsoTarget(e.Uri) is not null)
                {
                    status.Text="正在将学校身份认证跳转升级为 HTTPS…";
                    return; // The local resource interceptor returns 307 before any HTTP transmission.
                }
                if(access.AllowedNavigation(e.Uri))
                {
                    var raw=new Uri(e.Uri);if(raw.IdnHost==SchoolNavigation.WebVpnHost)viaWebVpn=true;
                    var canonical=access.CanonicalForRead(raw);
                    // The academic server serves HTTP. Redirect its local login form to the
                    // verified CAS entry before rendering credential inputs on that page.
                    if(canonical is not null && SchoolNavigation.IsAcademicLogin(canonical.AbsoluteUri))
                    {
                        e.Cancel=true;
                        if(academicSignInAttempted){status.Text="教务系统尚未接受登录会话。可点击本科教务课表重试。";return;}
                        academicSignInAttempted=true;
                        WorkerTrace.Stage(WorkerTraceStage.AcademicCasRedirect);
                        Dispatcher.BeginInvoke(new Action(()=>{if(!closing && !finished.Task.IsCompleted)core.Navigate(access.TransportFor(new(SchoolNavigation.AcademicSignIn),viaWebVpn));}));
                    }
                    return;
                }
                e.Cancel=true;
                status.Text=$"登录跳转未通过地址校验：{SchoolNavigation.BlockedOrigin(e.Uri)}。已暂停；请反馈此提示，可保留窗口。";
                WorkerTrace.Stage(WorkerTraceStage.NavigationBlocked);
            };
            core.FrameNavigationStarting+=(_,e)=>{var canonical=Uri.TryCreate(e.Uri,UriKind.Absolute,out var raw)?access.CanonicalForRead(raw):null;
                if((!access.AllowedNavigation(e.Uri) && access.SecureSsoTarget(e.Uri) is null)
                    || canonical is not null && SchoolNavigation.IsAcademicLogin(canonical.AbsoluteUri))e.Cancel=true;};
            core.NewWindowRequested+=(_,e)=>{e.Handled=true;var secure=access.SecureSsoTarget(e.Uri);
                if(secure is not null)core.Navigate(secure);else if(access.AllowedNavigation(e.Uri))core.Navigate(e.Uri);};
            core.LaunchingExternalUriScheme+=(_,e)=>e.Cancel=true;
            core.DownloadStarting+=(_,e)=>e.Cancel=true;
            core.PermissionRequested+=(_,e)=>e.State=CoreWebView2PermissionState.Deny;
            core.ServerCertificateErrorDetected+=(_,e)=>e.Action=CoreWebView2ServerCertificateErrorAction.Cancel;
            core.ProcessFailed+=(_,_)=>finished.TrySetResult(WorkerStatus.NetworkFailure);
            core.NavigationCompleted+=(_,e)=>
            {
                if(closing || finished.Task.IsCompleted || !Uri.TryCreate(core.Source,UriKind.Absolute,out var raw) || CanonicalResponse(raw) is not {} page || page.IdnHost!="jxgl.dlut.edu.cn")return;
                if(!e.IsSuccess && e.WebErrorStatus!=CoreWebView2WebErrorStatus.OperationCanceled)
                    ReportProblem(new(SchoolResponseFailure.Unavailable));
                else if(e.IsSuccess && page.AbsolutePath.TrimEnd('/')=="/student/for-std/course-table"
                    && !schoolImport.HasResponses && importFailure is null)
                {
                    // Bound an authenticated page whose scripts never return any data too;
                    // waiting for CAS/user interaction itself does not start this deadline.
                    readingDeadline??=DateTimeOffset.UtcNow.Add(sourceReadTimeout);UpdateImportUi();
                }
            };
            status.Text="请登录并进入本人课表。课表读取后会导入桌面；如未自动识别账号，请在底部填写学号。密码与验证码仅在学校页面输入。";
            foreach(var button in sources.Children.OfType<Button>())button.IsEnabled=button.Tag is not true || access.WebVpnGranted;
            System.Windows.Automation.AutomationProperties.SetHelpText(status,"browser-ready");
            configureForFixture?.Invoke(core);
            core.Navigate(SchoolNavigation.AcademicCourseTable);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
            or System.Runtime.InteropServices.COMException or WebView2RuntimeNotFoundException)
        {WorkerTrace.Stage(WorkerTraceStage.BrowserFailed);finished.TrySetResult(WorkerStatus.NetworkFailure);}
    }
    private Uri? CanonicalResponse(Uri uri)
    {
        var proxy=uri.IdnHost.Equals(SchoolNavigation.WebVpnHost,StringComparison.OrdinalIgnoreCase);
        return proxy==viaWebVpn?access.CanonicalForRead(uri):null;
    }
    private void OnBrowserExited(object? sender,CoreWebView2BrowserProcessExitedEventArgs args)
    {WorkerTrace.Stage(WorkerTraceStage.BrowserExitEvent);browserExitEventObserved=true;}
    private async Task WaitForObservedExitAsync()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(2.5));
        try
        {
            while(true)
            {
                var observed=processTracker.ProcessIds;
                await Task.WhenAll(observed.Select(async id=>
                {
                    try {using var process=Process.GetProcessById(id);await process.WaitForExitAsync(deadline.Token);}
                    catch(ArgumentException){}
                }));
                processTracker.ConfirmExited(observed);
                if(processTracker.Snapshot().All(p=>p.ExitObservedMilliseconds is not null))break;
                deadline.Token.ThrowIfCancellationRequested();
            }
        }
        catch(OperationCanceledException) when(deadline.IsCancellationRequested){throw new TimeoutException("Observed browser group did not exit within grace.");}
    }
    private void OnProcessInfos(object? sender,object args)=>RememberProcesses();
    private void RememberProcesses()
    {
        if(environment is null)return;
        foreach(var process in environment.GetProcessInfos())
        {
            var role=process.Kind switch
            {
                CoreWebView2ProcessKind.Browser=>BrowserProcessRole.Browser,
                CoreWebView2ProcessKind.Renderer=>BrowserProcessRole.Renderer,
                CoreWebView2ProcessKind.Gpu=>BrowserProcessRole.Gpu,
                CoreWebView2ProcessKind.Utility=>BrowserProcessRole.Utility,
                CoreWebView2ProcessKind.SandboxHelper=>BrowserProcessRole.SandboxHelper,
                CoreWebView2ProcessKind.PpapiPlugin=>BrowserProcessRole.Plugin,
                CoreWebView2ProcessKind.PpapiBroker=>BrowserProcessRole.PluginBroker,
                _=>BrowserProcessRole.Other
            };
            processTracker.Observe(checked((int)process.ProcessId),role);
        }
    }
}
