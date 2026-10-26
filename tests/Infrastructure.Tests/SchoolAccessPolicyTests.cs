using DlutTimetable.Contracts;
using DlutTimetable.Infrastructure.Academic;

namespace DlutTimetable.Tests;

public sealed class SchoolAccessPolicyTests
{
    private static SchoolAccessPolicy Granted()=>new(SchoolNavigation.Hosts.Append(SchoolNavigation.WebVpnHost));
    [Fact]
    public void ObservedPublicCasRedirectDecodesToSsoAndDoesNotWidenCanonicalHosts()
    {
        var policy=Granted();
        var uri=new Uri("https://webvpn.dlut.edu.cn/https/57787a7876706e323032336b657940246b0b0d56f80f865ae449fe2ddfb88b/cas/login?service=synthetic");
        Assert.True(policy.AllowedNavigation(uri.AbsoluteUri));
        Assert.Equal("https://sso.dlut.edu.cn/cas/login?service=synthetic",policy.CanonicalForRead(uri)!.AbsoluteUri);
        Assert.False(SchoolNavigation.Allowed(uri.AbsoluteUri,SchoolNavigation.Hosts));
    }
    [Fact]
    public void GatewayLoginCanNavigateButIsNeverAReadableSchoolResponse()
    {
        var policy=Granted();
        foreach(var uri in new[]{"https://webvpn.dlut.edu.cn/","https://webvpn.dlut.edu.cn/login?cas_login=true"})
        {Assert.True(policy.AllowedNavigation(uri));Assert.Null(policy.CanonicalForRead(new(uri)));}
        Assert.False(policy.AllowedNavigation("https://webvpn.dlut.edu.cn/unknown-admin"));
    }
    [Fact]
    public void ProxyRequestRequiresGrantAndCanonicalAcademicPolicyStillApplies()
    {
        var codec=WebVpnUriCodec.ForDlut();var plain=new Uri(SchoolNavigation.AcademicCourseTable);var proxy=codec.Encode(plain);
        var direct=new SchoolAccessPolicy(SchoolNavigation.Hosts);
        Assert.False(direct.AllowedNavigation(proxy.AbsoluteUri));Assert.Null(direct.CanonicalForRead(proxy));
        Assert.Throws<InvalidOperationException>(()=>direct.TransportFor(plain,true));
        Assert.Equal(plain.AbsoluteUri,Granted().CanonicalForRead(proxy)!.AbsoluteUri);
        Assert.Null(Granted().CanonicalForRead(codec.Encode(new("http://jxgl.dlut.edu.cn/admin/"))));
    }
    [Fact]
    public void InsecureSsoInsideProxyUpgradesWithoutReadingOrChangingQueryValues()
    {
        var codec=WebVpnUriCodec.ForDlut();var policy=Granted();
        var raw=codec.Encode(new("http://sso.dlut.edu.cn/cas/login?service=a%2Fb#part"));
        Assert.Null(policy.CanonicalForRead(raw));
        var secure=policy.SecureSsoTarget(raw.AbsoluteUri);Assert.NotNull(secure);
        Assert.Equal("https://sso.dlut.edu.cn/cas/login?service=a%2Fb#part",codec.Decode(new(secure!)).AbsoluteUri);
        Assert.True(policy.AllowedNavigation(secure!));
        Assert.Null(new SchoolAccessPolicy(SchoolNavigation.Hosts).SecureSsoTarget(raw.AbsoluteUri));
    }
}
