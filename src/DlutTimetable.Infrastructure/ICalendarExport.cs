using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DlutTimetable.Domain;

namespace DlutTimetable.Infrastructure;

// A local RFC 5545 snapshot, not a calendar subscription or an invitation.
// Each real date is explicit; no min/max-week recurrence expansion is used.
public static class ICalendarExport
{
    public static byte[] Create(ValidatedDataset source,CalendarAdjustment? calendar=null,
        DateTimeOffset? exportedAt=null,CancellationToken cancellationToken=default)
    {
        calendar?.ValidateFor(source);
        var data=source.Value;var term=calendar?.Term(data.Term)??data.Term;
        using var output=new MemoryStream();
        var timestamp=(exportedAt??DateTimeOffset.UtcNow).UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'",CultureInfo.InvariantCulture);
        Line("BEGIN:VCALENDAR");Line("VERSION:2.0");Line("PRODID:-//DLUT Desktop Timetable//Calendar Export//ZH");
        Line("CALSCALE:GREGORIAN");Line("X-WR-CALNAME:大工课表");
        var eventCount=0;
        for(var week=1;week<=term.WeekCount;week++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projection=TimetableProjector.Week(source,week,calendar);
            var duplicates=new Dictionary<(string Course,DateOnly Date,int Start,int End),int>();
            foreach(var occurrence in projection.Occurrences.OrderBy(o=>o.Date).ThenBy(o=>o.Start).ThenBy(o=>o.Meeting.Id,StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                eventCount++;
                var meeting=occurrence.Meeting;
                var originalDate=data.Coverage.Kind=="DAY"?data.Coverage.Date!.Value:SchoolCalendar.DateOf(data.Term,week,meeting.Weekday);
                // The academic meeting hash can change when its room changes.
                // Identify a class/date/source slot, with a deterministic tie-break
                // only when the source really contains multiple identical slots.
                var slot=(meeting.CourseId,originalDate,meeting.PeriodStart,meeting.PeriodEnd);
                var ordinal=duplicates.GetValueOrDefault(slot);duplicates[slot]=ordinal+1;
                var key=JsonSerializer.SerializeToUtf8Bytes(new[]{data.AccountId,data.Term.Id,meeting.CourseId,
                    originalDate.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),meeting.PeriodStart.ToString(CultureInfo.InvariantCulture),
                    meeting.PeriodEnd.ToString(CultureInfo.InvariantCulture),ordinal.ToString(CultureInfo.InvariantCulture)});
                var uid=Convert.ToHexString(SHA256.HashData(key)).ToLowerInvariant()+"@dlut-timetable.local";
                Line("BEGIN:VEVENT");Line("UID:"+uid);Line("DTSTAMP:"+timestamp);Line("LAST-MODIFIED:"+timestamp);
                Line("DTSTART:"+Utc(occurrence.Date,occurrence.Start));Line("DTEND:"+Utc(occurrence.Date,occurrence.End));
                Line("SUMMARY:"+Escape(meeting.Name));
                if(!string.IsNullOrEmpty(meeting.Location))Line("LOCATION:"+Escape(meeting.Location));
                var description=$"第 {week} 周 · 第 {meeting.PeriodStart}—{meeting.PeriodEnd} 节";
                if(meeting.TeacherNames.Length>0)description+="\n教师："+string.Join("、",meeting.TeacherNames);
                if(occurrence.HasConflict)description+="\n与其他课程存在时间冲突";
                Line("DESCRIPTION:"+Escape(description));Line("STATUS:CONFIRMED");Line("TRANSP:OPAQUE");Line("END:VEVENT");
            }
        }
        if(eventCount==0)throw new InvalidDataException("当前已获取范围没有可导出的课程安排。");
        Line("END:VCALENDAR");return output.ToArray();

        void Write(ReadOnlySpan<byte> bytes)
        {
            if(output.Length+bytes.Length>5*1024*1024)throw new InvalidDataException("日历导出超过 5 MiB 上限。");
            output.Write(bytes);
        }
        void Line(string line)
        {
            var octets=0;Span<byte> bytes=stackalloc byte[4];
            foreach(var rune in line.EnumerateRunes())
            {
                var size=rune.EncodeToUtf8(bytes);
                if(octets+size>75){Write("\r\n "u8);octets=1;}
                Write(bytes[..size]);octets+=size;
            }
            Write("\r\n"u8);
        }
    }
    private static string Utc(DateOnly date,TimeOnly time)
    {
        var local=date.ToDateTime(time,DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(local,SchoolCalendar.Shanghai).ToString("yyyyMMdd'T'HHmmss'Z'",CultureInfo.InvariantCulture);
    }
    private static string Escape(string value)
    {
        if(value.Any(c=>char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw new InvalidDataException("课程包含日历格式不支持的控制字符。");
        return value.Replace("\\","\\\\").Replace("\r\n","\n").Replace('\r','\n').Replace("\n","\\n").Replace(";","\\;").Replace(",","\\,");
    }
}
