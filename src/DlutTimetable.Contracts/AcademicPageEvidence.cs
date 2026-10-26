using System.Text;
using System.Text.RegularExpressions;

namespace DlutTimetable.Contracts;

public sealed record AcademicPageEvidence(string[] StaticRoutes,bool Truncated)
{
    public const int MaximumMarkupBytes=1024*1024;
    public const int MaximumRoutes=32;
    private static readonly Regex Scripts=new(@"<script\b[^>]*>(?<body>.*?)</script\s*>",
        RegexOptions.IgnoreCase|RegexOptions.Singleline|RegexOptions.CultureInvariant|RegexOptions.NonBacktracking,TimeSpan.FromMilliseconds(200));
    private static readonly Regex Routes=new("""["'](?<route>(?:/student)?/(?:ws/schedule-table|for-std/course-table)/[a-z][a-z-]{1,63})["']""",
        RegexOptions.CultureInvariant|RegexOptions.NonBacktracking,TimeSpan.FromMilliseconds(200));

    // Only static path literals inside the exact course page's scripts. Never export the
    // markup, form fields, arbitrary script text, query values or dynamically built suffixes.
    public static AcademicPageEvidence? Read(Uri uri,string method,int status,ReadOnlyMemory<byte> body)
    {
        if(method!="GET" || status!=200 || uri.IdnHost!="jxgl.dlut.edu.cn"
            || !SchoolNavigation.Allowed(uri.AbsoluteUri,SchoolNavigation.Hosts)
            || uri.AbsolutePath!="/student/for-std/course-table" || body.Length>MaximumMarkupBytes)return null;
        var found=new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach(Match script in Scripts.Matches(Encoding.UTF8.GetString(body.Span)))
                foreach(Match route in Routes.Matches(script.Groups["body"].Value))
                {
                    var path=route.Groups["route"].Value;
                    if(!path.StartsWith("/student/",StringComparison.Ordinal))path="/student"+path;
                    if(path.Length>128)continue;
                    found.Add(path);
                    if(found.Count>MaximumRoutes)return new(found.Take(MaximumRoutes).Order(StringComparer.Ordinal).ToArray(),true);
                }
        }
        catch(RegexMatchTimeoutException){return null;}
        return found.Count==0?null:new(found.Order(StringComparer.Ordinal).ToArray(),false);
    }
}
