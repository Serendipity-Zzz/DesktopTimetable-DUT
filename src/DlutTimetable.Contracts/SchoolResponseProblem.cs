using System.Globalization;

namespace DlutTimetable.Contracts;

public enum SchoolResponseFailure { NeedsLogin,RateLimited,Unavailable,Timeout,TooLarge,Unreadable }
public sealed record SchoolResponseProblem(SchoolResponseFailure Failure,DateTimeOffset? RetryAfterUtc=null);

public static class AcademicEndpoints
{
    public static bool IsData(Uri uri)=>uri.IdnHost=="jxgl.dlut.edu.cn" && uri.IsDefaultPort && uri.UserInfo.Length==0
        && uri.Scheme is "http" or "https" && uri.AbsolutePath is
            "/student/for-std/course-table/get-data" or "/student/ws/schedule-table/datum" or "/student/ws/schedule-table/timetable-layout";
}

public static class SchoolRetryAfter
{
    public static DateTimeOffset Parse(string? header,DateTimeOffset now)
    {
        if(header is not {Length:>0 and <=128})return now.AddSeconds(30);
        if(long.TryParse(header,NumberStyles.None,CultureInfo.InvariantCulture,out var seconds) && seconds>=0)
        {
            var maximum=(DateTimeOffset.MaxValue-now).Ticks/TimeSpan.TicksPerSecond;
            return seconds>maximum?DateTimeOffset.MaxValue:now.AddTicks(seconds*TimeSpan.TicksPerSecond);
        }
        if(DateTimeOffset.TryParseExact(header,"r",CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out var date))return date>now?date:now;
        return now.AddSeconds(30);
    }
}
