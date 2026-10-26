using System.Globalization;
using DlutTimetable.Contracts;

namespace DlutTimetable.Tests;

public sealed class SchoolRetryAfterTests
{
    private static readonly DateTimeOffset Now=new(2026,10,24,8,0,0,TimeSpan.Zero);
    [Theory]
    [InlineData("0",0)]
    [InlineData("12",12)]
    [InlineData(null,30)]
    [InlineData("-1",30)]
    [InlineData("untrusted header",30)]
    public void ParsesDeltaOrUsesBoundedFallback(string? header,int seconds)=>Assert.Equal(Now.AddSeconds(seconds),SchoolRetryAfter.Parse(header,Now));
    [Fact]
    public void AcceptsHttpDateAndNeverWaitsForAPastDate()
    {
        Assert.Equal(Now.AddHours(1),SchoolRetryAfter.Parse(Now.AddHours(1).ToString("r",CultureInfo.InvariantCulture),Now));
        Assert.Equal(Now,SchoolRetryAfter.Parse(Now.AddHours(-1).ToString("r",CultureInfo.InvariantCulture),Now));
        Assert.Equal(DateTimeOffset.MaxValue,SchoolRetryAfter.Parse(long.MaxValue.ToString(CultureInfo.InvariantCulture),Now));
    }
    [Theory]
    [InlineData("http://jxgl.dlut.edu.cn/student/ws/schedule-table/datum",true)]
    [InlineData("https://jxgl.dlut.edu.cn/student/ws/schedule-table/datum",true)]
    [InlineData("https://jxgl.dlut.edu.cn:444/student/ws/schedule-table/datum",false)]
    [InlineData("http://jxgl.dlut.edu.cn.evil/student/ws/schedule-table/datum",false)]
    [InlineData("http://user@jxgl.dlut.edu.cn/student/ws/schedule-table/datum",false)]
    [InlineData("http://jxgl.dlut.edu.cn/student/other",false)]
    public void FailuresAndCooldownApplyOnlyToVerifiedDataEndpoints(string value,bool expected)=>Assert.Equal(expected,AcademicEndpoints.IsData(new(value)));
    [Fact]
    public async Task CooldownSurvivesWorkerProtocolIncludingCancellation()
    {
        var request=new WorkerRequest(WorkerProtocol.Version,Guid.NewGuid(),new(1),new string('A',64),WorkerTaskKind.InteractiveLogin,
            SchoolNavigation.Hosts.ToArray(),DateTimeOffset.UtcNow.AddMinutes(2));
        var response=new WorkerResponse(WorkerProtocol.Version,request.RequestId,request.Generation,request.Nonce,WorkerStatus.Cancelled,null,
            new(0,[],true,RetryAfterUtc:Now.AddHours(1)));
        using var stream=new MemoryStream();await WorkerWire.WriteAsync(stream,response);stream.Position=0;
        var read=await WorkerWire.ReadAsync<WorkerResponse>(stream);WorkerWire.Validate(read,request,request.Generation);
        Assert.Equal(response.Diagnostics!.RetryAfterUtc,read.Diagnostics!.RetryAfterUtc);
    }
}
