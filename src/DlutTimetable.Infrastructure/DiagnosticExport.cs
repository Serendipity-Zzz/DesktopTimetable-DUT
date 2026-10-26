using System.Text.Json;
using DlutTimetable.Domain;

namespace DlutTimetable.Infrastructure;

public sealed record DiagnosticUiState(TimetableState State,double Scale,bool ReducedMotion,bool GlassSelected,bool Dark,
    int BackgroundTransparencyPercent,int ForegroundTransparencyPercent,bool WorkerBusy,
    bool StartupDesired,bool? StartupRegistrationObserved,bool StartupOutOfSync,bool CacheReadable=true);

public static class DiagnosticExport
{
    // No arbitrary message, URL, path, account, course or adapter string enters this report.
    public static byte[] Create(ValidatedDataset? dataset,DiagnosticUiState ui,DiagnosticSnapshot? diagnostics=null)
    {
        if(!Enum.IsDefined(ui.State) || !double.IsFinite(ui.Scale) || ui.Scale is <0.1 or >2
            || ui.BackgroundTransparencyPercent is <40 or >95 || ui.ForegroundTransparencyPercent is <0 or >45)
            throw new ArgumentException("诊断状态无效。");
        if(diagnostics is not null && (diagnostics.Entries is null || diagnostics.Entries.Length>DiagnosticJournal.Capacity
            || diagnostics.Evicted<0 || diagnostics.LogWriteFailures<0 || diagnostics.LogQueueDropped<0))
            throw new ArgumentException("诊断记录超出范围。");
        var data=dataset?.Value;
        var coverage=data?.Coverage.Kind switch{"TERM"=>"Term","WEEK"=>"Week","DAY"=>"Day",_=>"None"};
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            diagnosticVersion=2,createdUtc=DateTimeOffset.UtcNow,appVersion=typeof(DiagnosticExport).Assembly.GetName().Version?.ToString(),
            runtimeVersion=Environment.Version.ToString(),osVersion=Environment.OSVersion.Version.ToString(),
            architecture=System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            protocolVersion=DlutTimetable.Contracts.WorkerProtocol.Version,
            data=new{present=data is not null,synthetic=data?.Synthetic??false,localImport=data?.Source=="local-import",
                normalizedSchema=data is null?null:"1.0",coverage,weekCount=data?.Term.WeekCount??0,
                meetingCount=data?.Meetings.Length??0,periodCount=data?.Periods.Length??0,lastFetchedUtc=data?.FetchedAt.ToUniversalTime()},
            ui,diagnostics
        },new JsonSerializerOptions{WriteIndented=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase,
            Converters={new System.Text.Json.Serialization.JsonStringEnumConverter()}});
    }
}
