using DlutTimetable.Infrastructure.Academic;

namespace DlutTimetable.Tests;

public sealed class WebVpnUriCodecTests
{
    private const string PortalToken="57787a7876706e323032336b657940246817100cfd0fdd4aa659ee7694bf9069de28";
    [Fact]
    public void MatchesExistingOfflinePortalVectorAndPreservesQueryAndFragment()
    {
        var codec=WebVpnUriCodec.ForDlut();
        var source=new Uri("https://portal.dlut.edu.cn/tp/index.html?week=8&label=a%2Fb#calendar");
        var encoded=codec.Encode(source);
        Assert.Equal("https://webvpn.dlut.edu.cn/https/"+PortalToken+"/tp/index.html?week=8&label=a%2Fb#calendar",encoded.AbsoluteUri);
        Assert.Equal(source.AbsoluteUri,codec.Decode(encoded).AbsoluteUri);
    }
    [Fact]
    public void CorrectlyDecodesExistingExplicitPortVectorInsteadOfTreatingPortAsScheme()
    {
        var codec=new WebVpnUriCodec(new("https://webvpn.dlut.edu.cn/"),["example.edu.cn"],"Wxzxvpn2023key@$"u8,"Wxzxvpn2023key@$"u8);
        var encoded=new Uri("https://webvpn.dlut.edu.cn/https-8443/57787a7876706e323032336b657940247d000315ec0f9600af48ef7692b5/student/home");
        Assert.Equal("https://example.edu.cn:8443/student/home",codec.Decode(encoded).AbsoluteUri);
        Assert.Equal(encoded,codec.Encode(codec.Decode(encoded)));
    }
    [Theory]
    [InlineData("http://jxgl.dlut.edu.cn/student/for-std/course-table?semester=2026#week")]
    [InlineData("https://portal.dlut.edu.cn/")]
    [InlineData("https://sso.dlut.edu.cn/cas/login?service=https%3A%2F%2Fportal.dlut.edu.cn%2F")]
    public void RoundTripsApprovedHostShapesWithoutFetchingAnything(string value)
    {
        var codec=WebVpnUriCodec.ForDlut();var source=new Uri(value);
        Assert.Equal(source.AbsoluteUri,codec.Decode(codec.Encode(source)).AbsoluteUri);
    }
    [Fact]
    public void MatchesIndependentLegacyVectorAcrossTwoFullBlocksAndPartialTail()
    {
        var codec=new WebVpnUriCodec(new("https://webvpn.dlut.edu.cn/"),["long-campus-service.example.edu.cn"],"Wxzxvpn2023key@$"u8,"Wxzxvpn2023key@$"u8);
        var source=new Uri("https://long-campus-service.example.edu.cn:8443/student/home?week=1#calendar");
        const string expected="https://webvpn.dlut.edu.cn/https-8443/57787a7876706e323032336b6579402474170c1fb1009243ba59e97582be97314c61ffa018f51646a2d8c52030ff02a189e9/student/home?week=1#calendar";
        Assert.Equal(expected,codec.Encode(source).AbsoluteUri);
        Assert.Equal(source.AbsoluteUri,codec.Decode(new(expected)).AbsoluteUri);
    }
    [Theory]
    [InlineData("https://foreign.invalid/https/")]
    [InlineData("https://webvpn.dlut.edu.cn/ftp/")]
    [InlineData("https://webvpn.dlut.edu.cn/https-0/")]
    [InlineData("https://webvpn.dlut.edu.cn/https-65536/")]
    [InlineData("https://webvpn.dlut.edu.cn/https-080/")]
    [InlineData("https://user:pass@webvpn.dlut.edu.cn/https/")]
    public void RejectsUntrustedOriginOrMalformedRoute(string prefix)
    {
        Assert.False(WebVpnUriCodec.ForDlut().TryDecode(new(prefix+PortalToken+"/tp/index.html"),out _));
    }
    [Fact]
    public void RejectsUnknownDestinationAndModifiedCiphertextWithoutLeakingTheInput()
    {
        var codec=WebVpnUriCodec.ForDlut();
        Assert.Throws<ArgumentException>(()=>codec.Encode(new("https://foreign.invalid/private")));
        Assert.Throws<ArgumentException>(()=>codec.Encode(new("https://user:pass@portal.dlut.edu.cn/")));
        Assert.False(codec.TryDecode(new("https://webvpn.dlut.edu.cn/https/"+new string('0',32)+PortalToken[32..]+"/"),out _));
        Assert.False(codec.TryDecode(new("https://webvpn.dlut.edu.cn/https/"+PortalToken[..^1]+"g/"),out _));
    }
}
