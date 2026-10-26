namespace DlutTimetable.Domain;

public sealed record Occurrence(Meeting Meeting, DateOnly Date, TimeOnly Start, TimeOnly End, bool HasConflict);
public enum CoverageStatus { Complete, PartialDay, NotFetched, OutsideTerm }
public sealed record WeekProjection(int Week, CoverageStatus Coverage, IReadOnlyList<Occurrence> Occurrences);
public enum TodayScheduleStatus { NotFetched, OutsideTerm, NoClasses, Upcoming, InProgress, Finished }
public sealed record TodayProjection(TodayScheduleStatus Status, IReadOnlyList<Occurrence> Current, Occurrence? Next)
{
    public bool IsCovered=>Status is not (TodayScheduleStatus.NotFetched or TodayScheduleStatus.OutsideTerm);
}

public static class TimetableProjector
{
    public static WeekProjection Week(ValidatedDataset validated, int week,CalendarAdjustment? calendar=null)
    {
        var data = validated.Value;
        calendar?.ValidateFor(validated);
        var term=calendar?.Term(data.Term)??data.Term;var periods=calendar?.Periods??data.Periods;
        if (week < 1 || week > term.WeekCount) return new(week, CoverageStatus.OutsideTerm, []);
        var day=data.Coverage.Kind=="DAY";
        var covered=day?SchoolCalendar.TeachingWeek(term,data.Coverage.Date!.Value)==week:data.Coverage.Weeks.Contains(week);
        if (!covered) return new(week, CoverageStatus.NotFetched, []);
        var rows = data.Meetings.Where(m => day || m.Weeks.Contains(week)).Select(m => new Occurrence(m,
            day?data.Coverage.Date!.Value:SchoolCalendar.DateOf(term, week, m.Weekday),
            periods.Single(p => p.Index == m.PeriodStart).Start,
            periods.Single(p => p.Index == m.PeriodEnd).End, false)).OrderBy(o => o.Date).ThenBy(o => o.Start).ToArray();
        var result = rows.Select(o => o with { HasConflict = rows.Any(other => other.Meeting.Id != o.Meeting.Id
            && other.Date == o.Date && other.Start < o.End && o.Start < other.End) }).ToArray();
        return new(week, data.Coverage.Kind == "DAY" ? CoverageStatus.PartialDay : CoverageStatus.Complete, result);
    }
    public static TodayProjection Today(ValidatedDataset validated, DateTimeOffset instant,CalendarAdjustment? calendar=null)
    {
        var data = validated.Value;
        var local = SchoolCalendar.ShanghaiTime(instant);
        var date = DateOnly.FromDateTime(local);
        var time = TimeOnly.FromDateTime(local);
        var term=calendar?.Term(data.Term)??data.Term;
        var projection = Week(validated, SchoolCalendar.TeachingWeek(term, date),calendar);
        if(projection.Coverage==CoverageStatus.OutsideTerm)return new(TodayScheduleStatus.OutsideTerm,[],null);
        var covered = projection.Coverage == CoverageStatus.Complete ||
            (projection.Coverage == CoverageStatus.PartialDay && data.Coverage.Date == date);
        var today = projection.Occurrences.Where(o => o.Date == date).ToArray();
        if(!covered)return new(TodayScheduleStatus.NotFetched,[],null);
        var current=today.Where(o=>o.Start<=time && time<o.End).ToArray();
        var next=today.FirstOrDefault(o=>o.Start>time);
        var status=current.Length>0?TodayScheduleStatus.InProgress:next is not null?TodayScheduleStatus.Upcoming:
            today.Length>0?TodayScheduleStatus.Finished:TodayScheduleStatus.NoClasses;
        return new(status,current,next);
    }
}
