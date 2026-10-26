namespace DlutTimetable.Domain;

public static class SchoolCalendar
{
    public static TimeZoneInfo Shanghai { get; } = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
    public static DateOnly ShanghaiDate(DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, Shanghai).DateTime);
    public static DateTime ShanghaiTime(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Shanghai).DateTime;
    public static int Weekday(DateOnly day) => day.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)day.DayOfWeek;
    public static int TeachingWeek(Term term, DateOnly date) =>
        (int)Math.Floor((date.DayNumber - term.FirstTeachingMonday.DayNumber) / 7d) + 1;
    public static DateOnly DateOf(Term term, int week, int weekday)
    {
        if (week < 1 || week > term.WeekCount || weekday is < 1 or > 7) throw new ArgumentOutOfRangeException(nameof(week));
        return term.FirstTeachingMonday.AddDays((week - 1) * 7 + weekday - 1);
    }
    public static DateTimeOffset NextShanghaiMidnight(DateTimeOffset instant)
    {
        var midnight = ShanghaiDate(instant).AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new(midnight, Shanghai.GetUtcOffset(midnight));
    }
}
