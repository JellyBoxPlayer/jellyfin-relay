using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Jellyfin.Plugin.JellyboxRemote.Tunnel;

internal static class AgentCertificate
{
    public static X509Certificate2 LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            return X509CertificateLoader.LoadPkcs12FromFile(path, null);
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=JellyBox Remote Access", key, HashAlgorithmName.SHA256);
        var now = DateTimeOffset.UtcNow;
        using var created = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(30));
        var pfx = created.Export(X509ContentType.Pfx);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, pfx);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return X509CertificateLoader.LoadPkcs12(pfx, null);
    }

    public static string Fingerprint(X509Certificate2 certificate) =>
        Convert.ToHexStringLower(SHA256.HashData(certificate.RawData));
}
