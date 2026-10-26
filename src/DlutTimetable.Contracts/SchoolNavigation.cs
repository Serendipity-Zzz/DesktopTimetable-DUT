namespace DlutTimetable.Contracts;

public static class SchoolNavigation
{
    public const string Portal="https://portal.dlut.edu.cn/tp/index.html#act=sems-tp-nup_29827717";
    public const string AcademicCourseTable="http://jxgl.dlut.edu.cn/student/for-std/course-table";
    // Match the official login button exactly; extra parameters change the CAS service identity.
    public const string AcademicSignIn="http://jxgl.dlut.edu.cn/student/ucas-sso/login";
    public const string WebVpnHost="webvpn.dlut.edu.cn";
    public const string WebVpnLogin="https://webvpn.dlut.edu.cn/login";
    // Public portal defaults/js/constant.js identifies its CAS and API gateway (2026-10-23).
    public static readonly IReadOnlyList<string> Hosts=Array.AsReadOnly(new[]{"portal.dlut.edu.cn","sso.dlut.edu.cn","apim.dlut.edu.cn","jxgl.dlut.edu.cn"});
    public static bool Allowed(string uri,IReadOnlyCollection<string> requestedHosts)=>Uri.TryCreate(uri,UriKind.Absolute,out var parsed)
        && parsed.IsDefaultPort && parsed.UserInfo.Length==0
        && Hosts.Contains(parsed.IdnHost,StringComparer.OrdinalIgnoreCase)
        && requestedHosts.Contains(parsed.IdnHost,StringComparer.OrdinalIgnoreCase)
        && (parsed.Scheme==Uri.UriSchemeHttps || parsed.Scheme==Uri.UriSchemeHttp
            && parsed.IdnHost.Equals("jxgl.dlut.edu.cn",StringComparison.OrdinalIgnoreCase)
            && parsed.AbsolutePath.StartsWith("/student/",StringComparison.Ordinal));

    public static bool IsAcademicLogin(string uri)=>Uri.TryCreate(uri,UriKind.Absolute,out var parsed)
        && parsed.IdnHost.Equals("jxgl.dlut.edu.cn",StringComparison.OrdinalIgnoreCase)
        && parsed.AbsolutePath.TrimEnd('/')=="/student/login";

    // Origin only: never expose callback tickets, query strings, fragments or user information.
    public static string BlockedOrigin(string uri)=>Uri.TryCreate(uri,UriKind.Absolute,out var parsed)
        && parsed.Scheme is "http" or "https"
        ? $"{parsed.Scheme}://{parsed.IdnHost}:{parsed.Port}" : "未知协议地址";

    public static string? SecureSsoTarget(string uri,IReadOnlyCollection<string> requestedHosts)
    {
        if(!Uri.TryCreate(uri,UriKind.Absolute,out var parsed) || parsed.Scheme!=Uri.UriSchemeHttp
            || !parsed.IsDefaultPort || parsed.UserInfo.Length!=0
            || !parsed.IdnHost.Equals("sso.dlut.edu.cn",StringComparison.OrdinalIgnoreCase)
            || !parsed.AbsolutePath.StartsWith("/cas/",StringComparison.Ordinal))return null;
        var target=new UriBuilder(parsed){Scheme=Uri.UriSchemeHttps,Port=-1}.Uri.AbsoluteUri;
        return Allowed(target,requestedHosts)?target:null;
    }
}
