using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using DlutTimetable.Windows;

namespace DlutTimetable.Windows.IntegrationTests;

public sealed class ReleaseSignatureTests
{
    [Fact]
    public void DetachedManifestBindsExactBytesAndPublisherWithoutChangingSystemTrust()
    {
        using var key=RSA.Create(2048);
        var request=new CertificateRequest("CN=DLUT synthetic signing check",key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true,false,0,true));
        using var certificate=request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddHours(1));
        var manifest="{\"synthetic\":true,\"sha256\":\"payload A\"}"u8.ToArray();
        var cms=new SignedCms(new ContentInfo(manifest),true);
        cms.ComputeSignature(new CmsSigner(certificate){DigestAlgorithm=new Oid("2.16.840.1.101.3.4.2.1"),IncludeOption=X509IncludeOption.EndCertOnly});
        var signature=cms.Encode();var publisher=ReleaseSignatureVerifier.Key(certificate);
        bool IsolatedTrust(X509Certificate2 leaf)
        {
            using var chain=new X509Chain();chain.ChainPolicy.TrustMode=X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(certificate);chain.ChainPolicy.RevocationMode=X509RevocationMode.NoCheck;
            return chain.Build(leaf);
        }
        Assert.True(ReleaseSignatureVerifier.CheckManifest(manifest,signature,publisher,IsolatedTrust));
        Assert.False(ReleaseSignatureVerifier.CheckManifest("changed DLL hash"u8.ToArray(),signature,publisher,IsolatedTrust));
        Assert.False(ReleaseSignatureVerifier.CheckManifest(manifest,signature,new string('0',64),IsolatedTrust));
        Assert.False(ReleaseSignatureVerifier.CheckManifest(manifest,signature,publisher,_=>false));
        Assert.False(ReleaseSignatureVerifier.CheckManifest(manifest,[1,2,3],publisher,IsolatedTrust));
    }
    [Fact]
    public void NativeTrustRejectsUnsignedInputAndUsesWindowsX64Layouts()
    {
        Assert.Equal(8,IntPtr.Size);
        Assert.Equal(88,Marshal.SizeOf<ReleaseSignatureVerifier.TrustData>());
        Assert.Equal(32,Marshal.SizeOf<ReleaseSignatureVerifier.FileInfoNative>());
        Assert.Equal(16,Marshal.OffsetOf<ReleaseSignatureVerifier.SignerHeader>(nameof(ReleaseSignatureVerifier.SignerHeader.Certificates)).ToInt32());
        Assert.Equal(8,Marshal.OffsetOf<ReleaseSignatureVerifier.ProviderCertificateHeader>(nameof(ReleaseSignatureVerifier.ProviderCertificateHeader.Certificate)).ToInt32());
        Assert.Null(ReleaseSignatureVerifier.TrustedCertificate(typeof(ReleaseSignatureTests).Assembly.Location));
    }
}
