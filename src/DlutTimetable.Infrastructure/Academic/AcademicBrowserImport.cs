using System.Collections.Immutable;
using DlutTimetable.Contracts;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DlutTimetable.Domain;

namespace DlutTimetable.Infrastructure.Academic;

/// <summary>One user initiated browser import. Raw school responses live only in this object.</summary>
public sealed class AcademicBrowserImport
{
    private JsonElement? datum,layout,configuration;
    private string? semesterId,studentIdentifier;
    public bool HasResponses=>datum.HasValue && layout.HasValue && configuration.HasValue && semesterId is not null;
    public string? StudentIdentifier=>studentIdentifier;
    public void Reset(){datum=null;layout=null;configuration=null;semesterId=null;studentIdentifier=null;}
    public void Accept(Uri uri,ReadOnlyMemory<byte> bytes)
    {
        if(!SchoolNavigation.Allowed(uri.AbsoluteUri,SchoolNavigation.Hosts) || uri.IdnHost!="jxgl.dlut.edu.cn" || bytes.Length>5*1024*1024)return;
        if(uri.AbsolutePath=="/student/for-std/course-table")
        {
            // Only an explicit student/std bootstrap object is considered identity evidence.
            var html=Encoding.UTF8.GetString(bytes.Span);
            var match=Regex.Match(html,"(?:\\b(?:student|std)\\s*=|[\"'](?:student|std)[\"']\\s*:)\\s*\\{[^{}]{0,4096}?[\"'](?:code|studentNo|studentNumber)[\"']\\s*:\\s*[\"']([0-9]{8,16})[\"']",
                RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
            if(match.Success)studentIdentifier=match.Groups[1].Value;
            return;
        }
        if(!AcademicEndpoints.IsData(uri))return;
        // A failed replacement must not leave an older candidate importable.
        switch(uri.AbsolutePath)
        {
            case "/student/for-std/course-table/get-data":configuration=null;datum=null;layout=null;semesterId=null;break;
            case "/student/ws/schedule-table/datum":datum=null;break;
            case "/student/ws/schedule-table/timetable-layout":layout=null;break;
        }
        using var document=JsonDocument.Parse(bytes,new(){MaxDepth=32});
        var root=document.RootElement.Clone();
        if(root.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("学校课表响应格式无效，已有课表保留。");
        AcademicScheduleReader.CheckUniqueFields(root);
        AcademicBatchCompleteness.CheckEnvelope(root);
        switch(uri.AbsolutePath)
        {
            case "/student/for-std/course-table/get-data":
                configuration=root;
                semesterId=Query(uri,"semesterId");
                // These explicit fields refer to the signed in student's identity, never a lesson ID.
                foreach(var key in new[]{"studentNo","studentNumber","studentCode"})
                    if(root.TryGetProperty(key,out var number) && number.ValueKind==JsonValueKind.String)studentIdentifier=number.GetString();
                break;
            case "/student/ws/schedule-table/datum":datum=root;break;
            case "/student/ws/schedule-table/timetable-layout":layout=root;break;
        }
    }
    public ValidatedDataset Build(string? confirmedStudentIdentifier=null)
    {
        if(!HasResponses)throw new InvalidDataException("请先进入本人课表，再点击“重新读取”。");
        var identity=(confirmedStudentIdentifier??studentIdentifier)?.Trim();
        if(identity is null || !Regex.IsMatch(identity,"^[0-9A-Za-z-]{6,32}$",RegexOptions.CultureInvariant))
            throw new InvalidDataException("请填写学号，用于在本机区分账号，然后导入课表。");
        if(!string.IsNullOrWhiteSpace(studentIdentifier) && !string.Equals(identity,studentIdentifier,StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("填写的学号与学校页面账号不一致，请核对后再导入。");
        var lessonArray=SelectArray(datum!.Value,v=>Has(v,"courseName","teacherAssignmentList","id"));
        var scheduleArray=SelectArray(datum.Value,v=>Has(v,"lessonId","date","weekday","startTime","endTime","weekIndex"));
        var periods=SelectArray(layout!.Value,v=>Has(v,"indexNo","startTime","endTime"));
        var batch=AcademicScheduleReader.Read(Encoding.UTF8.GetBytes(lessonArray.GetRawText()),Encoding.UTF8.GetBytes(scheduleArray.GetRawText()));
        AcademicBatchCompleteness.CheckLessonScope(configuration!.Value,batch);
        AcademicBatchCompleteness.CheckScheduleCount(datum.Value,batch.Schedules.Length);
        if(batch.Schedules.IsEmpty)throw new InvalidDataException("学校没有返回可验证的课程安排，已保留原课表。");
        // All dated rows must agree on the same one-based teaching calendar; a single
        // inconsistent row rejects the whole batch instead of silently dropping courses.
        if(batch.Schedules.Any(s=>s.SourceWeekIndex is null or <1))throw new InvalidDataException("学校周次信息不完整，已保留原课表。");
        DateOnly[] mondays;
        try {mondays=batch.Schedules.Select(s=>s.Date.AddDays(-(s.Weekday-1)-7*(s.SourceWeekIndex!.Value-1))).Distinct().ToArray();}
        catch(ArgumentOutOfRangeException){throw new InvalidDataException("学校日期超出范围，已有课表保留。");}
        if(mondays.Length!=1)throw new InvalidDataException("学校日期与周次不一致，已保留原课表。");
        var first=mondays[0];
        int? declaredWeekCount=null;
        // The get-data response's explicit week list can include weeks without lessons.
        foreach(var property in configuration!.Value.EnumerateObject())
        {
            if(!property.Name.Contains("week",StringComparison.OrdinalIgnoreCase) || property.Value.ValueKind!=JsonValueKind.Array)continue;
            var weeks=property.Value.EnumerateArray().ToArray();
            if(weeks.Length is <1 or >60 || weeks.Any(w=>w.ValueKind!=JsonValueKind.Number || !w.TryGetInt32(out var n) || n is <1 or >60))continue;
            var numbers=weeks.Select(w=>w.GetInt32()).Order().ToArray();
            if(numbers.SequenceEqual(Enumerable.Range(1,numbers.Length)))
            {
                if(declaredWeekCount is {} previous && previous!=numbers.Length)throw new InvalidDataException("学校学期范围不一致，已有课表保留。");
                declaredWeekCount=numbers.Length;
            }
        }
        var maxWeek=declaredWeekCount??throw new InvalidDataException("学校未返回完整学期周范围，已有课表保留。");
        var year=first.Month>=7?first.Year:first.Year-1;
        var term=new Term("jxgl-"+semesterId,$"{year}-{year+1}","semester-"+semesterId,first,"Asia/Shanghai",maxWeek);
        var account="dlut-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToUpperInvariant())));
        var context=new AcademicMappingContext(account,term,AcademicPeriodTableReader.Read(Encoding.UTF8.GetBytes(periods.GetRawText())),
            new Coverage("TERM",Enumerable.Range(1,maxWeek).ToImmutableArray()),AcademicWeekIndexBasis.OneBased,
            "dlut-academic","academic-browser-1",DateTimeOffset.UtcNow);
        return AcademicScheduleMapper.Map(batch,context).Dataset;
    }
    private static bool Has(JsonElement value,params string[] keys)=>value.ValueKind==JsonValueKind.Object && keys.All(key=>value.TryGetProperty(key,out _));
    private static JsonElement SelectArray(JsonElement root,Func<JsonElement,bool> predicate)
    {
        var found=new List<JsonElement>();
        Visit(root,0);
        if(found.Count!=1)throw new InvalidDataException("学校课表格式发生变化，未覆盖原课表。");
        return found[0];
        void Visit(JsonElement value,int depth)
        {
            if(depth>8)return;
            if(value.ValueKind==JsonValueKind.Array)
            {
                if(value.GetArrayLength()>0 && predicate(value[0])){found.Add(value);return;}
                return;
            }
            if(value.ValueKind==JsonValueKind.Object)foreach(var property in value.EnumerateObject())Visit(property.Value,depth+1);
        }
    }
    private static string? Query(Uri uri,string name)
    {
        string? result=null;var found=0;
        foreach(var part in uri.Query.TrimStart('?').Split('&'))
        {
            var pair=part.Split('=',2);
            if(pair.Length==2 && pair[0]==name)
            {found++;var value=Uri.UnescapeDataString(pair[1]);result=value.Length is >0 and <=64 && value.All(char.IsAsciiDigit)?value:null;}
        }
        return found==1?result:null;
    }
}
