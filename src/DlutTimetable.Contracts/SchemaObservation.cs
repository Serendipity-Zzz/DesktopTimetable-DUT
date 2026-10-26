using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;

namespace DlutTimetable.Contracts;

public sealed record ResponseObservation(string Host,string Route,string Method,int Status,string[] QueryNames,
    string ShapeJson,int Bytes,bool Truncated,int MaskedNames,string MediaType="application/json",string Outcome="Json");
public sealed record ObservationReport(int Version,DateTimeOffset CreatedUtc,ResponseObservation[] Responses,int SkippedBusy,int Rejected,
    string[]? PublicScriptPaths=null);

public static class PublicSchoolAssets
{
    public const int MaximumScriptCandidates=192;
    public const int MaximumScriptPaths=48;
    public static string[] AcademicScriptPaths(IEnumerable<string> urls)
    {
        var paths=urls.Take(MaximumScriptCandidates).Select(AcademicScriptPath).OfType<string>().Distinct().ToArray();
        // RequireJS inserts many shared models ahead of the entry module. Retain both ends
        // rather than spending the whole bounded report on the first model scripts.
        return paths.Length<=MaximumScriptPaths?paths:paths.Take(MaximumScriptPaths/2).Concat(paths.TakeLast(MaximumScriptPaths/2)).ToArray();
    }
    // Only anonymous static code paths, never query/fragment values or account-specific routes.
    public static string? AcademicScriptPath(string uri)
    {
        if(!Uri.TryCreate(uri,UriKind.Absolute,out var parsed) || parsed.IdnHost!="jxgl.dlut.edu.cn"
            || !SchoolNavigation.Allowed(uri,SchoolNavigation.Hosts))return null;
        var path=parsed.AbsolutePath;
        return path.Length<=240 && path.StartsWith("/student/static/",StringComparison.Ordinal)
            && path.EndsWith(".js",StringComparison.Ordinal) && path.All(c=>char.IsAsciiLetterOrDigit(c) || "/._-".Contains(c))
            && !Regex.IsMatch(path,"[0-9]{7,}") && !path.Contains("..",StringComparison.Ordinal)?path:null;
    }
}

/// <summary>Bounded structural evidence. Unknown names are keyed hashes, never arbitrary dictionary keys.</summary>
public sealed class SchemaObservation : IDisposable
{
    public const int MaximumEntries=24;
    public const int MaximumShapeBytes=32768;
    private readonly byte[] key=RandomNumberGenerator.GetBytes(32);
    private static readonly HashSet<string> Fields=new((
        "code status message msg success data result results rows list records items total count page pageSize pageNo size current pages " +
        "content value name id type key label text children courses course courseList courseName courseId courseCode courseType " +
        "meetings meeting periods period periodStart periodEnd periodIndex periodList startTime endTime time date day weekday weeks week weekNo weekCount weekStart weekEnd " +
        "teacher teacherName teachers teacherNames room roomName classroom classroomName location campus campusName term termId termCode termName semester schoolYear year " +
        "student studentId studentName user userId userName identity account accountId roles role college major className classId " +
        "kcmc kch kcdm kcid kclb kcxz kcxx jsxm jsxx jsgh jsmc jxbmc jxbid jxb_id xqj xq xqmc xqm zc zcd skzc skzcd kczc jcs jcd jc ksjc jsjc sksj skdd skjs cdmc cddm jxcdmc xnm xqm xnxq xnxqdm xn xqdm xh xm bjmc bjdm kkbm kkbmmc zxs xf kkbz bz zgh " +
        "课程 课程名称 课程编号 教师 教师姓名 任课教师 教室 上课地点 上课时间 周次 星期 节次 开始节次 结束节次 学期 学年 学期编号 校区 学号 姓名 课表 数据 列表 " +
        "api tp v1 v2 rest portal sems timetable schedule teaching student semester class course query get list find select detail index json action " +
        "act m method appId componentId termId weekId weekdayId for-std course-table static").Split(' ',StringSplitOptions.RemoveEmptyEntries),StringComparer.OrdinalIgnoreCase);
    // Names verified in anonymous official Lesson/Schedule/TimeTable/common JavaScript.
    private static readonly HashSet<string> AcademicFields=new((
        "lessonId scheduleGroupId weekIndex teacherAssignmentList requiredPeriodInfo nameZh nameEn building " +
        "indexNo timeSegments units schedules scheduleJsonParams teacherId personId personName customPlace lessonType " +
        "semesterId bizTypeId rootClass rootId rootName activityType activityId activityName minorCourseName").Split(' '),StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> NumericScheduleFields=new("weekday week weeks weekNo weekIndex weekCount weekStart weekEnd periods period periodIndex periodStart periodEnd indexNo xqj zc jc ksjc jsjc 星期 开始节次 结束节次".Split(' '),StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> WeekTextFields=new("weeks zcd skzc skzcd kczc jcs jcd 周次 节次".Split(' '),StringComparer.OrdinalIgnoreCase);
    private int nodes,masked;
    private bool truncated;
    private bool academicSource;
    private int timingUnitsRemaining;
    private readonly HashSet<string> academicRoutes=new(StringComparer.Ordinal);
    public void ResetPageEvidence()=>academicRoutes.Clear();
    private string SafeName(string name)
    {
        if(Fields.Contains(name) || academicSource && AcademicFields.Contains(name))return name;
        masked++;return "key_"+Convert.ToHexString(HMACSHA256.HashData(key,Encoding.UTF8.GetBytes(name)))[..12];
    }
    private static bool Sensitive(string name)
    {
        var lower=name.ToLowerInvariant().Replace("_","").Replace("-","");
        return new[]{"password","passwd","token","cookie","secret","authorization","csrf","session","验证码","密码"}.Any(lower.Contains);
    }
    public ResponseObservation Create(Uri uri,string method,int status,ReadOnlyMemory<byte> body)
    {
        if(body.Length>WorkerProtocol.MaxMessageBytes)throw new InvalidDataException("Observation body exceeds limit.");
        if(!SchoolNavigation.Allowed(uri.AbsoluteUri,SchoolNavigation.Hosts))throw new InvalidDataException("Observation source rejected.");
        nodes=0;masked=0;truncated=false;timingUnitsRemaining=24;academicSource=uri.IdnHost.Equals("jxgl.dlut.edu.cn",StringComparison.OrdinalIgnoreCase);
        using var json=JsonDocument.Parse(body,new JsonDocumentOptions{MaxDepth=48});
        using var output=new MemoryStream();
        using(var writer=new Utf8JsonWriter(output)){Shape(writer,json.RootElement,"",0);}
        if(output.Length>MaximumShapeBytes)throw new InvalidDataException("Observation shape exceeds limit.");
        var route=academicSource && academicRoutes.Contains(uri.AbsolutePath)?uri.AbsolutePath:
            "/"+string.Join('/',uri.AbsolutePath.Split('/',StringSplitOptions.RemoveEmptyEntries).Take(16).Select(SafeName));
        var query=uri.Query.TrimStart('?').Split('&',StringSplitOptions.RemoveEmptyEntries).Take(24)
            .Select(part=>part.Split('=',2)[0]).Select(Uri.UnescapeDataString).Where(name=>!Sensitive(name)).Select(SafeName).Distinct().ToArray();
        return new(uri.IdnHost,route,method is "GET" or "POST"?method:"OTHER",status,query,Encoding.UTF8.GetString(output.ToArray()),body.Length,truncated,masked);
    }
    public ResponseObservation Metadata(Uri uri,string method,int status,string mediaType,string outcome)=>
        Create(uri,method,status,"null"u8.ToArray()) with {Bytes=0,MediaType=mediaType,Outcome=outcome};
    public ResponseObservation? ObserveAcademicPage(Uri uri,string method,int status,ReadOnlyMemory<byte> body)
    {
        var evidence=AcademicPageEvidence.Read(uri,method,status,body);
        if(evidence is null)return null;
        foreach(var path in evidence.StaticRoutes)if(academicRoutes.Count<AcademicPageEvidence.MaximumRoutes)academicRoutes.Add(path);
        return Metadata(uri,method,status,"text/html","HtmlMetadata") with
        {
            Bytes=body.Length,Truncated=evidence.Truncated,
            ShapeJson=JsonSerializer.Serialize(new{academicStaticRoutes=evidence.StaticRoutes})
        };
    }
    private void Shape(Utf8JsonWriter writer,JsonElement value,string field,int depth,bool academicTiming=false,int nodeLimit=256)
    {
        if(nodes>=nodeLimit || depth>=8){truncated=true;writer.WriteStringValue("<bounded>");return;}
        nodes++;
        switch(value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var timingContainer=academicSource && IsAcademicTimingContainer(value);
                var properties=value.EnumerateObject().Where(property=>!Sensitive(property.Name)).Take(65).ToArray();
                var propertyCount=Math.Min(64,properties.Length);
                if(properties.Length>64)truncated=true;
                for(var index=0;index<propertyCount;index++)
                {
                    if(nodes>=nodeLimit){truncated=true;break;}
                    var property=properties[index];
                    // Reserve space for siblings so a large lesson object cannot consume the
                    // entire response budget before the schedules array is reached.
                    var childLimit=nodes+Math.Max(1,(nodeLimit-nodes)/(propertyCount-index));
                    writer.WritePropertyName(SafeName(property.Name));Shape(writer,property.Value,property.Name,depth+1,timingContainer,childLimit);
                }
                writer.WriteEndObject();break;
            case JsonValueKind.Array:
                writer.WriteStartObject();writer.WriteNumber("arrayCount",value.GetArrayLength());writer.WritePropertyName("samples");writer.WriteStartArray();
                var sampleCount=Math.Min(2,value.GetArrayLength());var sampleIndex=0;
                foreach(var item in value.EnumerateArray())
                {
                    if(sampleIndex==sampleCount)break;
                    if(nodes>=nodeLimit){truncated=true;break;}
                    var childLimit=nodes+Math.Max(1,(nodeLimit-nodes)/(sampleCount-sampleIndex));sampleIndex++;
                    Shape(writer,item,field,depth+1,false,childLimit);
                }
                writer.WriteEndArray();
                if(academicSource && value.GetArrayLength()<=timingUnitsRemaining
                    && AcademicPeriodEvidence.ReadCompleteArray(value) is {} units)
                {
                    timingUnitsRemaining-=units.Length;
                    writer.WritePropertyName("timingUnits");writer.WriteStartArray();
                    foreach(var unit in units)
                    {
                        writer.WriteStartObject();writer.WriteNumber("indexNo",unit.Index);
                        writer.WriteNumber("startTime",unit.StartTime);writer.WriteNumber("endTime",unit.EndTime);writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();break;
            case JsonValueKind.Number:
                if(academicTiming && (field is "startTime" or "endTime") && value.TryGetInt32(out var clock)
                    && clock is >=0 and <=2359 && clock%100<60)writer.WriteNumberValue(clock);
                else if(NumericScheduleFields.Contains(field) && value.TryGetInt32(out var number) && number is >=0 and <=60)writer.WriteNumberValue(number);
                else writer.WriteStringValue("<number>");break;
            case JsonValueKind.String:
                var text=value.GetString()??"";
                if(academicTiming && field=="date" && text.Length==10
                    && DateOnly.TryParseExact(text,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date)
                    && date.Year is >=2010 and <=2100)writer.WriteStringValue(text);
                else if(WeekTextFields.Contains(field) && text.Length is >0 and <=96 && text.All(c=>"0123456789 ,，;；-~至单双周节()（）".Contains(c))
                    && Regex.Matches(text,"[0-9]+").Cast<Match>().All(match=>match.Length<=2 && int.Parse(match.Value)<=60))writer.WriteStringValue(text);
                else writer.WriteStringValue("<string>");break;
            case JsonValueKind.Null:writer.WriteNullValue();break;
            default:writer.WriteStringValue("<boolean>");break;
        }
    }
    private static bool IsAcademicTimingContainer(JsonElement value)=>
        value.TryGetProperty("startTime",out _) && value.TryGetProperty("endTime",out _)
        && (value.TryGetProperty("lessonId",out _) && value.TryGetProperty("weekday",out _) && value.TryGetProperty("date",out _)
            || value.TryGetProperty("indexNo",out _) || value.TryGetProperty("indexno",out _));
    public void Dispose()=>CryptographicOperations.ZeroMemory(key);
}
