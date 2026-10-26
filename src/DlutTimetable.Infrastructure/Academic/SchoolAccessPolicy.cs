using DlutTimetable.Contracts;

namespace DlutTimetable.Infrastructure.Academic;

/// <summary>Transport mapping preserves the existing canonical school policy.</summary>
public sealed class SchoolAccessPolicy
{
    private readonly string[] requested;
    private readonly WebVpnUriCodec codec=WebVpnUriCodec.ForDlut();
    public bool WebVpnGranted {get;}
    public SchoolAccessPolicy(IEnumerable<string> requestedHosts)
    {
        ArgumentNullException.ThrowIfNull(requestedHosts);requested=requestedHosts.Take(9).ToArray();
        if(requested.Length is <1 or >8)throw new ArgumentException("Invalid requested host set.");
        WebVpnGranted=requested.Contains(SchoolNavigation.WebVpnHost,StringComparer.OrdinalIgnoreCase);
    }
    private static bool Parse(string address,out Uri uri)=>Uri.TryCreate(address,UriKind.Absolute,out uri!) && address.Length<=8192;
    private static bool ProxyOrigin(Uri uri)=>uri.Scheme=="https" && uri.IsDefaultPort && uri.UserInfo.Length==0
        && uri.IdnHost.Equals(SchoolNavigation.WebVpnHost,StringComparison.OrdinalIgnoreCase);
    private Uri? Unwrap(Uri uri)
    {
        if(uri.IdnHost.Equals(SchoolNavigation.WebVpnHost,StringComparison.OrdinalIgnoreCase))
            return WebVpnGranted && ProxyOrigin(uri) && codec.TryDecode(uri,out var target)?target:null;
        return uri;
    }
    public Uri? CanonicalForRead(Uri transport)
    {
        if(!transport.IsAbsoluteUri || transport.OriginalString.Length>8192)return null;
        var target=Unwrap(transport);
        return target is not null && SchoolNavigation.Allowed(target.AbsoluteUri,requested)?target:null;
    }
    public bool AllowedNavigation(string address)
    {
        if(!Parse(address,out var uri))return false;
        if(WebVpnGranted && ProxyOrigin(uri) && uri.AbsolutePath is "/" or "/login")return true;
        return CanonicalForRead(uri) is not null;
    }
    public string? SecureSsoTarget(string address)
    {
        if(!Parse(address,out var uri))return null;
        var target=Unwrap(uri);if(target is null)return null;
        var secure=SchoolNavigation.SecureSsoTarget(target.AbsoluteUri,requested);
        if(secure is null)return null;
        return ProxyOrigin(uri)?codec.Encode(new(secure)).AbsoluteUri:secure;
    }
    public bool IsInsecureSso(string address)
    {
        if(!Parse(address,out var uri))return false;
        var target=Unwrap(uri);
        return target is not null && target.Scheme=="http" && target.IdnHost=="sso.dlut.edu.cn";
    }
    public string TransportFor(Uri canonical,bool viaWebVpn)
    {
        if(!SchoolNavigation.Allowed(canonical.AbsoluteUri,requested))throw new ArgumentException("School destination is not allowed.");
        if(!viaWebVpn)return canonical.AbsoluteUri;
        if(!WebVpnGranted)throw new InvalidOperationException("WebVPN transport was not granted.");
        return codec.Encode(canonical).AbsoluteUri;
    }
}
