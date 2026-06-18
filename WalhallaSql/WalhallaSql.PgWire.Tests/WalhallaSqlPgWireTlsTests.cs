using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Npgsql;
using WalhallaSql;
using WalhallaSql.PgWire;
using Xunit;

namespace WalhallaSql.PgWire.Tests;

/// <summary>
/// Tests verifying TLS encryption through the PgWire frontend.
/// </summary>
public class WalhallaSqlPgWireTlsTests
{
    [Fact]
    public async Task Tls_ConnectWithRequire_Succeeds()
    {
        await using var scope = await WalhallaSqlPgWireTlsTestScope.CreateAsync();
        var csb = new NpgsqlConnectionStringBuilder(scope.ConnectionString)
        {
            SslMode = SslMode.Require,
            TrustServerCertificate = true
        };

        await using var conn = new NpgsqlConnection(csb.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand("SELECT 1", conn);
        var result = await cmd.ExecuteScalarAsync();
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Tls_WithoutTlsOptions_ClientGetsN()
    {
        await using var scope = await WalhallaSqlPgWireTestScope.CreateAsync();
        var csb = new NpgsqlConnectionStringBuilder(scope.ConnectionString)
        {
            SslMode = SslMode.Prefer,
            TrustServerCertificate = true
        };

        await using var conn = new NpgsqlConnection(csb.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand("SELECT 1", conn);
        var result = await cmd.ExecuteScalarAsync();
        Assert.Equal(1, result);
    }
}

/// <summary>
/// Test helper that spins up a PgWireServer with a temporary self-signed certificate.
/// </summary>
internal sealed class WalhallaSqlPgWireTlsTestScope : IAsyncDisposable
{
    private readonly string _tempPath;
    private readonly WalhallaEngine _engine;
    private readonly PgWireServer _server;
    private readonly X509Certificate2 _certificate;
    private NpgsqlDataSource? _npgsqlDataSource;

    private WalhallaSqlPgWireTlsTestScope(
        string tempPath,
        WalhallaEngine engine,
        PgWireServer server,
        X509Certificate2 certificate)
    {
        _tempPath = tempPath;
        _engine = engine;
        _server = server;
        _certificate = certificate;
    }

    public string ConnectionString =>
        $"Host=127.0.0.1;Port={_server.BoundPort};Database=WalhallaSql;User Id=test;Password=test;Pooling=false;Timeout=5;Command Timeout=10";

    public static async Task<WalhallaSqlPgWireTlsTestScope> CreateAsync()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "WalhallaSqlPgWireTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempPath);

        var engine = new WalhallaEngine(new WalhallaOptions(tempPath));
        var backend = new WalhallaSqlPgWireBackend(engine);

        var cert = GenerateSelfSignedCertificate();
        var tlsOptions = new WalhallaSql.PgWire.Tls.PgWireTlsOptions
        {
            Certificate = cert
        };

        var server = new PgWireServer(backend, tlsOptions, host: "127.0.0.1", port: 0);
        await server.StartAsync();

        return new WalhallaSqlPgWireTlsTestScope(tempPath, engine, server, cert);
    }

    public async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        _npgsqlDataSource ??= NpgsqlDataSource.Create(ConnectionString);
        return await _npgsqlDataSource.OpenConnectionAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _npgsqlDataSource?.Dispose();
        await _server.DisposeAsync();
        _engine.Dispose();
        _certificate.Dispose();

        try { Directory.Delete(_tempPath, recursive: true); } catch { }
    }

    private static X509Certificate2 GenerateSelfSignedCertificate()
    {
        var subjectName = "CN=localhost";
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subjectName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(sanBuilder.Build());

        var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Export and re-import to get a certificate with a private key that works with SslStream
        var export = cert.Export(X509ContentType.Pfx);
        return new X509Certificate2(export);
    }
}
