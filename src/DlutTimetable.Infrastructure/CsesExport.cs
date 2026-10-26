using System.Globalization;
using System.Text;
using DlutTimetable.Domain;

namespace DlutTimetable.Infrastructure;

// CSES v1: SmartTeachCN/CSES commit 2622c3aa0da0d780d47911e1b6dfcdb7dac1de13.
// Term dates, arbitrary week sets and university exceptions are not representable.
public static class CsesExport
{
    private sealed record Subject(string CourseId,string Name,string Teacher,string Room);
    private sealed record Class(Subject Subject,int Day,string Weeks,TimeOnly Start,TimeOnly End);
    public static byte[] Create(ValidatedDataset source,CalendarAdjustment? calendar=null,int? week=null)
    {
        calendar?.ValidateFor(source);var data=source.Value;
        var term=calendar?.Term(data.Term)??data.Term;var periods=calendar?.Periods??data.Periods;
        static Subject Key(Meeting meeting)=>new(meeting.CourseId,meeting.Name,string.Join("、",meeting.TeacherNames),meeting.Location??"");
        List<Class> rows;
        if(week is {} selected)
        {
            var projected=TimetableProjector.Week(source,selected,calendar);
            if(projected.Coverage!=CoverageStatus.Complete)
                throw new InvalidDataException("只能导出已完整获取的一周；日覆盖或未获取范围不能生成完整周模板。");
            rows=projected.Occurrences.Select(o=>new Class(Key(o.Meeting),SchoolCalendar.Weekday(o.Date),"all",o.Start,o.End)).ToList();
        }
        else
        {
            if(data.Coverage.Kind!="TERM" || term.WeekCount!=data.Term.WeekCount)
                throw new InvalidDataException("周期导出需要完整学期覆盖；可改用已获取的当前周模板或 ICS。");
            var all=Enumerable.Range(1,term.WeekCount).ToArray();var odd=all.Where(w=>w%2==1).ToArray();var even=all.Where(w=>w%2==0).ToArray();
            rows=[];
            foreach(var meeting in data.Meetings)
            {
                var weeks=meeting.Weeks.Order().ToArray();
                var pattern=weeks.SequenceEqual(all)?"all":weeks.SequenceEqual(odd)?"odd":weeks.SequenceEqual(even)?"even":null;
                if(pattern is null)throw new InvalidDataException("存在不连续教学周，CSES v1 无法完整表达。请选择当前周模板或 ICS，未把它改成连续课程。");
                rows.Add(new(Key(meeting),meeting.Weekday,pattern,periods.Single(p=>p.Index==meeting.PeriodStart).Start,
                    periods.Single(p=>p.Index==meeting.PeriodEnd).End));
            }
        }
        var subjects=rows.Select(c=>c.Subject).Distinct().OrderBy(s=>s.Name,StringComparer.Ordinal).ThenBy(s=>s.CourseId,StringComparer.Ordinal)
            .ThenBy(s=>s.Teacher,StringComparer.Ordinal).ThenBy(s=>s.Room,StringComparer.Ordinal).ToArray();
        var names=new Dictionary<Subject,string>();var used=new HashSet<string>(StringComparer.Ordinal);
        foreach(var subject in subjects)
        {
            var name=subject.Name;var suffix=2;
            while(!used.Add(name))name=subject.Name+" · "+(suffix++).ToString(CultureInfo.InvariantCulture);
            names.Add(subject,name);
        }
        var text=new StringBuilder("# CSES v1：不包含学期日期；导入后请核对接收软件的校历。\n");
        text.AppendLine(week is null?"# 仅保留可准确表达的 all/odd/even 周期；不含私人备注和提醒。":"# 当前教学周快照，weeks=all；不保留原始离散周、日期或调课范围。");
        text.AppendLine("version: 1");
        if(subjects.Length==0)text.AppendLine("subjects: []");
        else
        {
            text.AppendLine("subjects:");
            foreach(var subject in subjects)
            {
                text.Append("  - name: ").AppendLine(Scalar(names[subject]));
                if(subject.Teacher.Length>0)text.Append("    teacher: ").AppendLine(Scalar(subject.Teacher));
                if(subject.Room.Length>0)text.Append("    room: ").AppendLine(Scalar(subject.Room));
                if(text.Length>5*1024*1024)throw new InvalidDataException("CSES 导出超过 5 MiB 上限。");
            }
        }
        var schedules=rows.GroupBy(r=>(r.Day,r.Weeks)).OrderBy(g=>g.Key.Day).ThenBy(g=>g.Key.Weeks,StringComparer.Ordinal).ToArray();
        if(schedules.Length==0)text.AppendLine("schedules: []");
        else
        {
            text.AppendLine("schedules:");
            foreach(var schedule in schedules)
            {
                text.Append("  - name: ").AppendLine(Scalar($"周{schedule.Key.Day}-{schedule.Key.Weeks}"));
                text.Append("    enable_day: ").AppendLine(schedule.Key.Day.ToString(CultureInfo.InvariantCulture));
                text.Append("    weeks: ").AppendLine(schedule.Key.Weeks);text.AppendLine("    classes:");
                foreach(var item in schedule.OrderBy(c=>c.Start).ThenBy(c=>c.End).ThenBy(c=>names[c.Subject],StringComparer.Ordinal))
                {
                    text.Append("      - subject: ").AppendLine(Scalar(names[item.Subject]));
                    text.Append("        start_time: ").AppendLine(Scalar(item.Start.ToString("HH:mm:ss",CultureInfo.InvariantCulture)));
                    text.Append("        end_time: ").AppendLine(Scalar(item.End.ToString("HH:mm:ss",CultureInfo.InvariantCulture)));
                    if(text.Length>5*1024*1024)throw new InvalidDataException("CSES 导出超过 5 MiB 上限。");
                }
            }
        }
        var content=new UTF8Encoding(false,true).GetBytes(text.ToString());
        if(content.Length>5*1024*1024)throw new InvalidDataException("CSES 导出超过 5 MiB 上限。");
        return content;
    }
    private static string Scalar(string value)
    {
        if(value.Any(c=>char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw new InvalidDataException("课程包含交换格式不支持的控制字符。");
        return "\""+value.Replace("\\","\\\\").Replace("\"","\\\"").Replace("\r","\\r").Replace("\n","\\n").Replace("\t","\\t")+"\"";
    }
}
