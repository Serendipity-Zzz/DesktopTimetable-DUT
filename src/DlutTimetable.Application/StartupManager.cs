namespace DlutTimetable.Application;

public interface IStartupRegistration
{
    string? Read();
    void Write(string? command);
}

public sealed record StartupState(bool Desired, bool? RegistrationObserved, bool OutOfSync, bool Busy, string Message);

/// <summary>Register, read back, then confirm preferences. Never repairs a mismatch implicitly.</summary>
public sealed class StartupManager(IStartupRegistration registration, string command, bool initialized, bool desired,
    Func<bool,Task> persistConfirmed)
{
    private readonly SemaphoreSlim gate=new(1,1);
    private bool initialized=initialized;
    private bool desired=desired;
    public StartupState State { get; private set; }=new(desired,null,false,false,"正在检查登录启动设置");
    public event Action<StartupState>? Changed;
    private void Publish(StartupState value) { State=value;Changed?.Invoke(value); }
    private StartupState Observed(string? actual,string? error=null)
    {
        var matches=desired ? string.Equals(actual,command,StringComparison.OrdinalIgnoreCase) : actual is null;
        return new(desired,actual is not null,!matches,false,error??(!matches
            ? "启动登记与已保存偏好不一致，请检查或重新应用设置。"
            : desired ? "已登记登录启动；Windows 的启动应用设置或策略仍可能禁用它。" : "已关闭登录启动。"));
    }
    private static bool Expected(Exception ex)=>ex is IOException or InvalidDataException or UnauthorizedAccessException
        or System.Security.SecurityException or InvalidOperationException or ArgumentException;
    private static string FailureReason(Exception ex)=>ex switch
    {
        UnauthorizedAccessException or System.Security.SecurityException=>"访问权限不足，请检查文件与启动登记权限。",
        InvalidDataException=>"配置或启动登记格式不受支持。",
        IOException=>"无法读写配置或启动登记，请检查文件是否可用。",
        ArgumentException=>"启动路径或设置参数无效。",
        _=>"启动登记状态无效，请在设置中重新检查。"
    };
    public async Task InitializeAsync(bool allowFirstRegistration)
    {
        await gate.WaitAsync();
        try
        {
            var actual=registration.Read();
            // Existing entries may be left by an interrupted initialization or another install.
            // Observe them; never blindly re-enable or replace them at each process start.
            if(!initialized && allowFirstRegistration && actual is null)
                await ApplyCoreAsync(desired);
            else if(!initialized && desired && allowFirstRegistration && string.Equals(actual,command,StringComparison.OrdinalIgnoreCase))
            {
                await persistConfirmed(true);initialized=true;desired=true;Publish(Observed(actual));
            }
            else Publish(Observed(actual));
        }
        catch(Exception ex) when(Expected(ex))
        { Publish(new(desired,null,true,false,"无法确认启动设置。"+FailureReason(ex))); }
        finally { gate.Release(); }
    }
    public async Task ApplyAsync(bool value)
    {
        await gate.WaitAsync();
        try { await ApplyCoreAsync(value); }
        finally { gate.Release(); }
    }
    private async Task ApplyCoreAsync(bool value)
    {
        Publish(State with {Busy=true,Message="正在保存启动设置…"});
        string? before=null;var readBefore=false;var attempted=false;
        var next=value?command:null;
        try
        {
            before=registration.Read();readBefore=true;
            if(!string.Equals(before,next,StringComparison.OrdinalIgnoreCase))
            { attempted=true;registration.Write(next); }
            if(!string.Equals(registration.Read(),next,StringComparison.OrdinalIgnoreCase))
                throw new IOException("启动登记读回不一致。");
            await persistConfirmed(value);
            initialized=true;desired=value;Publish(Observed(next));
        }
        catch(Exception ex) when(Expected(ex))
        {
            var rolledBack=!attempted;
            if(readBefore && attempted)
            {
                try
                {
                    var current=registration.Read();
                    // Do not overwrite a value changed concurrently outside this transaction.
                    if(string.Equals(current,before,StringComparison.OrdinalIgnoreCase))rolledBack=true;
                    else if(string.Equals(current,next,StringComparison.OrdinalIgnoreCase))
                    { registration.Write(before);rolledBack=string.Equals(registration.Read(),before,StringComparison.OrdinalIgnoreCase); }
                }
                catch(Exception rollback) when(Expected(rollback)){rolledBack=false;}
            }
            try
            {
                var state=Observed(registration.Read(),"启动设置未保存"+(rolledBack?"，已保留原设置。":"，登记状态需修复。")+FailureReason(ex));
                Publish(state with {OutOfSync=state.OutOfSync || !rolledBack});
            }
            catch(Exception read) when(Expected(read))
            { Publish(new(desired,null,true,false,"启动设置未保存，无法确认登记状态。"+FailureReason(ex))); }
        }
    }
}
