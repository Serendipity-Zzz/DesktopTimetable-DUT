using System.Collections.Immutable;
using DlutTimetable.Domain;
using DlutTimetable.Infrastructure.Academic;

namespace DlutTimetable.Tests;

public sealed class AcademicPeriodTableTests
{
    private static byte[] Observed()=>File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"fixtures/academic-observed-periods.json"));
    [Fact]
    public void ReadsAllTwelveObservedPeriodsWithAfternoonAndEveningBoundaries()
    {
        var table=AcademicPeriodTableReader.Read(Observed());
        Assert.Equal(12,table.Length);Assert.Equal(new TimeOnly(13,30),table[4].Start);
        Assert.Equal(new TimeOnly(18,0),table[8].Start);Assert.Equal(new TimeOnly(21,15),table[11].End);
    }
    [Fact]
    public void ObservedNightRangeMapsToPeriodsNineThroughTwelveUnderFictionalContext()
    {
        var table=AcademicPeriodTableReader.Read(Observed());
        var context=new AcademicMappingContext("fictional-account",
            new("fictional-term","2026-2027","fictional-code",new(2026,8,31),"Asia/Shanghai",4),table,
            new("WEEK",[1]),AcademicWeekIndexBasis.OneBased,"fictional-source","observed-time-synthetic-context-v1",
            new(2026,10,24,0,0,0,TimeSpan.Zero),Synthetic:true);
        var batch=new AcademicScheduleBatch([new("101","虚构课程",[])],
            [new("101","201",new(2026,9,1),2,1,new(18,0),new(21,15),4,null,null,null)]);
        var mapped=AcademicScheduleMapper.Map(batch,context);var meeting=Assert.Single(mapped.Dataset.Value.Meetings);
        Assert.Equal(9,meeting.PeriodStart);Assert.Equal(12,meeting.PeriodEnd);Assert.True(mapped.Dataset.Value.Synthetic);
        var error=Assert.Throws<AcademicMappingException>(()=>AcademicScheduleMapper.Map(batch,context with{Periods=table.Take(2).ToImmutableArray()}));
        Assert.Equal(AcademicMappingFailure.UnalignedTime,error.Failure);
    }
    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"indexNo\":1,\"startTime\":800,\"endTime\":865}]")]
    public void DoesNotSubstituteDefaultTimesForInvalidEvidence(string json)
        =>Assert.Throws<AcademicReadException>(()=>AcademicPeriodTableReader.Read(System.Text.Encoding.UTF8.GetBytes(json)));
}
