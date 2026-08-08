using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Compendio.Hosting;

/// <summary>
/// The instance issues its own TLS certificate.
/// </summary>
/// <remarks>
/// <para>
/// "Supply a certificate for direct TLS" is a feature only for organizations that already have PKI.
/// An SMB with no public hostname has nothing to supply, so this closes the gap with
/// <see cref="CertificateRequest"/> from the BCL: no OpenSSL, no certificate authority, no internet,
/// no purchase.
/// </para>
/// <para>
/// Content encryption and this are unrelated and neither depends on the other. Certificates appear
/// in exactly this one place in the product.
/// </para>
/// </remarks>
public static class SelfSignedCertificates
{
    private const string FileName = "compendio-tls.pfx";
    private const int KeySizeBits = 3072;
    private static readonly TimeSpan Validity = TimeSpan.FromDays(730);

    public static string PathFor(DataDirectory dataDirectory) => Path.Combine(dataDirectory.TlsKeys, FileName);

    public static X509Certificate2? TryLoad(DataDirectory dataDirectory)
    {
        var path = PathFor(dataDirectory);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return X509CertificateLoader.LoadPkcs12FromFile(path, password: null,
                X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>
    /// Issues a certificate covering the host name, the FQDN and the machine's LAN addresses.
    /// </summary>
    /// <remarks>
    /// The LAN addresses matter: on a small network people reach the wiki by IP as often as by name,
    /// and a certificate that only covers the host name produces a warning on the URL they actually
    /// type. Which a self-signed certificate does anyway until it is trusted — the docs say so
    /// plainly, with the two commands to trust it.
    /// </remarks>
    public static X509Certificate2 Create(DataDirectory dataDirectory, IReadOnlyList<string> extraDnsNames)
    {
        Directory.CreateDirectory(dataDirectory.TlsKeys);

        var hostName = Dns.GetHostName();
        var subject = new X500DistinguishedName($"CN={hostName}, O={Domain.CompendioConstants.ProductName}");

        using var rsa = RSA.Create(KeySizeBits);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], critical: false)); // serverAuth

        var sans = new SubjectAlternativeNameBuilder();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddDns(string? name)
        {
            if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
            {
                sans.AddDnsName(name);
            }
        }

        AddDns(hostName);
        AddDns("localhost");
        AddDns(FullyQualifiedName(hostName));

        foreach (var name in extraDnsNames)
        {
            AddDns(name);
        }

        sans.AddIpAddress(IPAddress.Loopback);
        sans.AddIpAddress(IPAddress.IPv6Loopback);

        foreach (var address in LocalAddresses())
        {
            sans.AddIpAddress(address);
        }

        request.CertificateExtensions.Add(sans.Build());

        var now = DateTimeOffset.UtcNow;
        using var certificate = request.CreateSelfSigned(now.AddMinutes(-5), now.Add(Validity));

        var pfx = certificate.Export(X509ContentType.Pfx);
        var path = PathFor(dataDirectory);

        File.WriteAllBytes(path, pfx);
        RestrictToOwner(path);

        return X509CertificateLoader.LoadPkcs12(pfx, password: null,
            X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
    }

    /// <summary>Days until the certificate expires, or null when there is none. <c>doctor</c> warns at 30.</summary>
    public static int? DaysUntilExpiry(DataDirectory dataDirectory)
    {
        using var certificate = TryLoad(dataDirectory);
        return certificate is null ? null : (int)(certificate.NotAfter.ToUniversalTime() - DateTime.UtcNow).TotalDays;
    }

    private static string? FullyQualifiedName(string hostName)
    {
        try
        {
            var domain = IPGlobalProperties.GetIPGlobalProperties().DomainName;
            return string.IsNullOrWhiteSpace(domain) ? null : $"{hostName}.{domain}";
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    private static IEnumerable<IPAddress> LocalAddresses()
    {
        IEnumerable<NetworkInterface> interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback);
        }
        catch (NetworkInformationException)
        {
            yield break;
        }

        foreach (var adapter in interfaces)
        {
            foreach (var address in adapter.GetIPProperties().UnicastAddresses)
            {
                if (address.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    yield return address.Address;
                }
            }
        }
    }

    private static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // The private key is protected exactly as the master key is.
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
