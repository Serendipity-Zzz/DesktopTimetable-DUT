using DlutTimetable.Domain;

namespace DlutTimetable.Tests;

public sealed class ProjectionBoundaryTests
{
    [Theory]
    [InlineData(2026,9,1,7,59,TodayScheduleStatus.Upcoming)]
    [InlineData(2026,9,1,8,0,TodayScheduleStatus.InProgress)]
    [InlineData(2026,9,1,9,45,TodayScheduleStatus.Finished)]
    [InlineData(2026,9,2,8,0,TodayScheduleStatus.NoClasses)]
    [InlineData(2026,8,30,8,0,TodayScheduleStatus.OutsideTerm)]
    public void TodayStatesUseTheActualShanghaiDay(int year,int month,int day,int hour,int minute,TodayScheduleStatus expected)
    {
        var instant=new DateTimeOffset(year,month,day,hour,minute,0,TimeSpan.FromHours(8));
        var view=TimetableProjector.Today(ValidatedDataset.Create(Samples.Dataset),instant);
        Assert.Equal(expected,view.Status);
        Assert.Equal(expected!=TodayScheduleStatus.OutsideTerm,view.IsCovered);
    }
    [Fact]
    public void ConfirmedEmptyWeekIsDifferentFromAnUnfetchedWeek()
    {
        var data=ValidatedDataset.Create(Samples.Dataset with {Coverage=new("WEEK",[2]),Meetings=[],VerifiedEmpty=true});
        var empty=TimetableProjector.Week(data,2);var unknown=TimetableProjector.Week(data,1);
        Assert.Equal(CoverageStatus.Complete,empty.Coverage);Assert.Empty(empty.Occurrences);
        Assert.Equal(CoverageStatus.NotFetched,unknown.Coverage);Assert.Empty(unknown.Occurrences);
        Assert.Equal(TodayScheduleStatus.NotFetched,TimetableProjector.Today(data,new(2026,9,1,8,0,0,TimeSpan.FromHours(8))).Status);
        Assert.Equal(TodayScheduleStatus.NoClasses,TimetableProjector.Today(data,new(2026,9,8,8,0,0,TimeSpan.FromHours(8))).Status);
    }
    [Fact]
    public void ExplicitSundaySourceDateIsNotReplacedByWeekdayOrHolidayGuessing()
    {
        var day=new DateOnly(2026,9,6);
        var data=ValidatedDataset.Create(Samples.Dataset with
        {
            Coverage=new("DAY",[1],day),
            Meetings=[Samples.Dataset.Meetings[0] with{Id="official-dated-sunday",Weekday=7,Weeks=[1]}]
        });
        var row=Assert.Single(TimetableProjector.Week(data,1).Occurrences);
        Assert.Equal(day,row.Date);Assert.Equal(7,SchoolCalendar.Weekday(row.Date));
        Assert.Equal(TodayScheduleStatus.NotFetched,TimetableProjector.Today(data,new(2026,9,1,8,0,0,TimeSpan.FromHours(8))).Status);
        Assert.Equal(TodayScheduleStatus.InProgress,TimetableProjector.Today(data,new(2026,9,6,8,0,0,TimeSpan.FromHours(8))).Status);
    }
    [Fact]
    public void LongNamesArePreservedForDetailsAndInvalidRequiredTextIsRejected()
    {
        var name="唯一识别标记"+new string('长',506);
        var source=Samples.Dataset;var meeting=source.Meetings[0] with{Name=name};
        Assert.Equal(512,name.Length);
        var result=TimetableProjector.Week(ValidatedDataset.Create(source with{Meetings=[meeting]}),1);
        Assert.Equal(name,Assert.Single(result.Occurrences).Meeting.Name);
        Assert.Throws<DatasetValidationException>(()=>ValidatedDataset.Create(source with{Meetings=[meeting with{Name=name+"长"}]}));
        Assert.Throws<DatasetValidationException>(()=>ValidatedDataset.Create(source with{Meetings=[meeting with{Name=null!}]}));
        Assert.Throws<DatasetValidationException>(()=>ValidatedDataset.Create(source with{Meetings=[meeting with{TeacherNames=default}]}));
    }
}
