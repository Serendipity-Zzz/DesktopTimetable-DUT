using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using DlutTimetable.Contracts;

namespace DlutTimetable.Infrastructure.Academic;

public sealed record AcademicLesson(string Id,string CourseName,ImmutableArray<string> TeacherNames);
public sealed record AcademicRoom(string? Name,string? Building,string? Campus);
public sealed record AcademicSchedule(string LessonId,string? ScheduleGroupId,DateOnly Date,int Weekday,
    int? SourceWeekIndex,TimeOnly Start,TimeOnly End,int PeriodCount,AcademicRoom? Room,string? CustomPlace,string? PersonName);

// An observation candidate, not a CurrentDataset: no inferred identity, term, period table or coverage.
public sealed record AcademicScheduleBatch(ImmutableArray<AcademicLesson> Lessons,ImmutableArray<AcademicSchedule> Schedules);
public enum AcademicReadFailure { InvalidJson,InvalidShape,TooLarge,DuplicateField,DuplicateLesson,UnknownLesson,InvalidDate,WeekdayMismatch,InvalidTime }
public sealed class AcademicReadException(AcademicReadFailure failure,int? recordIndex=null)
    : Exception($"教务模型解析失败：{failure}"+(recordIndex is {} index?$"（记录 {index}）":""))
{
    public AcademicReadFailure Failure {get;}=failure;
    public int? RecordIndex {get;}=recordIndex;
}

/// <summary>Reads arrays explicitly selected by a source mapper. Does not guess endpoint or envelope keys.</summary>
public static class AcademicScheduleReader
{
    public const int MaximumRecords=10000;
    public static AcademicScheduleBatch Read(ReadOnlyMemory<byte> lessonJson,ReadOnlyMemory<byte> scheduleJson)
    {
        if((long)lessonJson.Length+scheduleJson.Length>WorkerProtocol.MaxMessageBytes)throw Error(AcademicReadFailure.TooLarge);
        try
        {
            using var lessons=JsonDocument.Parse(lessonJson,new(){MaxDepth=32});
            using var schedules=JsonDocument.Parse(scheduleJson,new(){MaxDepth=32});
            CheckArray(lessons.RootElement);CheckArray(schedules.RootElement);
            CheckUniqueFields(lessons.RootElement);CheckUniqueFields(schedules.RootElement);
            var result=ImmutableArray.CreateBuilder<AcademicLesson>();var known=new HashSet<string>(StringComparer.Ordinal);
            var index=0;
            foreach(var value in lessons.RootElement.EnumerateArray())
            {
                var id=Id(Required(value,"id",index),index);
                if(!known.Add(id))throw Error(AcademicReadFailure.DuplicateLesson,index);
                var name=Text(Required(value,"courseName",index),512,false,index)!;
                var assignments=Required(value,"teacherAssignmentList",index);CheckArray(assignments,index);
                var teachers=ImmutableArray.CreateBuilder<string>();
                foreach(var assignment in assignments.EnumerateArray())
                    teachers.Add(Text(Required(assignment,"name",index),256,false,index)!);
                result.Add(new(id,name,teachers.Distinct(StringComparer.Ordinal).ToImmutableArray()));index++;
            }
            var meetings=ImmutableArray.CreateBuilder<AcademicSchedule>();index=0;
            foreach(var value in schedules.RootElement.EnumerateArray())
            {
                var lessonId=Id(Required(value,"lessonId",index),index);
                if(!known.Contains(lessonId))throw Error(AcademicReadFailure.UnknownLesson,index);
                var rawDate=Required(value,"date",index);
                if(rawDate.ValueKind!=JsonValueKind.String || !DateOnly.TryParseExact(rawDate.GetString(),"yyyy-MM-dd",
                    CultureInfo.InvariantCulture,DateTimeStyles.None,out var date))throw Error(AcademicReadFailure.InvalidDate,index);
                var weekday=Integer(Required(value,"weekday",index),1,7,index);
                if(weekday!=((int)date.DayOfWeek+6)%7+1)throw Error(AcademicReadFailure.WeekdayMismatch,index);
                var start=Clock(Required(value,"startTime",index),index);var end=Clock(Required(value,"endTime",index),index);
                if(start>=end)throw Error(AcademicReadFailure.InvalidTime,index);
                var count=Integer(Required(value,"periods",index),1,24,index);
                int? week=value.TryGetProperty("weekIndex",out var weekValue) && weekValue.ValueKind!=JsonValueKind.Null
                    ? Integer(weekValue,0,60,index):null; // Preserve raw index; its zero/one basis remains a source concern.
                string? group=value.TryGetProperty("scheduleGroupId",out var groupValue) && groupValue.ValueKind!=JsonValueKind.Null
                    ? Id(groupValue,index):null;
                AcademicRoom? room=null;
                if(value.TryGetProperty("room",out var roomValue) && roomValue.ValueKind!=JsonValueKind.Null)
                {
                    RequireObject(roomValue,index);
                    var roomName=OptionalText(roomValue,"nameZh",512,index);string? building=null,campus=null;
                    if(roomValue.TryGetProperty("building",out var buildingValue) && buildingValue.ValueKind!=JsonValueKind.Null)
                    {
                        RequireObject(buildingValue,index);building=OptionalText(buildingValue,"nameZh",512,index);
                        if(buildingValue.TryGetProperty("campus",out var campusValue) && campusValue.ValueKind!=JsonValueKind.Null)
                        {RequireObject(campusValue,index);campus=OptionalText(campusValue,"nameZh",512,index);}
                    }
                    room=new(roomName,building,campus);
                }
                meetings.Add(new(lessonId,group,date,weekday,week,start,end,count,room,
                    OptionalText(value,"customPlace",512,index),OptionalText(value,"personName",256,index)));index++;
            }
            return new(result.ToImmutable(),meetings.ToImmutable());
        }
        catch(JsonException){throw Error(AcademicReadFailure.InvalidJson);}
    }
    private static TimeOnly Clock(JsonElement value,int index)
    {
        if(value.ValueKind!=JsonValueKind.Number || !value.TryGetInt32(out var time)
            || time is <0 or >2359 || time%100>=60)throw Error(AcademicReadFailure.InvalidTime,index);
        return new(time/100,time%100);
    }
    private static int Integer(JsonElement value,int minimum,int maximum,int index)
    {
        if(value.ValueKind!=JsonValueKind.Number || !value.TryGetInt32(out var number) || number<minimum || number>maximum)
            throw Error(AcademicReadFailure.InvalidShape,index);
        return number;
    }
    private static string Id(JsonElement value,int index)
    {
        if(value.ValueKind!=JsonValueKind.Number || !value.TryGetInt64(out var id) || id<=0)
            throw Error(AcademicReadFailure.InvalidShape,index);
        return id.ToString(CultureInfo.InvariantCulture);
    }
    private static string? OptionalText(JsonElement value,string name,int maximum,int index)=>
        value.TryGetProperty(name,out var text)?Text(text,maximum,true,index):null;
    private static string? Text(JsonElement value,int maximum,bool optional,int index)
    {
        if(optional && value.ValueKind==JsonValueKind.Null)return null;
        if(value.ValueKind!=JsonValueKind.String)throw Error(AcademicReadFailure.InvalidShape,index);
        var text=value.GetString()!;
        if(text.Length>maximum || text.Contains('\0') || !optional && string.IsNullOrWhiteSpace(text))
            throw Error(AcademicReadFailure.InvalidShape,index);
        return text;
    }
    private static JsonElement Required(JsonElement value,string field,int index)
    {
        RequireObject(value,index);
        return value.TryGetProperty(field,out var found)?found:throw Error(AcademicReadFailure.InvalidShape,index);
    }
    private static void RequireObject(JsonElement value,int index)
    {if(value.ValueKind!=JsonValueKind.Object)throw Error(AcademicReadFailure.InvalidShape,index);}
    private static void CheckArray(JsonElement value,int? index=null)
    {
        if(value.ValueKind!=JsonValueKind.Array)throw Error(AcademicReadFailure.InvalidShape,index);
        if(value.GetArrayLength()>MaximumRecords)throw Error(AcademicReadFailure.TooLarge,index);
    }
    internal static void CheckUniqueFields(JsonElement value)
    {
        if(value.ValueKind==JsonValueKind.Object)
        {
            var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(var field in value.EnumerateObject())
            {if(!names.Add(field.Name))throw Error(AcademicReadFailure.DuplicateField);CheckUniqueFields(field.Value);}
        }
        else if(value.ValueKind==JsonValueKind.Array)foreach(var item in value.EnumerateArray())CheckUniqueFields(item);
    }
    private static AcademicReadException Error(AcademicReadFailure failure,int? index=null)=>new(failure,index);
}
