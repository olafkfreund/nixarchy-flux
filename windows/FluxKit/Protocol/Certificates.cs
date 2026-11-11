using System.Formats.Asn1;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace FluxKit;

// Protects the private key at rest. The Windows app uses DPAPI. Tests use
// PlainKeyProtector.
public interface IKeyProtector
{
    byte[] Protect(byte[] data);
    byte[] Unprotect(byte[] data);
}

// Keeps the bytes as they are, for tests and for platforms without DPAPI.
public sealed class PlainKeyProtector : IKeyProtector
{
    public byte[] Protect(byte[] data) => (byte[])data.Clone();
    public byte[] Unprotect(byte[] data) => (byte[])data.Clone();
}

// The certificate of this device. Its common name is the device ID.
public sealed class LocalCertificate : IDisposable
{
    const string FileName = "identity.bin";

    public string DeviceId { get; }
    public byte[] CertificateDer { get; }

    // The certificate with its private key, for TLS.
    public X509Certificate2 Certificate { get; }

    LocalCertificate(X509Certificate2 certificate)
    {
        Certificate = certificate;
        CertificateDer = certificate.RawData;
        DeviceId = certificate.GetNameInfo(X509NameType.SimpleName, false);
    }

    // Reads the certificate from the directory, or makes a new one there.
    // A file that does not read stops the start, because a new certificate
    // would end each pairing.
    public static LocalCertificate LoadOrCreate(string directory, IKeyProtector protector)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);
        if (File.Exists(path))
        {
            var pfx = protector.Unprotect(File.ReadAllBytes(path));
            try
            {
                var loaded = new LocalCertificate(Load(pfx));
                if (!Identity.ValidDeviceId(loaded.DeviceId))
                {
                    throw new InvalidDataException("The certificate of this device has no valid device ID.");
                }
                return loaded;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pfx);
            }
        }
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var id = Guid.NewGuid().ToString("N");
        var request = new CertificateRequest($"CN={id}, OU=Flux, O=Omarchy", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        var now = DateTimeOffset.UtcNow;
        using var created = request.CreateSelfSigned(now.AddYears(-1), now.AddYears(10));
        var bytes = created.Export(X509ContentType.Pkcs12, "");
        try
        {
            var certificate = new LocalCertificate(Load(bytes));
            FileStore.WriteAtomic(path, protector.Protect(bytes), secret: true);
            return certificate;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    // Schannel cannot use an ephemeral private key. UserKeySet imports a
    // temporary user key that the dispose removes. The file stays the only
    // durable copy.
    static X509Certificate2 Load(byte[] pfx) => X509CertificateLoader.LoadPkcs12(
        pfx, "", OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet);

    public void Dispose() => Certificate.Dispose();
}

public static class Certificates
{
    // The common name of a DER certificate, or null.
    public static string? CommonName(byte[] der)
    {
        try
        {
            using var c = X509CertificateLoader.LoadCertificate(der);
            var name = c.GetNameInfo(X509NameType.SimpleName, false);
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    // The SubjectPublicKeyInfo of a DER certificate, as the certificate has
    // it. fluxd hashes these exact bytes, so a new encoding of the key could
    // give another verification key.
    public static byte[] SubjectPublicKeyInfo(byte[] der)
    {
        var certificate = new AsnReader(der, AsnEncodingRules.DER).ReadSequence();
        var tbs = certificate.ReadSequence();
        if (tbs.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0, true)))
        {
            tbs.ReadEncodedValue();
        }
        tbs.ReadEncodedValue(); // serialNumber
        tbs.ReadEncodedValue(); // signature
        tbs.ReadEncodedValue(); // issuer
        tbs.ReadEncodedValue(); // validity
        tbs.ReadEncodedValue(); // subject
        return tbs.ReadEncodedValue().ToArray();
    }

    // The key that both devices show during a pairing: the SHA-256 of the
    // larger public key, the smaller public key, and the timestamp, as 16
    // uppercase hex digits. proto.VerificationKey in fluxd is the reference.
    public static string VerificationKey(byte[] ownCertificate, byte[] peerCertificate, long timestamp)
    {
        try
        {
            return VerificationKeyOfKeys(SubjectPublicKeyInfo(ownCertificate), SubjectPublicKeyInfo(peerCertificate), timestamp);
        }
        catch (AsnContentException)
        {
            return "";
        }
    }

    internal static string VerificationKeyOfKeys(byte[] a, byte[] b, long timestamp)
    {
        if (a.AsSpan().SequenceCompareTo(b) < 0)
        {
            (a, b) = (b, a);
        }
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(a);
        h.AppendData(b);
        if (timestamp > 0)
        {
            h.AppendData(Encoding.ASCII.GetBytes(timestamp.ToString(CultureInfo.InvariantCulture)));
        }
        return Convert.ToHexString(h.GetHashAndReset().AsSpan(0, 8));
    }

    // Shows a key as groups of 4 digits, for example "5EE6 825F 974E D59A".
    public static string FormatKey(string key) =>
        string.Join(" ", Enumerable.Range(0, key.Length / 4).Select(i => key.Substring(i * 4, 4)));
}
