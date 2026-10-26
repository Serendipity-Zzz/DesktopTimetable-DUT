using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using DlutTimetable.Domain;

namespace DlutTimetable.Infrastructure.Academic;

public enum AcademicWeekIndexBasis { Unconfirmed,ZeroBased,OneBased }
public sealed record AcademicMappingContext(string AccountId,Term Term,ImmutableArray<Period> Periods,Coverage Coverage,
    AcademicWeekIndexBasis WeekIndexBasis,string Source,string AdapterVersion,DateTimeOffset FetchedAt,
    bool Synthetic=false,bool VerifiedEmpty=false);
public sealed record AcademicMappedBatch(ValidatedDataset Dataset,ImmutableArray<string> LessonIdsWithoutSchedules);
public enum AcademicMappingFailure { InvalidBatch,UnconfirmedWeekIndex,WeekIndexMismatch,OutsideCoverage,UnalignedTime,PeriodCountMismatch }
public sealed class AcademicMappingException(AcademicMappingFailure failure):Exception($"教务课表映射失败：{failure}")
{public AcademicMappingFailure Failure{get;}=failure;}

/// <summary>Pure mapping with explicit caller-supplied calendar, period table and coverage; never fetches or commits.</summary>
public static class AcademicScheduleMapper
{
    private sealed record Key(string LessonId,string? GroupId,int Weekday,int Start,int End,string? Place);
    public static AcademicMappedBatch Map(AcademicScheduleBatch batch,AcademicMappingContext context)
    {
        ArgumentNullException.ThrowIfNull(batch);ArgumentNullException.ThrowIfNull(context);
        var term=context.Term??throw new DatasetValidationException(["缺少学期"]);
        // Validate the external calendar/table/range before consulting a single source row.
        var template=new CurrentDataset("1.0",context.Synthetic,context.AccountId,context.AdapterVersion,context.Source,
            term,context.Coverage,context.FetchedAt,context.Periods,[],VerifiedEmpty:true);
        ValidatedDataset.Create(template,context.AccountId,term.Id);
        if(!Enum.IsDefined(context.WeekIndexBasis) || batch.Lessons.IsDefault || batch.Schedules.IsDefault
            || batch.Lessons.Any(l=>l is null || string.IsNullOrWhiteSpace(l.Id) || string.IsNullOrWhiteSpace(l.CourseName)
                || l.TeacherNames.IsDefault || l.TeacherNames.Any(t=>string.IsNullOrWhiteSpace(t)))
            || batch.Lessons.Select(l=>l.Id).Distinct(StringComparer.Ordinal).Count()!=batch.Lessons.Length
            || batch.Lessons.Length>AcademicScheduleReader.MaximumRecords || batch.Schedules.Length>AcademicScheduleReader.MaximumRecords)
            throw Error(AcademicMappingFailure.InvalidBatch);
        var lessons=batch.Lessons.ToDictionary(l=>l.Id,StringComparer.Ordinal);
        var teachers=lessons.ToDictionary(pair=>pair.Key,pair=>pair.Value.TeacherNames.ToHashSet(StringComparer.Ordinal),StringComparer.Ordinal);
        var periods=context.Periods.OrderBy(p=>p.Index).ToArray();
        var groups=new Dictionary<Key,SortedSet<int>>();var arranged=new HashSet<string>(StringComparer.Ordinal);
        foreach(var schedule in batch.Schedules)
        {
            if(schedule is null || !lessons.TryGetValue(schedule.LessonId,out var lesson)
                || schedule.Weekday!=((int)schedule.Date.DayOfWeek+6)%7+1 || schedule.Start>=schedule.End
                || schedule.SourceWeekIndex is <0 or >60)throw Error(AcademicMappingFailure.InvalidBatch);
            var week=SchoolCalendar.TeachingWeek(term,schedule.Date);
            if(week<1 || week>term.WeekCount || !context.Coverage.Weeks.Contains(week)
                || context.Coverage.Kind=="DAY" && context.Coverage.Date!=schedule.Date)
                throw Error(AcademicMappingFailure.OutsideCoverage);
            if(schedule.SourceWeekIndex is {} sourceWeek)
            {
                if(context.WeekIndexBasis==AcademicWeekIndexBasis.Unconfirmed)throw Error(AcademicMappingFailure.UnconfirmedWeekIndex);
                if(sourceWeek+(context.WeekIndexBasis==AcademicWeekIndexBasis.ZeroBased?1:0)!=week)
                    throw Error(AcademicMappingFailure.WeekIndexMismatch);
            }
            var start=Array.FindIndex(periods,p=>p.Start==schedule.Start);
            var end=Array.FindIndex(periods,p=>p.End==schedule.End);
            if(start<0 || end<start)throw Error(AcademicMappingFailure.UnalignedTime);
            if(schedule.PeriodCount!=end-start+1)throw Error(AcademicMappingFailure.PeriodCountMismatch);
            var place=!string.IsNullOrWhiteSpace(schedule.CustomPlace)?schedule.CustomPlace:schedule.Room?.Name;
            if(!string.IsNullOrWhiteSpace(schedule.PersonName))teachers[schedule.LessonId].Add(schedule.PersonName);
            var key=new Key(schedule.LessonId,schedule.ScheduleGroupId,schedule.Weekday,start+1,end+1,place);
            if(!groups.TryGetValue(key,out var weeks))groups.Add(key,weeks=[]);
            weeks.Add(week);arranged.Add(schedule.LessonId);
        }
        var meetings=groups.Select(pair=>
        {
            var key=pair.Key;var lesson=lessons[key.LessonId];
            var hash=SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(key));
            return new Meeting("academic-"+Convert.ToHexString(hash),key.LessonId,lesson.CourseName,
                teachers[key.LessonId].Order(StringComparer.Ordinal).ToImmutableArray(),key.Place,key.Weekday,key.Start,key.End,pair.Value.ToImmutableArray());
        }).OrderBy(m=>m.Id,StringComparer.Ordinal).ToImmutableArray();
        var dataset=ValidatedDataset.Create(template with{Meetings=meetings,VerifiedEmpty=context.VerifiedEmpty},context.AccountId,term.Id);
        return new(dataset,batch.Lessons.Where(l=>!arranged.Contains(l.Id)).Select(l=>l.Id).Order(StringComparer.Ordinal).ToImmutableArray());
    }
    private static AcademicMappingException Error(AcademicMappingFailure failure)=>new(failure);
}
