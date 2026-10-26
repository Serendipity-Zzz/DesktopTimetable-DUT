using System.Text;
using System.Text.Json.Nodes;
using DlutTimetable.Domain;
using DlutTimetable.Infrastructure;
using DlutTimetable.Infrastructure.Academic;
using DlutTimetable.Contracts;

namespace DlutTimetable.Tests;

public sealed class AcademicBrowserImportTests
{
    [Fact]
    public async Task BootstrapCredentialsAndStudentNumberNeverEnterNormalizedWorkerPayload()
    {
        var import=Ready(configuration:"""{"weekIndices":[1,2,3,4],"lessonIds":[101],"studentNo":"20250000001","cookie":"PrivateCookieSecret","token":"PrivateTokenSecret"}""");
        var dataset=import.Build();var normalized=Encoding.UTF8.GetString(DatasetJson.Serialize(dataset));
        var request=new WorkerRequest(WorkerProtocol.Version,Guid.NewGuid(),new(1),new string('A',64),WorkerTaskKind.InteractiveLogin,SchoolNavigation.Hosts.ToArray(),DateTimeOffset.UtcNow.AddMinutes(1));
        var response=new WorkerResponse(WorkerProtocol.Version,request.RequestId,request.Generation,request.Nonce,WorkerStatus.Completed,normalized);
        WorkerWire.Validate(response,request,request.Generation);
        using var wire=new MemoryStream();await WorkerWire.WriteAsync(wire,response);
        var payload=Encoding.UTF8.GetString(wire.ToArray());
        Assert.DoesNotContain("20250000001",payload);Assert.DoesNotContain("PrivateCookieSecret",payload);Assert.DoesNotContain("PrivateTokenSecret",payload);
        Assert.StartsWith("dlut-",dataset.Value.AccountId);Assert.NotEmpty(dataset.Value.Meetings);
    }
    private static void Feed(AcademicBrowserImport import,string path,string value)=>import.Accept(new("http://jxgl.dlut.edu.cn"+path),Encoding.UTF8.GetBytes(value));
    private static AcademicBrowserImport Ready(string secondDate="2026-09-15",string? metadata=null,string? configuration=null)
    {
        var import=new AcademicBrowserImport();
        Feed(import,"/student/for-std/course-table/get-data?semesterId=12&bizTypeId=2",configuration??"{\"weekIndices\":[1,2,3,4],\"lessonIds\":[101]}");
        Feed(import,"/student/ws/schedule-table/timetable-layout","""{"result":{"units":[{"indexNo":1,"startTime":800,"endTime":845},{"indexNo":2,"startTime":850,"endTime":935}]}}""");
        var body="""
        {"result":{"lessons":[{"id":101,"courseName":"虚构课程","teacherAssignmentList":[{"name":"虚构教师"}]}],"schedules":[
        {"lessonId":101,"date":"2026-09-01","weekday":2,"weekIndex":1,"startTime":800,"endTime":935,"periods":2,"customPlace":"虚构教室"},
        {"lessonId":101,"date":"DATE","weekday":2,"weekIndex":3,"startTime":800,"endTime":935,"periods":2,"customPlace":"虚构教室"}]}}
        """.Replace("DATE",secondDate);
        if(metadata is not null)
        {
            var node=JsonNode.Parse(body)!;
            foreach(var property in JsonNode.Parse(metadata)!.AsObject())node[property.Key]=property.Value?.DeepClone();
            body=node.ToJsonString();
        }
        Feed(import,"/student/ws/schedule-table/datum",body);
        return import;
    }
    [Fact]
    public async Task SchoolResponsesProducePersistedDatasetAndNonemptyWeekProjection()
    {
        var value=Ready().Build("fictional-student");
        Assert.Equal(new DateOnly(2026,8,31),value.Value.Term.FirstTeachingMonday);
        Assert.Equal(4,value.Value.Term.WeekCount);
        Assert.Equal([1,3],Assert.Single(value.Value.Meetings).Weeks.ToArray());
        var root=Path.Combine(Path.GetTempPath(),"dlut-import-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store=new SqliteStore(Path.Combine(root,"cache.db"));
            await store.ReplaceAsync(DatasetJson.Parse(DatasetJson.Serialize(value)));
            var saved=Assert.IsType<ValidatedDataset>(await store.ReadAsync());
            Assert.Equal("虚构课程",Assert.Single(TimetableProjector.Week(saved,3).Occurrences).Meeting.Name);
            Assert.Empty(TimetableProjector.Week(saved,2).Occurrences);
        }
        finally {Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
    }
    [Fact]
    public void RejectsInconsistentCalendarAndDoesNotDropOffendingRow()
    {Assert.Throws<InvalidDataException>(()=>Ready("2026-09-22").Build("fictional-student"));}
    [Fact]
    public void AccountKeysAreStableAndSeparate()
    {
        Assert.Equal(Ready().Build("fictional-student").Value.AccountId,Ready().Build("fictional-student").Value.AccountId);
        Assert.NotEqual(Ready().Build("fictional-student").Value.AccountId,Ready().Build("fictional-other").Value.AccountId);
        Assert.Throws<InvalidDataException>(()=>Ready().Build());
    }
    [Fact]
    public void AcceptsOnlyStudentBootstrapForAutomaticIdentity()
    {
        var import=Ready();
        Feed(import,"/student/for-std/course-table","<script>var unrelated={\"code\":\"12345678901\"};</script>");
        Assert.Null(import.StudentIdentifier);
        Feed(import,"/student/for-std/course-table","<script>var student={\"id\":123,\"code\":\"12345678901\"};</script>");
        Assert.Equal("12345678901",import.StudentIdentifier);
        Assert.Single(import.Build().Value.Meetings);
    }
    [Fact]
    public void NewSemesterCannotReuseOldSchedules()
    {
        var import=Ready();
        Feed(import,"/student/for-std/course-table/get-data?semesterId=13","{}");
        Assert.False(import.HasResponses);
        Assert.Throws<InvalidDataException>(()=>import.Build("fictional-student"));
    }
    [Fact]
    public void ConfirmedIdentityCannotOverrideDifferentSchoolIdentity()
    {
        var import=Ready();
        Feed(import,"/student/for-std/course-table","<script>var student={\"code\":\"12345678901\"};</script>");
        Assert.Throws<InvalidDataException>(()=>import.Build("98765432100"));
    }
    [Fact]
    public void ResetDropsEntirePreviousCandidateAndIdentity()
    {
        var import=Ready();import.Reset();
        Assert.False(import.HasResponses);Assert.Null(import.StudentIdentifier);
        Assert.Throws<InvalidDataException>(()=>import.Build("fictional-student"));
    }
    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"unexpected\"")]
    public void InvalidConfigurationRootCannotCrashTheWindow(string body)
    {Assert.Throws<InvalidDataException>(()=>Feed(new(),"/student/for-std/course-table/get-data?semesterId=1",body));}
    [Fact]
    public void AmbiguousSemesterAndMissingWholeTermRangeCannotProduceFullTermDataset()
    {
        var import=Ready();
        Feed(import,"/student/for-std/course-table/get-data?semesterId=12&semesterId=13","{}");
        Assert.False(import.HasResponses);
        import=Ready();
        Feed(import,"/student/for-std/course-table/get-data?semesterId=12","{\"weeks\":[\"一\",\"二\"]}");
        Feed(import,"/student/ws/schedule-table/timetable-layout","""{"units":[{"indexNo":1,"startTime":800,"endTime":935}]}""");
        Feed(import,"/student/ws/schedule-table/datum","""{"lessons":[{"id":101,"courseName":"虚构课程","teacherAssignmentList":[]}],"schedules":[{"lessonId":101,"date":"2026-09-01","weekday":2,"weekIndex":1,"startTime":800,"endTime":935,"periods":1}]}""");
        Assert.Throws<InvalidDataException>(()=>import.Build("fictional-student"));
    }
    [Theory]
    [InlineData("{\"hasMore\":true}")]
    [InlineData("{\"hasNextPage\":true}")]
    [InlineData("{\"nextPage\":2}")]
    [InlineData("{\"nextPageUrl\":\"/unverified-next\"}")]
    [InlineData("{\"nextCursor\":\"unverified-cursor\"}")]
    [InlineData("{\"pagination\":{\"totalPages\":2,\"currentPage\":1}}")]
    [InlineData("{\"pageInfo\":{\"hasNextPage\":true}}")]
    [InlineData("{\"pageNumber\":3}")]
    [InlineData("{\"pagination\":{\"offset\":20,\"limit\":20}}")]
    [InlineData("{\"meta\":{\"pagination\":{\"offset\":0,\"limit\":20,\"total\":205}}}")]
    [InlineData("{\"pageInfo\":{\"endCursor\":\"last-page\",\"hasNextPage\":false}}")]
    [InlineData("{\"success\":false}")]
    public void PartialOrFailedResponseCannotBecomeACompleteTerm(string metadata)
    {Assert.Throws<InvalidDataException>(()=>Ready(metadata:metadata).Build("fictional-student"));}
    [Fact]
    public void CompletePageAndDeclaredCountsMustMatchTheWholeBatch()
    {
        Assert.Single(Ready(metadata:"{\"pagination\":{\"totalPages\":1,\"currentPage\":1},\"hasMore\":false,\"totalSchedules\":2}").Build("fictional-student").Value.Meetings);
        Assert.Throws<InvalidDataException>(()=>Ready(metadata:"{\"totalSchedules\":3}").Build("fictional-student"));
    }
    [Fact]
    public void EverySelectedLessonMustBeReturnedIncludingUnarrangedLessons()
    {
        var import=Ready();
        Feed(import,"/student/for-std/course-table/get-data?semesterId=12","{\"weekIndices\":[1,2,3,4],\"lessonIds\":[101,102]}");
        Feed(import,"/student/ws/schedule-table/timetable-layout","""{"units":[{"indexNo":1,"startTime":800,"endTime":935}]}""");
        Feed(import,"/student/ws/schedule-table/datum","""{"lessons":[{"id":101,"courseName":"虚构课程","teacherAssignmentList":[]}],"schedules":[{"lessonId":101,"date":"2026-09-01","weekday":2,"weekIndex":1,"startTime":800,"endTime":935,"periods":1}]}""");
        Assert.Throws<InvalidDataException>(()=>import.Build("fictional-student"));
    }
    [Fact]
    public void ObservedConfigurationLessonObjectsAlsoConfirmTheSourceScope()
    {
        var import=Ready();
        // Equivalent to the observed get-data lesson array; all values are fictional.
        Feed(import,"/student/for-std/course-table/get-data?semesterId=12","""{"weekIndices":[1,2,3,4],"selected":[{"id":101,"teacherAssignmentList":[],"course":{}}]}""");
        Feed(import,"/student/ws/schedule-table/timetable-layout","""{"units":[{"indexNo":1,"startTime":800,"endTime":935}]}""");
        Feed(import,"/student/ws/schedule-table/datum","""{"lessons":[{"id":101,"courseName":"虚构课程","teacherAssignmentList":[]}],"schedules":[{"lessonId":101,"date":"2026-09-01","weekday":2,"weekIndex":1,"startTime":800,"endTime":935,"periods":1}]}""");
        Assert.Single(import.Build("fictional-student").Value.Meetings);
    }
    [Fact]
    public void DuplicateEnvelopeKeysAreRejectedBeforeTheirValuesCanReplaceScope()
    {
        Assert.Throws<AcademicReadException>(()=>Feed(new(),"/student/for-std/course-table/get-data?semesterId=12","{\"lessonIds\":[101],\"lessonIds\":[102]}"));
    }
    [Fact]
    public void FailedReplacementCannotLeaveAnOlderCandidateImportable()
    {
        var import=Ready();Assert.True(import.HasResponses);
        Assert.Throws<InvalidDataException>(()=>Feed(import,"/student/ws/schedule-table/datum","{\"hasMore\":true}"));
        Assert.False(import.HasResponses);Assert.Throws<InvalidDataException>(()=>import.Build("fictional-student"));
    }
}
