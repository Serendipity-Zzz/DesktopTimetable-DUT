using System.Collections.Immutable;

namespace DlutTimetable.Domain;

public sealed record Term(string Id, string SchoolYear, string TermCode,
    DateOnly FirstTeachingMonday, string Timezone, int WeekCount);
public sealed record Period(int Index, TimeOnly Start, TimeOnly End);
public sealed record Course(string Id, string Name, ImmutableArray<string> TeacherNames);
public sealed record Meeting(string Id, string CourseId, string Name, ImmutableArray<string> TeacherNames,
    string? Location, int Weekday, int PeriodStart, int PeriodEnd, ImmutableArray<int> Weeks);
public sealed record Coverage(string Kind, ImmutableArray<int> Weeks, DateOnly? Date = null);
public sealed record CurrentDataset(string SchemaVersion, bool Synthetic, string AccountId,
    string AdapterVersion, string Source, Term Term, Coverage Coverage, DateTimeOffset FetchedAt,
    ImmutableArray<Period> Periods, ImmutableArray<Meeting> Meetings, string? Notes = null,
    bool VerifiedEmpty = false)
{
    public IEnumerable<Course> GetCourses() => Meetings.GroupBy(m => m.CourseId)
        .Select(g => new Course(g.Key, g.First().Name, g.First().TeacherNames));
}

public sealed class DatasetValidationException(IEnumerable<string> errors)
    : Exception("课表数据未通过校验：" + string.Join("；", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors.ToArray();
}

// Immutable records and arrays prevent a caller mutating a batch after validation.
public sealed class ValidatedDataset
{
    public CurrentDataset Value { get; }
    private ValidatedDataset(CurrentDataset value) => Value = value;
    public static ValidatedDataset Create(CurrentDataset data, string? expectedAccount = null, string? expectedTerm = null)
    {
        var errors = DatasetValidator.Validate(data, expectedAccount, expectedTerm);
        if (errors.Count != 0) throw new DatasetValidationException(errors);
        return new(data);
    }
}

public static class DatasetValidator
{
    public static IReadOnlyList<string> Validate(CurrentDataset data, string? expectedAccount = null, string? expectedTerm = null)
    {
        var errors = new List<string>();
        void Require(bool condition, string message) { if (!condition) errors.Add(message); }
        Require(data.SchemaVersion == "1.0", "不支持的 schemaVersion");
        Require(!string.IsNullOrWhiteSpace(data.AccountId), "缺少账号标识");
        Require(expectedAccount is null || data.AccountId == expectedAccount, "账号不匹配");
        Require(!string.IsNullOrWhiteSpace(data.Source) && !string.IsNullOrWhiteSpace(data.AdapterVersion), "缺少来源/适配器版本");
        Require(data.FetchedAt != default, "缺少获取时间");
        if (data.Term is null || data.Coverage is null) { errors.Add("缺少学期/覆盖范围"); return errors; }
        var term = data.Term;
        Require(!string.IsNullOrWhiteSpace(term.Id) && !string.IsNullOrWhiteSpace(term.SchoolYear)
            && !string.IsNullOrWhiteSpace(term.TermCode), "缺少学期参数");
        Require(expectedTerm is null || term.Id == expectedTerm, "学期不匹配");
        Require(term.Timezone == "Asia/Shanghai", "时区必须为 Asia/Shanghai");
        Require(term.FirstTeachingMonday != default && term.FirstTeachingMonday.DayOfWeek == DayOfWeek.Monday,
            "第一教学日必须是已确认的周一");
        Require(term.WeekCount is >= 1 and <= 60, "教学周数必须在 1—60 内");
        Require(term.FirstTeachingMonday.DayNumber <= DateOnly.MaxValue.DayNumber - 420, "学期日期溢出");
        var coverage = data.Coverage;
        Require(coverage.Kind is "TERM" or "WEEK" or "DAY", "未知覆盖范围类型");
        if (coverage.Weeks.IsDefaultOrEmpty) errors.Add("覆盖周集合不能为空");
        else
        {
            Require(coverage.Weeks.Distinct().Count() == coverage.Weeks.Length, "覆盖周重复");
            Require(coverage.Weeks.All(w => w >= 1 && w <= term.WeekCount), "覆盖周越界");
            if (coverage.Kind == "TERM") Require(coverage.Weeks.Length == term.WeekCount, "完整学期覆盖不完整");
            if (coverage.Kind is "WEEK" or "DAY") Require(coverage.Weeks.Length == 1, "周/日范围只能覆盖一周");
        }
        if (coverage.Kind == "DAY")
        {
            Require(coverage.Date.HasValue, "日覆盖缺少日期");
            if (coverage.Date is { } date && !coverage.Weeks.IsDefaultOrEmpty)
                Require(coverage.Weeks.Contains(SchoolCalendar.TeachingWeek(term, date)), "日覆盖日期与周次不符");
        }
        else Require(coverage.Date is null, "非日覆盖不得携带日期");
        if (data.Periods.IsDefaultOrEmpty || data.Periods.Any(p => p is null))
            errors.Add("节次表缺失");
        else
        {
            var periods = data.Periods.OrderBy(p => p.Index).ToArray();
            Require(periods.Length <= 24, "节次数超出上限");
            Require(periods.Select(p => p.Index).SequenceEqual(Enumerable.Range(1, periods.Length)), "节次必须从 1 连续编号");
            Require(periods.All(p => p.Start < p.End), "节次结束必须晚于开始");
            Require(periods.Zip(periods.Skip(1)).All(p => p.First.End <= p.Second.Start), "节次时间重叠或逆序");
        }
        if (data.Meetings.IsDefault) { errors.Add("缺少 meetings"); return errors; }
        Require(data.Meetings.Length <= 10000, "课程记录超出上限");
        Require(data.Meetings.Length != 0 || data.VerifiedEmpty, "空课表必须明确 verifiedEmpty");
        Require(data.Meetings.Length == 0 || !data.VerifiedEmpty, "非空课表不得标为 verifiedEmpty");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var courses = new Dictionary<string, Meeting>(StringComparer.Ordinal);
        foreach (var m in data.Meetings)
        {
            if (m is null) { errors.Add("课程记录为空"); continue; }
            Require(!string.IsNullOrWhiteSpace(m.Id) && ids.Add(m.Id), "课程记录 ID 缺失/重复");
            Require(!string.IsNullOrWhiteSpace(m.CourseId) && !string.IsNullOrWhiteSpace(m.Name), "缺少课程标识/名称");
            Require(m.Name is { Length: <= 512 } && m.Location is not { Length: > 512 }, "课程文本超长");
            Require(!m.TeacherNames.IsDefault && m.TeacherNames.All(t => t is { Length: <= 256 }), "教师列表无效");
            Require(m.Weekday is >= 1 and <= 7, "星期必须为 1—7");
            Require(m.PeriodStart >= 1 && m.PeriodEnd >= m.PeriodStart && !data.Periods.IsDefault
                && m.PeriodEnd <= data.Periods.Length, "课程节次越界");
            if (m.Weeks.IsDefaultOrEmpty) errors.Add("课程周集合为空");
            else
            {
                Require(m.Weeks.Distinct().Count() == m.Weeks.Length, "课程周次重复");
                Require(m.Weeks.All(w => w >= 1 && w <= term.WeekCount && !coverage.Weeks.IsDefault
                    && coverage.Weeks.Contains(w)), "课程周次不在覆盖范围内");
            }
            if (coverage.Kind == "DAY" && coverage.Date is { } day)
                Require(m.Weekday == SchoolCalendar.Weekday(day), "课程不在覆盖日期内");
            if (!string.IsNullOrWhiteSpace(m.CourseId))
            {
                if (courses.TryGetValue(m.CourseId, out var prior))
                    Require(prior.Name == m.Name && !prior.TeacherNames.IsDefault && !m.TeacherNames.IsDefault
                        && prior.TeacherNames.SequenceEqual(m.TeacherNames), "相同课程 ID 的元数据冲突");
                else courses[m.CourseId] = m;
            }
        }
        return errors;
    }
}
