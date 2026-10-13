using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using CellBridge.Authentication;
using CellBridge.Storage.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace CellBridge.Storage.Tests;

public sealed class AuthenticationKeyProtectionTests
{
    [Fact]
    public void CertificateDirectoryIsSynchronizedBeforeNewAndExistingKeysReturn()
    {
        var root = Path.Combine(Path.GetTempPath(), "cellbridge-certificate-publication-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "private");
        var path = Path.Combine(directory, "key.pfx");
        var synchronized = new List<string>();
        try
        {
            AuthenticationKeyCertificate.CreateLocalCertificateIfMissing(path, flushed =>
            {
                if (flushed == directory) Assert.True(File.Exists(path));
                synchronized.Add(flushed);
            });
            var expected = new Stack<string>();
            for (string? ancestor = directory; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
                expected.Push(ancestor);
            Assert.Equal(expected, synchronized);
            var original = File.ReadAllBytes(path);
            synchronized.Clear();
            AuthenticationKeyCertificate.CreateLocalCertificateIfMissing(path, synchronized.Add);
            Assert.Equal(expected, synchronized);
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Throws<IOException>(() => AuthenticationKeyCertificate.CreateLocalCertificateIfMissing(path,
                _ => throw new IOException("Injected directory synchronization failure.")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ConcurrentCreatorSynchronizesAncestorsEvenWhenAnotherCreatorMadeThem()
    {
        var root = Path.Combine(Path.GetTempPath(), "cellbridge-certificate-parent-race-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "private");
        var path = Path.Combine(directory, "key.pfx");
        using var firstCreatorPaused = new ManualResetEventSlim();
        using var releaseFirstCreator = new ManualResetEventSlim();
        Task? firstCreator = null;
        try
        {
            firstCreator = Task.Run(() => AuthenticationKeyCertificate.CreateLocalCertificateIfMissing(path, _ =>
            {
                firstCreatorPaused.Set();
                if (!releaseFirstCreator.Wait(TimeSpan.FromSeconds(30)))
                    throw new TimeoutException("The other certificate creator did not finish.");
            }));
            Assert.True(firstCreatorPaused.Wait(TimeSpan.FromSeconds(30)));
            var synchronized = new List<string>();
            AuthenticationKeyCertificate.CreateLocalCertificateIfMissing(path, synchronized.Add);
            Assert.Contains(root, synchronized);
            Assert.Contains(Path.GetDirectoryName(root)!, synchronized);
            Assert.Equal(directory, synchronized[^1]);
            Assert.True(File.Exists(path));
        }
        finally
        {
            releaseFirstCreator.Set();
            if (firstCreator is not null) await firstCreator;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentLocalCertificateCreatorsKeepOneValidDurablyPublishedKey()
    {
        var root = Path.Combine(Path.GetTempPath(), "cellbridge-certificate-race-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "key.pfx");
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
                AuthenticationKeyCertificate.CreateLocalCertificateIfMissing(path))));
            using var certificate = AuthenticationKeyCertificate.Load(Configuration("Host=localhost;Database=unused", path, ""));
            Assert.True(certificate.Certificate.HasPrivateKey);
            Assert.Equal(new[] { path }, Directory.GetFiles(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void MissingConfiguredCertificateFailsClosed()
    {
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.pfx");
        var configuration = Configuration("Host=localhost;Database=unused", missing, "unused");

        Assert.Throws<FileNotFoundException>(() => AuthenticationKeyCertificate.Load(configuration));
    }

    [Fact]
    public void WrongCertificatePasswordFailsClosed()
    {
        using var certificate = new TemporaryCertificate("correct-password");
        var configuration = Configuration("Host=localhost;Database=unused", certificate.Path, "wrong-password");

        Assert.Throws<InvalidOperationException>(() => AuthenticationKeyCertificate.Load(configuration));
    }

    [Fact]
    public void PublicCertificatePermissionsAreRejectedOnUnix()
    {
        if (OperatingSystem.IsWindows()) return;
        using var certificate = new TemporaryCertificate("test-password");
        File.SetUnixFileMode(certificate.Path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);

        var error = Assert.Throws<InvalidOperationException>(() =>
            AuthenticationKeyCertificate.Load(Configuration(
                "Host=localhost;Database=unused", certificate.Path, certificate.Password)));

        Assert.Contains("owner-only", error.Message);
    }

    [Fact]
    public void PublicCertificateDirectoryIsRejectedOnUnix()
    {
        if (OperatingSystem.IsWindows()) return;
        using var certificate = new TemporaryCertificate("test-password");
        File.SetUnixFileMode(certificate.DirectoryPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupWrite);

        var error = Assert.Throws<InvalidOperationException>(() =>
            AuthenticationKeyCertificate.Load(Configuration(
                "Host=localhost;Database=unused", certificate.Path, certificate.Password)));

        Assert.Contains("directory", error.Message);
    }

    [Fact]
    public void SymbolicLinkCertificateIsRejectedOnUnix()
    {
        if (OperatingSystem.IsWindows()) return;
        using var certificate = new TemporaryCertificate("test-password");
        string link = System.IO.Path.Combine(certificate.DirectoryPath, "linked-key-protection.pfx");
        File.CreateSymbolicLink(link, certificate.Path);
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                AuthenticationKeyCertificate.Load(Configuration(
                    "Host=localhost;Database=unused", link, certificate.Password)));

            Assert.Contains("symbolic link", error.Message);
        }
        finally { File.Delete(link); }
    }

    [Fact]
    public async Task CertificateWrapperPreservesExistingProtectedPayloadAcrossProviders()
    {
        var repository = new MemoryKeyRepository();
        const string applicationName = "CellBridge.Authentication.v1";
        string payload;
        using (ServiceProvider legacy = LegacyProvider(repository, applicationName))
            payload = legacy.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("compatibility-ticket").Protect("local:preserved-user");
        Assert.Single(repository.Elements);
        Assert.Contains("masterKey", repository.Elements[0].ToString());

        using var temporaryCertificate = new TemporaryCertificate("test-password");
        var configuration = Configuration(
            "Host=localhost;Database=unused", temporaryCertificate.Path, temporaryCertificate.Password);
        using var certificate = AuthenticationKeyCertificate.Load(configuration);
        var protector = new AuthenticationKeyProtector(certificate, NullLoggerFactory.Instance);
        repository.Elements[0] = protector.Protect(repository.Elements[0]);

        using ServiceProvider secured = CertificateProvider(repository, applicationName, certificate.Certificate);
        await new AuthenticationKeyValidationService(secured.GetRequiredService<IKeyManager>())
            .StartAsync(CancellationToken.None);
        Assert.Equal("local:preserved-user", secured.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("compatibility-ticket").Unprotect(payload));
        XNamespace dataProtection = "http://schemas.asp.net/2015/03/dataProtection";
        Assert.NotEmpty(repository.Elements[0].Descendants(dataProtection + "encryptedSecret"));
        Assert.DoesNotContain(repository.Elements[0].Descendants(), e => e.Name.LocalName == "masterKey");

        using var wrongTemporaryCertificate = new TemporaryCertificate("other-password");
        using var wrongCertificate = AuthenticationKeyCertificate.Load(Configuration(
            "Host=localhost;Database=unused", wrongTemporaryCertificate.Path, wrongTemporaryCertificate.Password));
        using ServiceProvider wrong = CertificateProvider(repository, applicationName, wrongCertificate.Certificate);
        await Assert.ThrowsAsync<CryptographicException>(() =>
            new AuthenticationKeyValidationService(wrong.GetRequiredService<IKeyManager>())
                .StartAsync(CancellationToken.None));
        Assert.Throws<CryptographicException>(() => wrong.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("compatibility-ticket").Unprotect(payload));
    }

    [PostgreSqlFact]
    public Task PlaintextKeysAreMigratedWithoutInvalidatingProtectedPayloads() =>
        BudgetAndQueryTests.WithDatabase(new(), async source =>
        {
            // DataSource.ConnectionString omits the password after connection.
            // Preserve test credentials while selecting the isolated database.
            var target = new NpgsqlConnectionStringBuilder(source.ConnectionString);
            string connectionString = new NpgsqlConnectionStringBuilder(
                Environment.GetEnvironmentVariable("ConnectionStrings__cellbridge")!)
                { Database = target.Database }.ConnectionString;
            await AuthenticationDatabase.InitializeAsync(connectionString);
            var legacyRepository = new TestDatabaseKeyRepository(connectionString);
            const string applicationName = "CellBridge.Authentication.v1";
            string protectedTicket;
            string keyId;
            using (ServiceProvider legacy = LegacyProvider(legacyRepository, applicationName))
            {
                var protector = legacy.GetRequiredService<IDataProtectionProvider>()
                    .CreateProtector("migration-ticket");
                protectedTicket = protector.Protect("local:preserved-user");
            }

            string plaintextXml = await ReadOnlyKeyXml(source);
            var plaintextKey = XElement.Parse(plaintextXml);
            keyId = (string)plaintextKey.Attribute("id")!;
            Assert.Contains("masterKey", plaintextXml);
            Assert.DoesNotContain("encryptedSecret", plaintextXml);

            using var certificate = new TemporaryCertificate("test-password");
            using (ServiceProvider secured = SecuredProvider(connectionString, certificate))
            {
                var protector = secured.GetRequiredService<IDataProtectionProvider>()
                    .CreateProtector("migration-ticket");
                Assert.Equal("local:preserved-user", protector.Unprotect(protectedTicket));
            }

            string encryptedXml = await ReadOnlyKeyXml(source);
            var encryptedKey = XElement.Parse(encryptedXml);
            XNamespace dataProtection = "http://schemas.asp.net/2015/03/dataProtection";
            Assert.Equal(keyId, (string?)encryptedKey.Attribute("id"));
            Assert.NotEmpty(encryptedKey.Descendants(dataProtection + "encryptedSecret"));
            Assert.DoesNotContain(encryptedKey.Descendants(), e => e.Name.LocalName == "masterKey");

            using (ServiceProvider peer = SecuredProvider(connectionString, certificate))
            {
                var protector = peer.GetRequiredService<IDataProtectionProvider>()
                    .CreateProtector("migration-ticket");
                Assert.Equal("local:preserved-user", protector.Unprotect(protectedTicket));
            }

            using var wrongCertificate = new TemporaryCertificate("other-password");
            using ServiceProvider wrong = SecuredProvider(connectionString, wrongCertificate);
            var wrongProtector = wrong.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("migration-ticket");
            Assert.Throws<CryptographicException>(() => wrongProtector.Unprotect(protectedTicket));
        });

    private static IConfiguration Configuration(string connectionString, string certificatePath, string password) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:cellbridge"] = connectionString,
            ["Authentication:PublicOrigin"] = "https://localhost",
            ["Authentication:KeyProtection:CertificatePath"] = certificatePath,
            ["Authentication:KeyProtection:Password"] = password,
        }).Build();

    private static ServiceProvider SecuredProvider(string connectionString, TemporaryCertificate certificate)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCellBridgeAuthentication(Configuration(connectionString, certificate.Path, certificate.Password));
        return services.BuildServiceProvider();
    }

    private static ServiceProvider LegacyProvider(IXmlRepository repository, string applicationName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().SetApplicationName(applicationName);
        services.AddOptions<KeyManagementOptions>().Configure(options => options.XmlRepository = repository);
        return services.BuildServiceProvider();
    }

    private static ServiceProvider CertificateProvider(IXmlRepository repository, string applicationName,
        X509Certificate2 certificate)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().SetApplicationName(applicationName).ProtectKeysWithCertificate(certificate);
        services.AddOptions<KeyManagementOptions>().Configure(options => options.XmlRepository = repository);
        return services.BuildServiceProvider();
    }

    private static async Task<string> ReadOnlyKeyXml(NpgsqlDataSource source)
    {
        await using var command = source.CreateCommand("SELECT \"Xml\" FROM cellbridge_auth_keys ORDER BY \"Id\"");
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        string xml = reader.GetString(0);
        Assert.False(await reader.ReadAsync());
        return xml;
    }

    private sealed class TestDatabaseKeyRepository(string connectionString) : IXmlRepository
    {
        private readonly DbContextOptions<AuthenticationDatabase> _options =
            new DbContextOptionsBuilder<AuthenticationDatabase>().UseNpgsql(connectionString).Options;

        public IReadOnlyCollection<XElement> GetAllElements()
        {
            using var context = new AuthenticationDatabase(_options);
            return context.Keys.AsNoTracking().OrderBy(k => k.Id).Select(k => k.Xml)
                .AsEnumerable().Select(XElement.Parse).ToArray();
        }

        public void StoreElement(XElement element, string friendlyName)
        {
            using var context = new AuthenticationDatabase(_options);
            context.Keys.Add(new() { Xml = element.ToString(SaveOptions.DisableFormatting) });
            context.SaveChanges();
        }
    }

    private sealed class MemoryKeyRepository : IXmlRepository
    {
        public List<XElement> Elements { get; } = [];

        public IReadOnlyCollection<XElement> GetAllElements() =>
            Elements.Select(element => new XElement(element)).ToArray();

        public void StoreElement(XElement element, string friendlyName) => Elements.Add(new XElement(element));
    }

    private sealed class TemporaryCertificate : IDisposable
    {
        private readonly string _directory;

        public TemporaryCertificate(string password)
        {
            Password = password;
            _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "cellbridge-auth-cert-" + Guid.NewGuid().ToString("N"));
            ConfidentialFileSystem.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "key-protection.pfx");
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=CellBridge Test Key Protection", rsa,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment, true));
            using var value = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddDays(1));
            byte[] bytes = value.Export(X509ContentType.Pfx, password);
            try
            {
                using var output = ConfidentialFileSystem.CreateFile(Path, FileAccess.Write,
                    FileOptions.WriteThrough, bufferSize: 4096);
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }

        public string Path { get; }
        public string Password { get; }
        public string DirectoryPath => _directory;

        public void Dispose()
        {
            if (File.Exists(Path)) File.Delete(Path);
            if (Directory.Exists(_directory)) Directory.Delete(_directory);
        }
    }
}
