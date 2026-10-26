using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DlutTimetable.Contracts;

namespace DlutTimetable.Infrastructure.Academic;

/// <summary>Offline URI conversion only; never grants navigation or authenticates a session.</summary>
public sealed class WebVpnUriCodec
{
    private readonly Uri gateway;
    private readonly HashSet<string> hosts;
    private readonly byte[] key,iv;
    private readonly string prefix;
    private static readonly UTF8Encoding Utf8=new(false,true);
    public WebVpnUriCodec(Uri gateway,IEnumerable<string> allowedHosts,ReadOnlySpan<byte> key,ReadOnlySpan<byte> iv)
    {
        ArgumentNullException.ThrowIfNull(gateway);ArgumentNullException.ThrowIfNull(allowedHosts);
        if(!gateway.IsAbsoluteUri || gateway.Scheme!="https" || !gateway.IsDefaultPort || gateway.UserInfo.Length!=0
            || gateway.AbsolutePath!="/" || gateway.Query.Length!=0 || gateway.Fragment.Length!=0
            || gateway.HostNameType!=UriHostNameType.Dns)throw new ArgumentException("Invalid proxy origin.",nameof(gateway));
        if(key.Length!=16 || iv.Length!=16)throw new ArgumentException("AES-128 parameters must each contain 16 bytes.");
        this.gateway=gateway;this.key=key.ToArray();this.iv=iv.ToArray();prefix=Convert.ToHexString(iv).ToLowerInvariant();
        hosts=new(allowedHosts.Take(17).Select(NormalizeHost),StringComparer.Ordinal);
        if(hosts.Count is <1 or >16 || hosts.Contains(NormalizeHost(gateway.IdnHost)))throw new ArgumentException("Invalid destination host set.",nameof(allowedHosts));
    }
    public static WebVpnUriCodec ForDlut()=>new(new("https://webvpn.dlut.edu.cn/"),SchoolNavigation.Hosts,
        // Public protocol obfuscation parameters established by the local URI
        // fixtures, not credentials or encryption for confidential content.
        "Wxzxvpn2023key@$"u8,"Wxzxvpn2023key@$"u8);

    public Uri Encode(Uri destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if(!destination.IsAbsoluteUri || destination.OriginalString.Length>8192 || destination.Scheme is not ("http" or "https")
            || destination.UserInfo.Length!=0 || !hosts.Contains(NormalizeHost(destination.IdnHost)))
            throw new ArgumentException("Unsupported destination URI.",nameof(destination));
        var route=destination.Scheme+(destination.IsDefaultPort?"":"-"+destination.Port.ToString(CultureInfo.InvariantCulture));
        var encrypted=Convert.ToHexString(Transform(Utf8.GetBytes(NormalizeHost(destination.IdnHost)),false)).ToLowerInvariant();
        return new(gateway.AbsoluteUri+route+"/"+prefix+encrypted+destination.PathAndQuery+destination.Fragment);
    }
    public Uri Decode(Uri proxyUri)=>TryDecode(proxyUri,out var destination)?destination!:throw new InvalidDataException("Unsupported proxy URI.");
    public bool TryDecode(Uri? proxyUri,out Uri? destination)
    {
        destination=null;
        try
        {
            if(proxyUri is null || !proxyUri.IsAbsoluteUri || proxyUri.OriginalString.Length>8192
                || proxyUri.Scheme!=gateway.Scheme || proxyUri.Port!=gateway.Port || proxyUri.UserInfo.Length!=0
                || !proxyUri.IdnHost.Equals(gateway.IdnHost,StringComparison.OrdinalIgnoreCase))return false;
            var parts=proxyUri.AbsolutePath.Split('/',4,StringSplitOptions.None);
            if(parts.Length!=4 || parts[0].Length!=0)return false;
            var route=parts[1].Split('-',2);var scheme=route[0];
            if(scheme is not ("http" or "https"))return false;
            var port="";
            if(route.Length==2)
            {
                if(route[1].Length==0 || route[1][0]=='0' || !route[1].All(char.IsAsciiDigit)
                    || !int.TryParse(route[1],NumberStyles.None,CultureInfo.InvariantCulture,out var number) || number is <1 or >65535)return false;
                port=":"+number.ToString(CultureInfo.InvariantCulture);
            }
            var token=parts[2];
            if(token.Length is <34 or >538 || token.Length%2!=0 || !token.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))return false;
            var host=NormalizeHost(Utf8.GetString(Transform(Convert.FromHexString(token[32..]),true)));
            if(!hosts.Contains(host))return false;
            var decoded=new Uri(scheme+"://"+host+port+"/"+parts[3]+proxyUri.Query+proxyUri.Fragment);
            if(decoded.UserInfo.Length!=0 || NormalizeHost(decoded.IdnHost)!=host)return false;
            destination=decoded;return true;
        }
        catch(Exception ex) when(ex is ArgumentException or FormatException or CryptographicException){return false;}
    }
    private static string NormalizeHost(string value)
    {
        if(string.IsNullOrEmpty(value) || value.Length>253 || value.EndsWith('.') || Uri.CheckHostName(value)!=UriHostNameType.Dns)
            throw new ArgumentException("Invalid destination host.");
        return new IdnMapping().GetAscii(value).ToLowerInvariant();
    }
    private byte[] Transform(ReadOnlySpan<byte> input,bool decrypt)
    {
        // CFB-128 uses the AES block primitive. The final hostname segment can
        // be shorter than 16 bytes; no padding bytes belong in the URI token.
        using var aes=Aes.Create();aes.Key=key;
        Span<byte> feedback=stackalloc byte[16];iv.CopyTo(feedback);
        Span<byte> mask=stackalloc byte[16];var output=new byte[input.Length];
        for(var offset=0;offset<input.Length;offset+=16)
        {
            aes.EncryptEcb(feedback,mask,PaddingMode.None);
            var count=Math.Min(16,input.Length-offset);
            for(var i=0;i<count;i++)output[offset+i]=(byte)(input[offset+i]^mask[i]);
            if(count==16)
            {
                if(decrypt)input.Slice(offset,16).CopyTo(feedback);
                else output.AsSpan(offset,16).CopyTo(feedback);
            }
        }
        return output;
    }
}
