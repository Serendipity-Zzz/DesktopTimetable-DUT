using System.Collections.Immutable;
using DlutTimetable.Domain;
using DlutTimetable.Infrastructure.Academic;

namespace DlutTimetable.Tests;

public sealed class AcademicScheduleMapperTests
{
    private static AcademicMappingContext Context()=>new("fictional-account",
        new("fictional-term","2026-2027","fictional-code",new(2026,8,31),"Asia/Shanghai",4),
        [new(1,new(8,0),new(8,45)),new(2,new(8,50),new(9,35))],new("TERM",[1,2,3,4]),
        AcademicWeekIndexBasis.OneBased,"fictional-source","academic-model-synthetic-v1",new(2026,10,1,0,0,0,TimeSpan.Zero),Synthetic:true);
    private static AcademicSchedule Row(int week=1)=>new("101","201",new DateOnly(2026,9,1).AddDays(7*(week-1)),2,week,
        new(8,0),new(9,35),2,new("虚构101室",null,null),null,"虚构教师甲");
    private static AcademicScheduleBatch Batch(params AcademicSchedule[] rows)=>new(
        [new("101","虚构课程",["虚构教师甲","虚构教师乙"]),new("102","虚构本批无排课课程",[])],rows.ToImmutableArray());
    [Fact]
    public void MapsExactTimesAcrossBreaksPreservesDiscreteWeeksAndDeduplicatesSourceRows()
    {
        var mapped=AcademicScheduleMapper.Map(Batch(Row(1),Row(3),Row(1)),Context());
        var meeting=Assert.Single(mapped.Dataset.Value.Meetings);
        Assert.Equal([1,3],meeting.Weeks.ToArray());Assert.Equal(1,meeting.PeriodStart);Assert.Equal(2,meeting.PeriodEnd);
        Assert.Equal(2,meeting.TeacherNames.Length);Assert.Equal(["102"],mapped.LessonIdsWithoutSchedules.ToArray());
        Assert.Empty(TimetableProjector.Week(mapped.Dataset,2).Occurrences);
        Assert.Equal(new DateOnly(2026,9,15),Assert.Single(TimetableProjector.Week(mapped.Dataset,3).Occurrences).Date);
        Assert.True(mapped.Dataset.Value.Synthetic);
    }
    [Fact]
    public void PlaceChangesStaySeparateAndAllTeachersRemainConsistentPerCourse()
    {
        var mapped=AcademicScheduleMapper.Map(Batch(Row(),Row(3) with{Room=null,CustomPlace="虚构室外地点",PersonName="虚构教师丙"}),Context());
        Assert.Equal(2,mapped.Dataset.Value.Meetings.Length);
        Assert.Contains(mapped.Dataset.Value.Meetings,m=>m.Location=="虚构室外地点" && m.Weeks.SequenceEqual([3]));
        Assert.All(mapped.Dataset.Value.Meetings,m=>Assert.Equal(3,m.TeacherNames.Length));
        Assert.Single(mapped.Dataset.Value.GetCourses());
    }
    [Fact]
    public void MeetingIdsRemainStableAcrossSourceOrderWeeksAndTeacherMetadataChanges()
    {
        var first=AcademicScheduleMapper.Map(Batch(Row(),Row(3)),Context());
        var reversed=AcademicScheduleMapper.Map(Batch(Row(3),Row()),Context());
        var changed=AcademicScheduleMapper.Map(Batch(Row(3) with{PersonName="虚构教师丙"}),Context());
        Assert.Equal(first.Dataset.Value.Meetings[0].Id,reversed.Dataset.Value.Meetings[0].Id);
        Assert.Equal(first.Dataset.Value.Meetings[0].Id,changed.Dataset.Value.Meetings[0].Id);
        Assert.DoesNotContain("虚构",changed.Dataset.Value.Meetings[0].Id);
    }
    [Fact]
    public void SourceWeekIndexBasisMustBeExplicitAndConsistentWithActualDate()
    {
        var error=Assert.Throws<AcademicMappingException>(()=>AcademicScheduleMapper.Map(Batch(Row()),Context() with{WeekIndexBasis=AcademicWeekIndexBasis.Unconfirmed}));
        Assert.Equal(AcademicMappingFailure.UnconfirmedWeekIndex,error.Failure);
        Assert.Single(AcademicScheduleMapper.Map(Batch(Row() with{SourceWeekIndex=0}),Context() with{WeekIndexBasis=AcademicWeekIndexBasis.ZeroBased}).Dataset.Value.Meetings);
        error=Assert.Throws<AcademicMappingException>(()=>AcademicScheduleMapper.Map(Batch(Row() with{SourceWeekIndex=0}),Context()));
        Assert.Equal(AcademicMappingFailure.WeekIndexMismatch,error.Failure);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void PeriodCountCannotBeMistakenForStartingPeriod(int count)
    {
        var error=Assert.Throws<AcademicMappingException>(()=>AcademicScheduleMapper.Map(Batch(Row() with{PeriodCount=count}),Context()));
        Assert.Equal(AcademicMappingFailure.PeriodCountMismatch,error.Failure);
    }
    [Fact]
    public void TimesBetweenPeriodBoundariesAreRejectedRatherThanRounded()
    {
        var error=Assert.Throws<AcademicMappingException>(()=>AcademicScheduleMapper.Map(Batch(Row() with{Start=new(8,5)}),Context()));
        Assert.Equal(AcademicMappingFailure.UnalignedTime,error.Failure);
    }
    [Fact]
    public void DayAndWeekCoverageCannotSilentlyTrimOutOfRangeSourceRows()
    {
        var weekContext=Context() with{Coverage=new("WEEK",[1])};
        var error=Assert.Throws<AcademicMappingException>(()=>AcademicScheduleMapper.Map(Batch(Row(),Row(3)),weekContext));
        Assert.Equal(AcademicMappingFailure.OutsideCoverage,error.Failure);
        var dayContext=Context() with{Coverage=new("DAY",[1],new(2026,9,2))};
        error=Assert.Throws<AcademicMappingException>(()=>AcademicScheduleMapper.Map(Batch(Row()),dayContext));
        Assert.Equal(AcademicMappingFailure.OutsideCoverage,error.Failure);
    }
    [Fact]
    public void EmptySourceNeedsVerifiedEmptyFromConfirmedSourceScope()
    {
        Assert.Throws<DatasetValidationException>(()=>AcademicScheduleMapper.Map(Batch(),Context()));
        var mapped=AcademicScheduleMapper.Map(Batch(),Context() with{VerifiedEmpty=true,Coverage=new("WEEK",[1])});
        Assert.True(mapped.Dataset.Value.VerifiedEmpty);Assert.Equal("WEEK",mapped.Dataset.Value.Coverage.Kind);
        Assert.Equal(2,mapped.LessonIdsWithoutSchedules.Length);
    }
    [Fact]
    public void InvalidContextAndUnknownCourseReferencesCannotCreateAValidatedDataset()
    {
        Assert.Throws<DatasetValidationException>(()=>AcademicScheduleMapper.Map(Batch(Row()),Context() with{Periods=[]}));
        Assert.Throws<DatasetValidationException>(()=>AcademicScheduleMapper.Map(Batch(Row()),Context() with{Term=Context().Term with{FirstTeachingMonday=new(2026,9,1)}}));
        var error=Assert.Throws<AcademicMappingException>(()=>AcademicScheduleMapper.Map(Batch(Row() with{LessonId="999"}),Context()));
        Assert.Equal(AcademicMappingFailure.InvalidBatch,error.Failure);
    }
}
