using System.Collections.Immutable;

namespace DlutTimetable.Domain;

// A local presentation preference. The source dataset, coverage and fetch time stay intact.
public sealed record CalendarAdjustment(string AccountId,string TermId,DateOnly FirstTeachingMonday,int WeekCount,ImmutableArray<Period> Periods)
{
    public bool AppliesTo(CurrentDataset source)=>AccountId==source.AccountId && TermId==source.Term.Id;
    public IReadOnlyList<string> Errors(CurrentDataset? source=null)
    {
        var errors=new List<string>();
        if(string.IsNullOrWhiteSpace(AccountId) || string.IsNullOrWhiteSpace(TermId))errors.Add("缺少校历绑定信息");
        if(FirstTeachingMonday==default || FirstTeachingMonday.DayOfWeek!=DayOfWeek.Monday)errors.Add("第一教学日必须为周一");
        if(FirstTeachingMonday.DayNumber>DateOnly.MaxValue.DayNumber-420)errors.Add("学期日期超出范围");
        if(WeekCount is <1 or >60)errors.Add("教学周数须为 1—60");
        if(Periods.IsDefaultOrEmpty || Periods.Length>24 || Periods.Any(p=>p is null))errors.Add("节次须为 1—24 节");
        else
        {
            if(!Periods.Select(p=>p.Index).SequenceEqual(Enumerable.Range(1,Periods.Length)))errors.Add("节次须从 1 连续编号");
            if(Periods.Any(p=>p.Start>=p.End) || Periods.Zip(Periods.Skip(1)).Any(pair=>pair.First.End>pair.Second.Start))errors.Add("节次时间须先后有序且不能重叠");
        }
        if(source is not null)
        {
            if(!AppliesTo(source))errors.Add("校历与当前账号或学期不一致");
            if(source.Meetings.Any(m=>m.PeriodEnd>(Periods.IsDefault?0:Periods.Length)))errors.Add("节次数不能少于现有课程使用的节次");
            if(source.Coverage.Kind=="DAY")
            {
                var week=SchoolCalendar.TeachingWeek(Term(source.Term),source.Coverage.Date!.Value);
                if(week<1 || week>WeekCount)errors.Add("修改后明确日期的课程将落在学期之外");
            }
            else if(source.Meetings.Any(m=>m.Weeks.Any(w=>w>WeekCount)))errors.Add("教学周数不能截掉已有课程");
        }
        return errors;
    }
    public void ValidateFor(ValidatedDataset source)
    {var errors=Errors(source.Value);if(errors.Count>0)throw new DatasetValidationException(errors);}
    public Term Term(Term source)=>source with{FirstTeachingMonday=FirstTeachingMonday,WeekCount=WeekCount};
}
