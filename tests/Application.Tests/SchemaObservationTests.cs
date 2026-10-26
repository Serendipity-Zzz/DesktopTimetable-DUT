using System.Text;
using System.Text.Json;
using DlutTimetable.Contracts;

namespace DlutTimetable.Tests;

public sealed class SchemaObservationTests
{
    [Fact]
    public void ScriptSelectionKeepsLateEntryModulesAndCapsCanonicalPaths()
    {
        var scripts=Enumerable.Range(0,80).Select(index=>$"http://jxgl.dlut.edu.cn/student/static/scheduletable/model{index}.js")
            .Append("http://jxgl.dlut.edu.cn/student/static/business/course-entry.js?token=PrivateSecret").ToArray();
        var selected=PublicSchoolAssets.AcademicScriptPaths(scripts);
        Assert.Equal(48,selected.Length);Assert.Equal("/student/static/scheduletable/model0.js",selected[0]);
        Assert.Equal("/student/static/business/course-entry.js",selected[^1]);
        Assert.DoesNotContain(selected,path=>path.Contains('?') || path.Contains("PrivateSecret",StringComparison.Ordinal));
    }
    [Fact]
    public void ScriptSelectionDeduplicatesAndRejectsNonPublicCandidates()
    {
        var selected=PublicSchoolAssets.AcademicScriptPaths([
            "http://jxgl.dlut.edu.cn/student/static/a.js?token=one","http://jxgl.dlut.edu.cn/student/static/a.js?token=two",
            "https://evil.example/student/static/b.js","http://jxgl.dlut.edu.cn/student/123456789.js"]);
        Assert.Equal(["/student/static/a.js"],selected);
    }
    [Fact]
    public void AcademicModelRetainsScheduleTimingButNotPersonalNamesOrIds()
    {
        using var observer=new SchemaObservation();
        var body="""{"result":{"schedules":[{"lessonId":123456789,"scheduleGroupId":234567890,"weekday":2,"weekIndex":3,"date":"2026-09-15","startTime":805,"endTime":945,"periods":2,"personName":"PrivateTeacher","room":{"nameZh":"PrivateRoom"}}],"units":[{"indexNo":1,"startTime":805,"endTime":850}]}}""";
        var report=observer.Create(new("http://jxgl.dlut.edu.cn/student/for-std/course-table"),"POST",200,Encoding.UTF8.GetBytes(body));
        using var shape=JsonDocument.Parse(report.ShapeJson);
        var result=shape.RootElement.GetProperty("result");
        var schedule=result.GetProperty("schedules").GetProperty("samples")[0];
        Assert.Equal("2026-09-15",schedule.GetProperty("date").GetString());
        Assert.Equal(805,schedule.GetProperty("startTime").GetInt32());
        Assert.Equal(3,schedule.GetProperty("weekIndex").GetInt32());
        Assert.Equal(2,schedule.GetProperty("periods").GetInt32());
        Assert.Equal(850,result.GetProperty("units").GetProperty("samples")[0].GetProperty("endTime").GetInt32());
        foreach(var personal in new[]{"123456789","234567890","PrivateTeacher","PrivateRoom"})Assert.DoesNotContain(personal,report.ShapeJson);
        var foreign=observer.Create(new("https://portal.dlut.edu.cn/api"),"POST",200,Encoding.UTF8.GetBytes(body));
        Assert.DoesNotContain("2026-09-15",foreign.ShapeJson);Assert.DoesNotContain("805",foreign.ShapeJson);
    }
    [Theory]
    [InlineData(965,"2026-02-30")]
    [InlineData(2400,"1234567890")]
    [InlineData(-1,"1900-01-01")]
    public void InvalidAcademicTimingIsRedacted(int time,string date)
    {
        using var observer=new SchemaObservation();
        var body=JsonSerializer.SerializeToUtf8Bytes(new{lessonId=1,weekday=2,date,startTime=time,endTime=time});
        var report=observer.Create(new("http://jxgl.dlut.edu.cn/student/for-std/course-table"),"POST",200,body);
        using var shape=JsonDocument.Parse(report.ShapeJson);
        Assert.Equal("<string>",shape.RootElement.GetProperty("date").GetString());
        Assert.Equal("<number>",shape.RootElement.GetProperty("startTime").GetString());
        Assert.Equal("<number>",shape.RootElement.GetProperty("endTime").GetString());
    }
    [Fact]
    public void PlainDatesAndTimesOutsideRecognizedModelStayRedacted()
    {
        using var observer=new SchemaObservation();
        var report=observer.Create(new("http://jxgl.dlut.edu.cn/student/for-std/course-table"),"GET",200,
            """{"date":"2026-09-15","startTime":805,"endTime":945,"personName":"PrivateTeacher"}"""u8.ToArray());
        Assert.DoesNotContain("2026-09-15",report.ShapeJson);Assert.DoesNotContain("805",report.ShapeJson);
        Assert.DoesNotContain("PrivateTeacher",report.ShapeJson);
    }
    [Fact]
    public void LargeEarlierBranchLeavesBudgetForLaterScheduleArray()
    {
        using var observer=new SchemaObservation();
        var wide=Enumerable.Range(0,64).ToDictionary(index=>"PrivateKey"+index,index=>(object)new{teacherAssignmentList=Enumerable.Repeat(new{name="PrivateTeacher",id=123456789},12).ToArray()});
        var schedules=Enumerable.Range(0,205).Select(index=>new{lessonId=123456789,weekday=2,weekIndex=3,date="2026-09-15",startTime=805,endTime=945}).ToArray();
        var body=JsonSerializer.SerializeToUtf8Bytes(new{result=new{earlier=wide,schedules}});
        var report=observer.Create(new("http://jxgl.dlut.edu.cn/student/for-std/course-table"),"POST",200,body);
        using var shape=JsonDocument.Parse(report.ShapeJson);
        var array=shape.RootElement.GetProperty("result").GetProperty("schedules");
        Assert.Equal(205,array.GetProperty("arrayCount").GetInt32());
        Assert.Equal(805,array.GetProperty("samples")[0].GetProperty("startTime").GetInt32());
        Assert.True(report.Truncated);Assert.InRange(Encoding.UTF8.GetByteCount(report.ShapeJson),1,SchemaObservation.MaximumShapeBytes);
        foreach(var personal in new[]{"PrivateKey","PrivateTeacher","123456789"})Assert.DoesNotContain(personal,report.ShapeJson);
    }
    [Theory]
    [InlineData("http://jxgl.dlut.edu.cn/student/static/business/course-table.js?token=private#secret","/student/static/business/course-table.js")]
    [InlineData("http://jxgl.dlut.edu.cn/student/static/123456789.js",null)]
    [InlineData("http://jxgl.dlut.edu.cn/student/private.js",null)]
    [InlineData("https://evil.example/student/static/course.js",null)]
    [InlineData("http://jxgl.dlut.edu.cn/student/static/student-data.json",null)]
    public void StaticCodeEvidenceExcludesQueryIdentifiersAndUnapprovedPaths(string uri,string? expected)
        =>Assert.Equal(expected,PublicSchoolAssets.AcademicScriptPath(uri));
    [Fact]
    public void RemovesCredentialsPersonalValuesDynamicKeysAndQueryValues()
    {
        using var observer=new SchemaObservation();
        var json="""{"data":[{"kcmc":"私人课程","studentId":"123456789","teacherName":"本人姓名","zcd":"1-8周(单)","xqj":3,"password":"PasswordSecret","access_token":"BearerSecret","张三的私人字典":{"value":"private"}}]}""";
        var report=observer.Create(new("https://apim.dlut.edu.cn/student/123456789?termId=private-term&token=secret&person_name=private"),"POST",200,Encoding.UTF8.GetBytes(json));
        var output=JsonSerializer.Serialize(report);
        foreach(var sensitive in new[]{"私人课程","123456789","本人姓名","PasswordSecret","BearerSecret","张三的私人字典","private-term","person_name","secret"})
            Assert.DoesNotContain(sensitive,output,StringComparison.OrdinalIgnoreCase);
        Assert.Contains("kcmc",report.ShapeJson);Assert.Contains("1-8",report.ShapeJson);Assert.Contains("\"xqj\":3",report.ShapeJson);
        Assert.DoesNotContain("token",output,StringComparison.OrdinalIgnoreCase);Assert.True(report.MaskedNames>0);
    }
    [Fact]
    public void NumericLookingIdentifiersAreNotMistakenForWeekExpressions()
    {
        using var observer=new SchemaObservation();
        var report=observer.Create(new("https://portal.dlut.edu.cn/api"),"GET",200,"{\"zcd\":\"123456789\",\"weeks\":[1,3,5],\"studentId\":123456789}"u8.ToArray());
        Assert.DoesNotContain("123456789",report.ShapeJson);Assert.Contains("[1,3]",report.ShapeJson);
    }
    [Fact]
    public void SamplesAreBoundedAndUnknownNameHashesAreSessionSpecific()
    {
        var body=Encoding.UTF8.GetBytes("{\"unlistedField\":["+string.Join(',',Enumerable.Repeat("{\"value\":\"secretValue\"}",10000))+"]}");
        using var one=new SchemaObservation();using var two=new SchemaObservation();var uri=new Uri("https://portal.dlut.edu.cn/api/unknownRoute");
        var a=one.Create(uri,"GET",200,body);var b=two.Create(uri,"GET",200,body);
        Assert.InRange(a.ShapeJson.Length,1,SchemaObservation.MaximumShapeBytes);Assert.DoesNotContain("secretValue",a.ShapeJson);
        Assert.NotEqual(a.ShapeJson,b.ShapeJson);Assert.NotEqual(a.Route,b.Route);
        using var parsed=JsonDocument.Parse(a.ShapeJson);Assert.Contains("10000",a.ShapeJson);
    }
    [Fact]
    public void OversizeMalformedOrForeignSourcesAreRejected()
    {
        using var observer=new SchemaObservation();
        Assert.Throws<InvalidDataException>(()=>observer.Create(new("https://evil.example/api"),"GET",200,"{}"u8.ToArray()));
        Assert.Throws<InvalidDataException>(()=>observer.Create(new("https://portal.dlut.edu.cn/api"),"GET",200,new byte[WorkerProtocol.MaxMessageBytes+1]));
        Assert.ThrowsAny<JsonException>(()=>observer.Create(new("https://portal.dlut.edu.cn/api"),"GET",200,"broken"u8.ToArray()));
    }
    [Fact]
    public void DeepOrWideShapesAreMarkedTruncatedWithoutKeepingPersonalStrings()
    {
        using var observer=new SchemaObservation();var deep=new string('[',20)+"\"personal\""+new string(']',20);
        var report=observer.Create(new("https://portal.dlut.edu.cn/api"),"GET",200,Encoding.UTF8.GetBytes(deep));
        Assert.True(report.Truncated);Assert.DoesNotContain("personal",report.ShapeJson);
    }
}
