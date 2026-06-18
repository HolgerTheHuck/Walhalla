using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;

namespace WalhallaSql.PgWire.Tls;

/// <summary>
/// TLS configuration for the PostgreSQL wire-protocol server.
/// </summary>
public sealed class PgWireTlsOptions
{
    /// <summary>
    /// The X.509 certificate used for TLS handshakes.
    /// Must include the private key for server authentication.
    /// </summary>
    public X509Certificate2? Certificate { get; set; }

    /// <summary>
    /// Minimum TLS protocol version. Defaults to TLS 1.2.
    /// </summary>
    public System.Security.Authentication.SslProtocols MinProtocolVersion { get; set; }
        = System.Security.Authentication.SslProtocols.Tls12;

    /// <summary>
    /// Loads a certificate from PEM files (certificate + private key).
    /// </summary>
    public static X509Certificate2 LoadFromPem(string certPath, string keyPath)
    {
        var certPem = File.ReadAllText(certPath);
        var keyPem = File.ReadAllText(keyPath);
        return X509Certificate2.CreateFromPem(certPem, keyPem);
    }

    /// <summary>
    /// Loads a certificate from the Windows certificate store by thumbprint.
    /// </summary>
    public static X509Certificate2? LoadFromStore(string thumbprint,
        StoreName storeName = StoreName.My,
        StoreLocation storeLocation = StoreLocation.LocalMachine)
    {
        using var store = new X509Store(storeName, storeLocation);
        store.Open(OpenFlags.ReadOnly);
        var certs = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        return certs.Count > 0 ? certs[0] : null;
    }
}
