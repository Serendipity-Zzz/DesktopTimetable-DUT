using System.Buffers.Binary;
using DlutTimetable.Contracts;

namespace DlutTimetable.Tests;

public sealed class WorkerProtocolTests
{
    [Theory]
    [InlineData("cookie")][InlineData("accessToken")][InlineData("studentNumber")]
    public async Task UnknownCredentialEnvelopeFieldsAreRejectedWithoutEchoingTheirValues(string field)
    {
        using var source=new MemoryStream();await WorkerWire.WriteAsync(source,Request());
        var node=System.Text.Json.Nodes.JsonNode.Parse(source.ToArray().AsSpan(4))!.AsObject();node[field]="PrivateTransportSecret";
        var body=System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(node);var frame=new byte[body.Length+4];
        BinaryPrimitives.WriteInt32LittleEndian(frame,body.Length);body.CopyTo(frame,4);
        using var malformed=new MemoryStream(frame);
        var error=await Assert.ThrowsAsync<InvalidDataException>(()=>WorkerWire.ReadAsync<WorkerRequest>(malformed));
        Assert.Equal("Invalid worker JSON.",error.Message);Assert.DoesNotContain("PrivateTransportSecret",error.Message);
    }
    private static WorkerRequest Request()=>new(WorkerProtocol.Version,Guid.NewGuid(),new(3),new string('A',64),
        WorkerTaskKind.InteractiveLogin,SchoolNavigation.Hosts.ToArray(),DateTimeOffset.UtcNow.AddMinutes(2));
    [Fact]
    public async Task RequestsAndControlsRoundtripWithLengthFraming()
    {
        var request=Request();using var stream=new MemoryStream();await WorkerWire.WriteAsync(stream,request);
        stream.Position=0;var read=await WorkerWire.ReadAsync<WorkerRequest>(stream);WorkerWire.Validate(read,request.Nonce,DateTimeOffset.UtcNow);
        Assert.Equal(request.RequestId,read.RequestId);Assert.Equal(request.AllowedHosts,read.AllowedHosts);Assert.Equal(stream.Length,stream.Position);
        stream.SetLength(0);var control=new WorkerControl(WorkerProtocol.Version,request.RequestId,request.Generation,request.Nonce,WorkerControlKind.Cancel);
        await WorkerWire.WriteAsync(stream,control);stream.Position=0;WorkerWire.Validate(await WorkerWire.ReadAsync<WorkerControl>(stream),request);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(5242881)]
    [InlineData(int.MaxValue)]
    public async Task RejectsOversizePrefixBeforeReadingBody(int length)
    {
        var prefix=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(prefix,length);using var stream=new MemoryStream(prefix);
        await Assert.ThrowsAsync<InvalidDataException>(()=>WorkerWire.ReadAsync<WorkerRequest>(stream));
    }
    [Fact]
    public async Task TruncatedMalformedOrUnexpectedFieldsCannotBecomeRequests()
    {
        using var prefix=new MemoryStream(new byte[]{3,0});
        await Assert.ThrowsAsync<EndOfStreamException>(()=>WorkerWire.ReadAsync<WorkerRequest>(prefix));
        using var incomplete=new MemoryStream(new byte[]{3,0,0,0,(byte)'{'});
        await Assert.ThrowsAsync<EndOfStreamException>(()=>WorkerWire.ReadAsync<WorkerRequest>(incomplete));
        using var malformed=new MemoryStream(new byte[]{1,0,0,0,(byte)'x'});
        await Assert.ThrowsAsync<InvalidDataException>(()=>WorkerWire.ReadAsync<WorkerRequest>(malformed));
        using var unexpected=new MemoryStream();await WorkerWire.WriteAsync(unexpected,new{command="delete files"});unexpected.Position=0;
        await Assert.ThrowsAsync<InvalidDataException>(()=>WorkerWire.ReadAsync<WorkerRequest>(unexpected));
    }
    [Fact]
    public void InvalidRequestIdentityTaskHostOrDeadlineIsRejected()
    {
        var r=Request();var now=DateTimeOffset.UtcNow;
        foreach(var invalid in new[]{r with{ProtocolVersion=99},r with{RequestId=Guid.Empty},r with{Generation=new(-1)},r with{Nonce=new string('B',64)},
            r with{TaskKind=(WorkerTaskKind)99},r with{AllowedHosts=[]},r with{AllowedHosts=["https://portal.dlut.edu.cn"]},
            r with{DeadlineUtc=now.AddSeconds(-1)},r with{DeadlineUtc=now.AddMinutes(16)}})
            Assert.Throws<InvalidDataException>(()=>WorkerWire.Validate(invalid,r.Nonce,now));
    }
    [Fact]
    public void StaleResultOrCredentialsPayloadInFailureDoesNotPassEnvelopeChecks()
    {
        var r=Request();var valid=new WorkerResponse(WorkerProtocol.Version,r.RequestId,r.Generation,r.Nonce,WorkerStatus.NeedsLogin,null);
        WorkerWire.Validate(valid,r,r.Generation);
        foreach(var invalid in new[]{valid with{ProtocolVersion=99},valid with{RequestId=Guid.NewGuid()},valid with{Generation=new(4)},
            valid with{Nonce=new string('0',64)},valid with{Status=(WorkerStatus)999},valid with{DatasetJson="unexpected"},
            valid with{Status=WorkerStatus.Completed},valid with{Diagnostics=new(1,[0],false)}})
            Assert.Throws<InvalidDataException>(()=>WorkerWire.Validate(invalid,r,r.Generation));
        Assert.Throws<InvalidDataException>(()=>WorkerWire.Validate(valid,r,new(4)));
        Assert.Throws<InvalidDataException>(()=>WorkerWire.Validate(new WorkerControl(WorkerProtocol.Version,r.RequestId,r.Generation,new string('0',64),WorkerControlKind.Cancel),r));
    }
    [Theory]
    [InlineData("https://portal.dlut.edu.cn/tp/index.html",true)]
    [InlineData("https://sso.dlut.edu.cn/cas/login",true)]
    [InlineData("https://apim.dlut.edu.cn/",true)]
    [InlineData("http://jxgl.dlut.edu.cn/student/for-std/course-table",true)]
    [InlineData("http://jxgl.dlut.edu.cn/student/ucas-sso/login",true)]
    [InlineData("http://jxgl.dlut.edu.cn/",false)]
    [InlineData("http://jxgl.dlut.edu.cn/student-evil/",false)]
    [InlineData("http://jxgl.dlut.edu.cn:8080/student/",false)]
    [InlineData("http://jxgl.dlut.edu.cn.evil.example/student/",false)]
    [InlineData("http://portal.dlut.edu.cn/",false)]
    [InlineData("https://portal.dlut.edu.cn.evil.example/",false)]
    [InlineData("https://portal.dlut.edu.cn@evil.example/",false)]
    [InlineData("https://evil@portal.dlut.edu.cn/",false)]
    [InlineData("https://portal.dlut.edu.cn:444/",false)]
    [InlineData("file:///C:/secrets",false)]
    [InlineData("javascript:alert(1)",false)]
    public void NavigationUsesVerifiedHostsAndAcademicHttpScope(string uri,bool expected)=>Assert.Equal(expected,SchoolNavigation.Allowed(uri,SchoolNavigation.Hosts));
    [Theory]
    [InlineData("http://sso.dlut.edu.cn/cas/login?ticket=secret#private", "http://sso.dlut.edu.cn:80")]
    [InlineData("https://user:secret@sso.dlut.edu.cn:8443/cas/login?ticket=secret", "https://sso.dlut.edu.cn:8443")]
    [InlineData("javascript:secret", "未知协议地址")]
    public void BlockedOriginExcludesCredentialsAndCallbackParameters(string uri,string expected)
        =>Assert.Equal(expected,SchoolNavigation.BlockedOrigin(uri));
    [Fact]
    public void SsoUpgradeIsExactHostCasDefaultPortAndRespectsRequestedHosts()
    {
        Assert.Equal("https://sso.dlut.edu.cn/cas/login?service=a%2Fb#part",
            SchoolNavigation.SecureSsoTarget("http://sso.dlut.edu.cn:80/cas/login?service=a%2Fb#part",SchoolNavigation.Hosts));
        foreach(var uri in new[]{"http://sso.dlut.edu.cn:8080/cas/login","http://sso.dlut.edu.cn/other",
            "http://sso.dlut.edu.cn/cas/../other","http://sso.dlut.edu.cn.evil.example/cas/login",
            "http://user@sso.dlut.edu.cn/cas/login","https://sso.dlut.edu.cn/cas/login"})
            Assert.Null(SchoolNavigation.SecureSsoTarget(uri,SchoolNavigation.Hosts));
        Assert.Null(SchoolNavigation.SecureSsoTarget("http://sso.dlut.edu.cn/cas/login",["portal.dlut.edu.cn"]));
        Assert.False(SchoolNavigation.Allowed("http://sso.dlut.edu.cn/cas/login",SchoolNavigation.Hosts));
    }
    [Fact]
    public void AcademicEntryStillRequiresRequestedHostAndIdentifiesOnlyItsLocalLogin()
    {
        Assert.Equal("http://jxgl.dlut.edu.cn/student/ucas-sso/login",SchoolNavigation.AcademicSignIn);
        Assert.False(SchoolNavigation.Allowed(SchoolNavigation.AcademicCourseTable,["portal.dlut.edu.cn"]));
        Assert.True(SchoolNavigation.IsAcademicLogin("http://jxgl.dlut.edu.cn/student/login?refer=anything"));
        Assert.False(SchoolNavigation.IsAcademicLogin("http://evil.example/student/login"));
        Assert.False(SchoolNavigation.IsAcademicLogin(SchoolNavigation.AcademicSignIn));
        Assert.False(SchoolNavigation.Allowed("http://jxgl.dlut.edu.cn/student/../admin/",SchoolNavigation.Hosts));
    }
    [Fact]
    public void ObservationReportsAreBoundedAndCannotCrossCancelledOrForeignResults()
    {
        var r=Request();
        var observation=new ResponseObservation("portal.dlut.edu.cn","/api","GET",200,[],"{}",2,false,0);
        var report=new ObservationReport(1,DateTimeOffset.UtcNow,[observation],0,0);
        var response=new WorkerResponse(WorkerProtocol.Version,r.RequestId,r.Generation,r.Nonce,WorkerStatus.UnsupportedSchema,null,new(0,[],true,false,report));
        WorkerWire.Validate(response,r,r.Generation);
        foreach(var invalid in new[]{response with{Status=WorkerStatus.Cancelled},
            response with{Diagnostics=new(0,[],true,false,report with{PublicScriptPaths=["/student/static/course.js?token=private"]})},
            response with{Diagnostics=new(0,[],true,false,report with{Responses=Enumerable.Repeat(observation,25).ToArray()})},
            response with{Diagnostics=new(0,[],true,false,report with{Responses=[observation with{Host="evil.example"}]})},
            response with{Diagnostics=new(0,[],true,false,report with{Responses=[observation with{ShapeJson=new string('x',32769)}]})}})
            Assert.Throws<InvalidDataException>(()=>WorkerWire.Validate(invalid,r,r.Generation));
    }
    [Fact]
    public void ProfileScopesAreOpaqueBoundedAndDataDirectorySpecific()
    {
        Assert.Null(AuthProfileLocation.ScopeForDataDirectory(AuthProfileLocation.ApplicationDirectory));
        var one=AuthProfileLocation.ScopeForDataDirectory(Path.Combine(Path.GetTempPath(),"scope-one"));
        var two=AuthProfileLocation.ScopeForDataDirectory(Path.Combine(Path.GetTempPath(),"scope-two"));
        Assert.NotEqual(one,two);Assert.Equal(64,one!.Length);
        Assert.NotEqual(AuthProfileLocation.ProfileDirectory,AuthProfileLocation.ProfileDirectoryForScope(one));
        var request=Request();WorkerWire.Validate(request with{ProfileScopeId=one},request.Nonce,DateTimeOffset.UtcNow);
        Assert.Throws<InvalidDataException>(()=>WorkerWire.Validate(request with{ProfileScopeId="../auth-profile"},request.Nonce,DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(()=>AuthProfileLocation.ApplicationDirectoryForScope("../auth-profile"));
    }
    [Fact]
    public async Task CancellationAndOutboundLimitAreEnforced()
    {
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();using var stream=new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>WorkerWire.WriteAsync(stream,Request(),cancellation.Token));
        await Assert.ThrowsAsync<InvalidDataException>(()=>WorkerWire.WriteAsync(stream,new string('x',WorkerProtocol.MaxMessageBytes)));
        Assert.False(WorkerWire.SameNonce(null,new string('A',64)));Assert.False(WorkerWire.SameNonce(new string('Z',64),new string('Z',64)));
    }
}
