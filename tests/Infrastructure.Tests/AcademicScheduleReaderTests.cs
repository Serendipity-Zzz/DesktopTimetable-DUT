using System.Text;
using System.Text.Json.Nodes;
using DlutTimetable.Infrastructure.Academic;

namespace DlutTimetable.Tests;

public sealed class AcademicScheduleReaderTests
{
    private const string Lessons="""[{"id":101,"courseName":"虚构课程甲","teacherAssignmentList":[{"name":"虚构教师甲"},{"name":"虚构教师乙"}]},{"id":102,"courseName":"虚构未排课课程","teacherAssignmentList":[]}]""";
    private const string Schedule="""{"lessonId":101,"scheduleGroupId":201,"weekday":2,"weekIndex":0,"date":"2026-09-15","startTime":805,"endTime":945,"periods":2,"room":{"nameZh":"虚构101室","building":{"nameZh":"虚构教学楼","campus":{"nameZh":"虚构校区"}}},"customPlace":null,"personName":"虚构教师甲"}""";
    private static AcademicScheduleBatch Read(string lessons=Lessons,string? schedules=null)=>
        AcademicScheduleReader.Read(Encoding.UTF8.GetBytes(lessons),Encoding.UTF8.GetBytes(schedules??"["+Schedule+"]"));
    [Fact]
    public void KeepsUnarrangedLessonsAndReadsHhmmAsClockTime()
    {
        var result=Read();var schedule=Assert.Single(result.Schedules);
        Assert.Equal(2,result.Lessons.Length);Assert.Equal(["虚构教师甲","虚构教师乙"],result.Lessons[0].TeacherNames.ToArray());
        Assert.Equal("101",schedule.LessonId);Assert.Equal(new TimeOnly(8,5),schedule.Start);Assert.Equal(new TimeOnly(9,45),schedule.End);
        Assert.Equal(2,schedule.PeriodCount);Assert.Equal(0,schedule.SourceWeekIndex);
        Assert.Equal(new DateOnly(2026,9,15),schedule.Date);Assert.Equal("虚构校区",schedule.Room!.Campus);
        Assert.DoesNotContain(result.Schedules,item=>item.LessonId=="102");
    }
    [Fact]
    public void KeepsDiscreteDatesRoomChangesAndRepeatedSourceRowsWithoutInventingWeeklyRules()
    {
        var later=JsonNode.Parse(Schedule)!;later["date"]="2026-09-29";later["weekIndex"]=2;later["room"]=null;later["customPlace"]="虚构实践地点";
        var result=Read(schedules:"["+Schedule+","+later.ToJsonString()+","+Schedule+"]");
        Assert.Equal(3,result.Schedules.Length);Assert.Equal(new DateOnly(2026,9,29),result.Schedules[1].Date);
        Assert.Null(result.Schedules[1].Room);Assert.Equal("虚构实践地点",result.Schedules[1].CustomPlace);
        Assert.Equal(result.Schedules[0],result.Schedules[2]);
    }
    [Fact]
    public void EmptySourceArraysAreOnlyCandidatesWithoutClaimingVerifiedEmptyCoverage()
    {
        Assert.Empty(Read("[]","[]").Lessons);Assert.Empty(Read(schedules:"[]").Schedules);
    }
    [Theory]
    [InlineData("lessonId","999",AcademicReadFailure.UnknownLesson)]
    [InlineData("weekday","3",AcademicReadFailure.WeekdayMismatch)]
    [InlineData("date","\"2026-02-30\"",AcademicReadFailure.InvalidDate)]
    [InlineData("startTime","965",AcademicReadFailure.InvalidTime)]
    [InlineData("endTime","2400",AcademicReadFailure.InvalidTime)]
    [InlineData("endTime","805",AcademicReadFailure.InvalidTime)]
    [InlineData("startTime","\"08:05\"",AcademicReadFailure.InvalidTime)]
    [InlineData("periods","0",AcademicReadFailure.InvalidShape)]
    [InlineData("periods","25",AcademicReadFailure.InvalidShape)]
    [InlineData("lessonId","\"101\"",AcademicReadFailure.InvalidShape)]
    public void RejectsMalformedRowsWithFixedErrors(string field,string json,AcademicReadFailure failure)
    {
        var schedule=JsonNode.Parse(Schedule)!;schedule[field]=JsonNode.Parse(json);
        var error=Assert.Throws<AcademicReadException>(()=>Read(schedules:"["+schedule.ToJsonString()+"]"));
        Assert.Equal(failure,error.Failure);Assert.Equal(0,error.RecordIndex);
        Assert.DoesNotContain("虚构",error.Message);
    }
    [Theory]
    [InlineData("lessonId")]
    [InlineData("date")]
    [InlineData("startTime")]
    [InlineData("periods")]
    public void MissingRequiredFieldsCannotBecomeEmptySuccess(string field)
    {
        var schedule=JsonNode.Parse(Schedule)!.AsObject();schedule.Remove(field);
        Assert.Equal(AcademicReadFailure.InvalidShape,Assert.Throws<AcademicReadException>(()=>Read(schedules:"["+schedule.ToJsonString()+"]")).Failure);
    }
    [Fact]
    public void RejectsAmbiguousLessonIdentityAndDuplicateJsonFields()
    {
        var one=JsonNode.Parse(Lessons)![0]!.ToJsonString();
        Assert.Equal(AcademicReadFailure.DuplicateLesson,Assert.Throws<AcademicReadException>(()=>Read("["+one+","+one+"]")).Failure);
        var duplicate=Schedule.Replace("\"lessonId\":101","\"lessonId\":101,\"lessonId\":999",StringComparison.Ordinal);
        Assert.Equal(AcademicReadFailure.DuplicateField,Assert.Throws<AcademicReadException>(()=>Read(schedules:"["+duplicate+"]")).Failure);
    }
    [Fact]
    public void InvalidLaterRowRejectsTheWholeCandidateInsteadOfReturningTheFirstValidRow()
    {
        var invalid=Schedule.Replace("\"lessonId\":101","\"lessonId\":999",StringComparison.Ordinal);
        var error=Assert.Throws<AcademicReadException>(()=>Read(schedules:"["+Schedule+","+invalid+"]"));
        Assert.Equal(AcademicReadFailure.UnknownLesson,error.Failure);Assert.Equal(1,error.RecordIndex);
    }
    [Theory]
    [InlineData("null",AcademicReadFailure.InvalidShape)]
    [InlineData("{}",AcademicReadFailure.InvalidShape)]
    [InlineData("broken",AcademicReadFailure.InvalidJson)]
    [InlineData("[null]",AcademicReadFailure.InvalidShape)]
    public void RejectsFailureEnvelopesAndMalformedJson(string schedules,AcademicReadFailure failure)
        =>Assert.Equal(failure,Assert.Throws<AcademicReadException>(()=>Read(schedules:schedules)).Failure);
    [Fact]
    public void EnforcesRecordAndByteLimitsBeforeProducingCandidates()
    {
        var many="["+string.Join(',',Enumerable.Repeat("null",AcademicScheduleReader.MaximumRecords+1))+"]";
        Assert.Equal(AcademicReadFailure.TooLarge,Assert.Throws<AcademicReadException>(()=>Read(schedules:many)).Failure);
        Assert.Equal(AcademicReadFailure.TooLarge,Assert.Throws<AcademicReadException>(()=>
            AcademicScheduleReader.Read(new byte[5242881],"[]"u8.ToArray())).Failure);
    }
}
