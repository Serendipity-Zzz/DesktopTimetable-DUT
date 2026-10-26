using System.Collections.Immutable;
using DlutTimetable.Domain;

namespace DlutTimetable.Tests;

public sealed class CalendarAdjustmentTests
{
    private static ValidatedDataset Source()=>ValidatedDataset.Create(Samples.Dataset);
    private static CalendarAdjustment Adjust(ValidatedDataset source)=>new(source.Value.AccountId,source.Value.Term.Id,
        source.Value.Term.FirstTeachingMonday.AddDays(7),20,source.Value.Periods);
    [Fact]
    public void LocalCalendarChangesProjectionAndLeavesTheOfficialDatasetIntact()
    {
        var source=Source();var adjustment=Adjust(source);
        var original=TimetableProjector.Week(source,1);var local=TimetableProjector.Week(source,1,adjustment);
        Assert.Equal(original.Occurrences[0].Date.AddDays(7),local.Occurrences[0].Date);
        Assert.Equal(new DateOnly(2026,8,31),source.Value.Term.FirstTeachingMonday);Assert.Equal(18,source.Value.Term.WeekCount);
        Assert.Equal(CoverageStatus.NotFetched,TimetableProjector.Week(source,19,adjustment).Coverage);
        Assert.Equal(CoverageStatus.Complete,TimetableProjector.Week(source,18,adjustment).Coverage);
    }
    [Fact]
    public void NewTimesDriveOccurrencesAndTodaysStatus()
    {
        var source=Source();var periods=source.Value.Periods.Take(2).Select(p=>p with{Start=p.Start.AddMinutes(10),End=p.End.AddMinutes(10)}).ToImmutableArray();
        var adjustment=Adjust(source) with{Periods=periods};
        var occurrence=Assert.Single(TimetableProjector.Week(source,1,adjustment).Occurrences);
        Assert.Equal(periods[0].Start,occurrence.Start);Assert.Equal(periods[1].End,occurrence.End);
        Assert.Equal(TodayScheduleStatus.Upcoming,TimetableProjector.Today(source,new(2026,9,8,8,5,0,TimeSpan.FromHours(8)),adjustment).Status);
    }
    [Theory]
    [InlineData("account")]
    [InlineData("term")]
    [InlineData("monday")]
    [InlineData("weeks")]
    [InlineData("periods")]
    public void InvalidOrForeignSettingsCannotHideCourses(string invalid)
    {
        var source=Source();var value=Adjust(source);
        value=invalid switch{
            "account"=>value with{AccountId="other-account"},"term"=>value with{TermId="other-term"},
            "monday"=>value with{FirstTeachingMonday=value.FirstTeachingMonday.AddDays(1)},
            "weeks"=>value with{WeekCount=7},_=>value with{Periods=[source.Value.Periods[0]]}};
        Assert.Throws<DatasetValidationException>(()=>TimetableProjector.Week(source,1,value));
    }
    [Fact]
    public void DatedSourceKeepsItsActualDateAndMovesToTheCorrectDisplayedWeek()
    {
        var source=ValidatedDataset.Create(Samples.Dataset with{Coverage=new("DAY",[1],new(2026,9,6)),
            Meetings=[Samples.Dataset.Meetings[0] with{Weekday=7,Weeks=[1]}]});
        var value=Adjust(source) with{FirstTeachingMonday=new(2026,8,24)};
        Assert.Equal(CoverageStatus.NotFetched,TimetableProjector.Week(source,1,value).Coverage);
        Assert.Equal(new DateOnly(2026,9,6),Assert.Single(TimetableProjector.Week(source,2,value).Occurrences).Date);
        Assert.Throws<DatasetValidationException>(()=>TimetableProjector.Week(source,1,Adjust(source)));
    }
    [Fact]
    public void MalformedPeriodPreferenceIsRejectedWithoutThrowingDuringValidation()
    {var value=Adjust(Source()) with{Periods=default};Assert.NotEmpty(value.Errors(Source().Value));}
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OverlappingAndReversedTimesCannotBeApplied(bool overlap)
    {
        var source=Source();var first=source.Value.Periods[0];var second=source.Value.Periods[1];
        var periods=overlap?ImmutableArray.Create(first,second with{Start=new(8,30)}):ImmutableArray.Create(first with{Start=new(9,0)},second);
        Assert.Throws<DatasetValidationException>(()=>TimetableProjector.Week(source,1,Adjust(source) with{Periods=periods}));
    }
}
