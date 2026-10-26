using System.Text;
using System.Text.Json;
using DlutTimetable.Contracts;

namespace DlutTimetable.Tests;

public sealed class AcademicPageEvidenceTests
{
    private static readonly Uri Course=new(SchoolNavigation.AcademicCourseTable);
    [Fact]
    public void KeepsOnlyScopedStaticPathLiteralsAndNoFormOrScriptValues()
    {
        var html="""
            <input name="password" value="PrivatePassword"><p>/ws/schedule-table/not-a-script</p>
            <script>var studentId='PrivateStudent'; var token='PrivateToken';
            var url=window.CONTEXT_PATH+'/ws/schedule-table/course-data';
            var other='/student/for-std/course-table/data';
            var evil='https://evil.example/ws/schedule-table/private-data';
            var query='/ws/schedule-table/query-data?token=PrivateToken';
            var dynamic='/ws/schedule-table/123456789';</script>
            """;
        var evidence=AcademicPageEvidence.Read(Course,"GET",200,Encoding.UTF8.GetBytes(html))!;
        Assert.Equal(["/student/for-std/course-table/data","/student/ws/schedule-table/course-data"],evidence.StaticRoutes);
        Assert.False(evidence.Truncated);
        var encoded=JsonSerializer.Serialize(evidence);
        foreach(var secret in new[]{"PrivatePassword","PrivateStudent","PrivateToken","evil.example","query-data","not-a-script","123456789"})
            Assert.DoesNotContain(secret,encoded);
    }
    [Theory]
    [InlineData("https://portal.dlut.edu.cn/tp/index.html","GET",200)]
    [InlineData("http://jxgl.dlut.edu.cn/student/login","GET",200)]
    [InlineData("http://jxgl.dlut.edu.cn:8080/student/for-std/course-table","GET",200)]
    [InlineData(SchoolNavigation.AcademicCourseTable,"POST",200)]
    [InlineData(SchoolNavigation.AcademicCourseTable,"GET",302)]
    public void RequiresExactSuccessfulAcademicPage(string uri,string method,int status)
        =>Assert.Null(AcademicPageEvidence.Read(new(uri),method,status,"<script>var url='/ws/schedule-table/course-data';</script>"u8.ToArray()));
    [Fact]
    public void BoundsRoutesAndMarkupAndDoesNotReturnArbitrarySource()
    {
        static string Word(int index)=>"route-"+(char)('a'+index/26)+(char)('a'+index%26);
        var html="<script>"+string.Join(';',Enumerable.Range(0,40).Select(index=>"var url='/ws/schedule-table/"+Word(index)+"'"))+"</script>";
        var evidence=AcademicPageEvidence.Read(Course,"GET",200,Encoding.UTF8.GetBytes(html))!;
        Assert.Equal(32,evidence.StaticRoutes.Length);Assert.True(evidence.Truncated);
        Assert.Null(AcademicPageEvidence.Read(Course,"GET",200,new byte[AcademicPageEvidence.MaximumMarkupBytes+1]));
    }
    [Fact]
    public void OnlyPageDeclaredExactRoutesLoseTheirMasksAndResetRemovesTheEvidence()
    {
        using var observer=new SchemaObservation();var endpoint=new Uri("http://jxgl.dlut.edu.cn/student/ws/schedule-table/course-data?studentId=PrivateStudent");
        var before=observer.Create(endpoint,"POST",200,"{}"u8.ToArray());
        Assert.DoesNotContain("course-data",before.Route);
        var page=observer.ObserveAcademicPage(Course,"GET",200,"<script>var url='/ws/schedule-table/course-data';</script>"u8.ToArray())!;
        Assert.Equal("HtmlMetadata",page.Outcome);
        var after=observer.Create(endpoint,"POST",200,"{}"u8.ToArray());
        Assert.Equal("/student/ws/schedule-table/course-data",after.Route);Assert.Equal("POST",after.Method);
        Assert.DoesNotContain("PrivateStudent",JsonSerializer.Serialize(after));
        var unrelated=observer.Create(new("http://jxgl.dlut.edu.cn/student/ws/schedule-table/other-data"),"POST",200,"{}"u8.ToArray());
        Assert.DoesNotContain("other-data",unrelated.Route);
        observer.ResetPageEvidence();
        Assert.Equal(before.Route,observer.Create(endpoint,"POST",200,"{}"u8.ToArray()).Route);
    }
}
