using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using CellBridge.Storage.Abstractions;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CellBridge.Authentication;

internal sealed class AuthenticationKeyCertificate : IDisposable
{
    private const string RelativeDefaultPath = "CellBridge/authentication-key-protection.pfx";

    private AuthenticationKeyCertificate(X509Certificate2 certificate, string path)
    {
        Certificate = certificate;
        Path = path;
    }

    public X509Certificate2 Certificate { get; }
    public string Path { get; }

    public static AuthenticationKeyCertificate Load(IConfiguration configuration)
    {
        string? configuredPath = configuration["Authentication:KeyProtection:CertificatePath"];
        string? password = configuration["Authentication:KeyProtection:Password"];
        string path;
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localData))
                throw new InvalidOperationException(
                    "Authentication key protection requires Authentication:KeyProtection:CertificatePath on this platform.");
            path = System.IO.Path.Combine(localData, RelativeDefaultPath);
            CreateLocalCertificateIfMissing(path);
        }
        else
        {
            path = System.IO.Path.GetFullPath(configuredPath);
            if (!File.Exists(path))
                throw new FileNotFoundException("The authentication key-protection certificate does not exist.", path);
        }

        ValidatePrivateDirectory(System.IO.Path.GetDirectoryName(path)!);
        ValidatePrivateFile(path);
        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password,
                X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (CryptographicException error)
        {
            throw new InvalidOperationException(
                "The authentication key-protection certificate could not be loaded. Check its password and format.", error);
        }

        try
        {
            ValidateCertificate(certificate);
            return new AuthenticationKeyCertificate(certificate, path);
        }
        catch
        {
            certificate.Dispose();
            throw;
        }
    }

    public void Dispose() => Certificate.Dispose();

    internal static void CreateLocalCertificateIfMissing(string path, Action<string>? flushDirectory = null)
    {
        flushDirectory ??= FlushCertificateDirectory;
        string directory = System.IO.Path.GetDirectoryName(path)!;
        ConfidentialFileSystem.CreateDirectory(directory);
        ValidatePrivateDirectory(directory);
        // Existing ancestors may have just been created by another process.
        // Persist every parent entry before committing secrets that need this key.
        var ancestors = new Stack<string>();
        for (var parent = System.IO.Path.GetDirectoryName(directory); parent is not null;
            parent = System.IO.Path.GetDirectoryName(parent))
            ancestors.Push(parent);
        while (ancestors.TryPop(out var ancestor))
            flushDirectory(ancestor);
        if (File.Exists(path))
        {
            // Another creator may have just published it without flushing yet.
            flushDirectory(directory);
            return;
        }

        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest("CN=CellBridge Local Data Protection", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(20));
        byte[] pfx = certificate.Export(X509ContentType.Pfx, string.Empty);
        string temporary = System.IO.Path.Combine(directory, ".authentication-key-protection-" +
            Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = ConfidentialFileSystem.CreateFile(temporary, FileAccess.Write,
                FileOptions.WriteThrough, bufferSize: 4096))
            {
                output.Write(pfx);
                output.Flush(flushToDisk: true);
            }
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { }
            // Flush on both success and the concurrent-creator path. Flushing
            // the PFX bytes alone does not persist the renamed directory entry.
            flushDirectory(directory);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void FlushCertificateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        // O_RDONLY can open a directory on supported Unix hosts without relying
        // on platform-specific O_DIRECTORY flag values.
        int descriptor = OpenDirectory(path, 0);
        if (descriptor < 0)
            throw new IOException("Cannot open the authentication certificate directory for synchronization.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        try
        {
            int result;
            do { result = FsyncDirectory(descriptor); }
            while (result != 0 && Marshal.GetLastPInvokeError() == 4); // EINTR
            if (result != 0)
                throw new IOException("Authentication certificate directory synchronization failed.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
        }
        finally { CloseDirectory(descriptor); }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenDirectory(string path, int flags);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int FsyncDirectory(int descriptor);
    [DllImport("libc", EntryPoint = "close")]
    private static extern int CloseDirectory(int descriptor);

    private static void ValidateCertificate(X509Certificate2 certificate)
    {
        DateTime now = DateTime.UtcNow;
        if (!certificate.HasPrivateKey)
            throw new InvalidOperationException("The authentication key-protection certificate has no private key.");
        if (now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime())
            throw new InvalidOperationException("The authentication key-protection certificate is not currently valid.");
        using RSA? rsa = certificate.GetRSAPrivateKey();
        if (rsa is null || rsa.KeySize < 2048)
            throw new InvalidOperationException(
                "The authentication key-protection certificate must have an RSA private key of at least 2048 bits.");
        var keyUsage = certificate.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault();
        if (keyUsage is not null && (keyUsage.KeyUsages & X509KeyUsageFlags.KeyEncipherment) == 0)
            throw new InvalidOperationException(
                "The authentication key-protection certificate does not permit key encipherment.");
    }

    private static void ValidatePrivateFile(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The authentication key-protection certificate path must not be a symbolic link.");
        if (OperatingSystem.IsWindows()) return;
        const UnixFileMode publicPermissions = UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
            UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite |
            UnixFileMode.OtherExecute;
        if ((File.GetUnixFileMode(path) & publicPermissions) != 0)
            throw new InvalidOperationException(
                "The authentication key-protection certificate must have owner-only Unix permissions.");
    }

    private static void ValidatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        const UnixFileMode publicWrite = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
        if ((File.GetUnixFileMode(path) & publicWrite) != 0)
            throw new InvalidOperationException(
                "The authentication key-protection certificate directory must not be writable by group or other users.");
    }
}

internal sealed class AuthenticationKeyProtector
{
    private static readonly XNamespace DataProtection = "http://schemas.asp.net/2015/03/dataProtection";
    private static readonly XName RequiresEncryption = DataProtection + "requiresEncryption";
    private static readonly XName EncryptedSecret = DataProtection + "encryptedSecret";
    private readonly CertificateXmlEncryptor _encryptor;

    public AuthenticationKeyProtector(AuthenticationKeyCertificate certificate, ILoggerFactory loggerFactory)
    {
        _encryptor = new CertificateXmlEncryptor(certificate.Certificate, loggerFactory);
    }

    public XElement Protect(XElement element)
    {
        var document = new XDocument(new XElement(element));
        while (document.Descendants().FirstOrDefault(RequiresProtection) is { } plaintext)
        {
            var encrypted = _encryptor.Encrypt(new XElement(plaintext));
            plaintext.ReplaceWith(new XElement(EncryptedSecret,
                new XAttribute("decryptorType", encrypted.DecryptorType.AssemblyQualifiedName!),
                encrypted.EncryptedElement));
        }
        XElement result = document.Root!;
        if (result.DescendantsAndSelf().Any(RequiresProtection) ||
            result.DescendantsAndSelf().Any(e => e.Name.LocalName == "masterKey"))
            throw new CryptographicException("Authentication key XML still contains unprotected secret material.");
        return result;
    }

    private static bool RequiresProtection(XElement element) =>
        string.Equals((string?)element.Attribute(RequiresEncryption), "true", StringComparison.OrdinalIgnoreCase);
}

internal sealed class AuthenticationKeyValidationService(IKeyManager keys) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var key in keys.GetAllKeys())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (key.CreateEncryptor() is null)
                throw new CryptographicException($"Authentication Data Protection key {key.KeyId} is not usable.");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
