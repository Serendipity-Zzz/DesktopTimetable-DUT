using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace DlutTimetable.Windows;

// Invoked only by an explicit installation/rollback. The executing signed app
// supplies the publisher key; package metadata cannot select a trusted publisher.
public static class ReleaseSignatureVerifier
{
    private static readonly Lazy<string?> Publisher=new(()=>
    {
        var path=Environment.ProcessPath;
        if(path is null || !Path.GetFileName(path).Equals("DlutTimetable.Desktop.exe",StringComparison.OrdinalIgnoreCase))return null;
        using var certificate=TrustedCertificate(path);
        return certificate is null?null:Key(certificate);
    });
    public static bool Verify(string executable)
    {
        try
        {
            if(Publisher.Value is not {} publisher)return false;
            if(Path.GetFileName(executable) is not ("DlutTimetable.Desktop.exe" or "DlutTimetable.AuthWorker.exe"))return false;
            using var certificate=TrustedCertificate(executable);
            if(certificate is null || Key(certificate)!=publisher)return false;
            var directory=Path.GetDirectoryName(Path.GetFullPath(executable))!;
            var manifest=BoundedRead(Path.Combine(directory,"release-manifest.json"),2*1024*1024);
            var signature=BoundedRead(Path.Combine(directory,"release-manifest.p7s"),65536);
            return CheckManifest(manifest,signature,publisher,TrustedChain);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException or System.Security.SecurityException)
        {return false;}
    }
    private static byte[] BoundedRead(string path,int maximum)
    {
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(stream.Length is <=0 || stream.Length>maximum)throw new InvalidDataException("Invalid signature input size");
        var bytes=new byte[(int)stream.Length];stream.ReadExactly(bytes);return bytes;
    }
    internal static string Key(X509Certificate2 certificate)=>Convert.ToHexString(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));
    internal static bool CheckManifest(byte[] manifest,byte[] signature,string publisher,Func<X509Certificate2,bool> chain)
    {
        try
        {
            if(manifest.Length is <=0 or >2097152 || signature.Length is <=0 or >65536)return false;
            var cms=new SignedCms(new ContentInfo(manifest),true);cms.Decode(signature);
            if(!cms.Detached || !CryptographicOperations.FixedTimeEquals(cms.ContentInfo.Content,manifest) || cms.SignerInfos.Count!=1)return false;
            var signer=cms.SignerInfos[0];
            if(signer.DigestAlgorithm.Value is not ("2.16.840.1.101.3.4.2.1" or "2.16.840.1.101.3.4.2.2" or "2.16.840.1.101.3.4.2.3"))return false;
            using var certificate=signer.Certificate;
            if(certificate is null || Key(certificate)!=publisher)return false;
            cms.CheckSignature(true);
            return chain(certificate);
        }
        catch(CryptographicException){return false;}
    }
    private static bool TrustedChain(X509Certificate2 certificate)
    {
        using var chain=new X509Chain();
        chain.ChainPolicy.RevocationMode=X509RevocationMode.Offline;
        chain.ChainPolicy.RevocationFlag=X509RevocationFlag.ExcludeRoot;
        chain.ChainPolicy.DisableCertificateDownloads=true;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.3"));
        return chain.Build(certificate);
    }
    internal static X509Certificate2? TrustedCertificate(string path)
    {
        var fileName=Marshal.StringToCoTaskMemUni(path);var infoPointer=nint.Zero;
        var data=new TrustData{Size=(uint)Marshal.SizeOf<TrustData>(),UiChoice=2,UnionChoice=1,StateAction=1,ProviderFlags=0x1080|0x2000};
        var action=new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        try
        {
            var info=new FileInfoNative{Size=(uint)Marshal.SizeOf<FileInfoNative>(),Path=fileName};
            infoPointer=Marshal.AllocCoTaskMem(Marshal.SizeOf<FileInfoNative>());Marshal.StructureToPtr(info,infoPointer,false);data.File=infoPointer;
            if(WinVerifyTrust((nint)(-1),ref action,ref data)!=0)return null;
            var provider=WTHelperProvDataFromStateData(data.StateData);if(provider==0)return null;
            var signerPointer=WTHelperGetProvSignerFromChain(provider,0,false,0);if(signerPointer==0)return null;
            var signer=Marshal.PtrToStructure<SignerHeader>(signerPointer);
            if(signer.Size<Marshal.SizeOf<SignerHeader>() || signer.Count is 0 or >64 || signer.Certificates==0)return null;
            var certificate=Marshal.PtrToStructure<ProviderCertificateHeader>(signer.Certificates);
            if(certificate.Size<Marshal.SizeOf<ProviderCertificateHeader>() || certificate.Certificate==0)return null;
            var encoded=Marshal.PtrToStructure<CertificateHeader>(certificate.Certificate);
            if(encoded.Length is 0 or >65536 || encoded.Data==0)return null;
            var bytes=new byte[(int)encoded.Length];Marshal.Copy(encoded.Data,bytes,0,bytes.Length);
            return X509CertificateLoader.LoadCertificate(bytes);
        }
        finally
        {
            if(data.StateData!=0){data.StateAction=2;WinVerifyTrust((nint)(-1),ref action,ref data);}
            if(infoPointer!=0)Marshal.FreeCoTaskMem(infoPointer);Marshal.FreeCoTaskMem(fileName);
        }
    }
    [StructLayout(LayoutKind.Sequential)]internal struct FileInfoNative {public uint Size;public nint Path,File,Subject;}
    [StructLayout(LayoutKind.Sequential)]internal struct SignerHeader {public uint Size,TimeLow,TimeHigh,Count;public nint Certificates;}
    [StructLayout(LayoutKind.Sequential)]internal struct ProviderCertificateHeader {public uint Size;public nint Certificate;}
    [StructLayout(LayoutKind.Sequential)]internal struct CertificateHeader {public uint Encoding;public nint Data;public uint Length;}
    [StructLayout(LayoutKind.Sequential)]internal struct TrustData
    {
        public uint Size;public nint PolicyCallback,SipClient;public uint UiChoice,RevocationChecks,UnionChoice;
        public nint File;public uint StateAction;public nint StateData,UrlReference;public uint ProviderFlags,UiContext;public nint SignatureSettings;
    }
    [DllImport("wintrust.dll",ExactSpelling=true,PreserveSig=true)]private static extern int WinVerifyTrust(nint window,ref Guid action,ref TrustData data);
    [DllImport("wintrust.dll",ExactSpelling=true)]private static extern nint WTHelperProvDataFromStateData(nint state);
    [DllImport("wintrust.dll",ExactSpelling=true)]private static extern nint WTHelperGetProvSignerFromChain(nint provider,uint index,[MarshalAs(UnmanagedType.Bool)]bool countersigner,uint counterIndex);
}
