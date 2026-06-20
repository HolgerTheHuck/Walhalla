using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace WalhallaSql.Catalog;

/// <summary>
/// In-memory catalog of database authentication identities (roles / users).
/// Persisted as <c>authid.json</c> in the database root path.
/// SCRAM-SHA-256 hashes are stored in Postgres-compatible format:
/// <c>SCRAM-SHA-256$&lt;iter&gt;:&lt;salt&gt;$&lt;storedkey&gt;:&lt;serverkey&gt;</c>.
/// </summary>
public sealed class AuthIdCatalog
{
    private readonly string? _persistPath;
    private readonly Dictionary<string, AuthIdEntry> _entries = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    /// <summary>
    /// Creates a catalog that persists to <paramref name="rootPath"/>.
    /// Pass <c>null</c> for an in-memory-only catalog.
    /// </summary>
    public AuthIdCatalog(string? rootPath)
    {
        if (!string.IsNullOrEmpty(rootPath))
        {
            _persistPath = Path.Combine(rootPath, "authid.json");
            Load();
        }
    }

    /// <summary>
    /// Returns the number of entries in the catalog.
    /// </summary>
    public int Count
    {
        get { lock (_sync) return _entries.Count; }
    }

    /// <summary>
    /// Creates a role with a SCRAM-SHA-256 hashed password.
    /// </summary>
    public void CreateRole(string rolname, string plainPassword, int iterations = 4096)
        => CreateRole(rolname, plainPassword, canLogin: true, isSuperuser: false, iterations);

    /// <summary>
    /// Creates a role with a SCRAM-SHA-256 hashed password and role flags.
    /// </summary>
    public void CreateRole(string rolname, string plainPassword, bool canLogin, bool isSuperuser, int iterations = 4096)
    {
        if (string.IsNullOrWhiteSpace(rolname))
            throw new ArgumentException("Role name must not be empty.", nameof(rolname));
        if (string.IsNullOrEmpty(plainPassword))
            throw new ArgumentException("Password must not be empty.", nameof(plainPassword));

        var hash = ScramHashPassword(plainPassword, iterations);
        lock (_sync)
        {
            _entries[rolname] = new AuthIdEntry(rolname, hash, canLogin, isSuperuser);
            SaveLocked();
        }
    }

    /// <summary>
    /// Changes the password of an existing role.
    /// </summary>
    public bool AlterRolePassword(string rolname, string plainPassword, int iterations = 4096)
    {
        if (string.IsNullOrWhiteSpace(rolname))
            throw new ArgumentException("Role name must not be empty.", nameof(rolname));
        if (string.IsNullOrEmpty(plainPassword))
            throw new ArgumentException("Password must not be empty.", nameof(plainPassword));

        var hash = ScramHashPassword(plainPassword, iterations);
        lock (_sync)
        {
            if (!_entries.TryGetValue(rolname, out var existing))
                return false;

            _entries[rolname] = existing with { Rolpassword = hash };
            SaveLocked();
            return true;
        }
    }

    /// <summary>
    /// Changes the superuser flag of an existing role.
    /// </summary>
    public bool AlterRoleSuperuser(string rolname, bool isSuperuser)
    {
        if (string.IsNullOrWhiteSpace(rolname))
            throw new ArgumentException("Role name must not be empty.", nameof(rolname));

        lock (_sync)
        {
            if (!_entries.TryGetValue(rolname, out var existing))
                return false;

            _entries[rolname] = existing with { IsSuperuser = isSuperuser };
            SaveLocked();
            return true;
        }
    }

    /// <summary>
    /// Drops a role from the catalog.
    /// </summary>
    public bool DropRole(string rolname)
    {
        lock (_sync)
        {
            if (!_entries.Remove(rolname))
                return false;
            SaveLocked();
            return true;
        }
    }

    /// <summary>
    /// Attempts to retrieve the stored SCRAM data for a role.
    /// </summary>
    public bool TryGetRole(string rolname, out AuthIdEntry entry)
    {
        lock (_sync)
        {
            return _entries.TryGetValue(rolname, out entry);
        }
    }

    /// <summary>
    /// Returns all role names.
    /// </summary>
    public IReadOnlyList<string> GetRoleNames()
    {
        lock (_sync)
        {
            return _entries.Keys.ToList();
        }
    }

    /// <summary>
    /// Parses the stored SCRAM hash string into its components.
    /// </summary>
    public static bool TryParseScramHash(string hashString, out int iterations, out byte[] salt, out byte[] storedKey, out byte[] serverKey)
    {
        iterations = 0;
        salt = Array.Empty<byte>();
        storedKey = Array.Empty<byte>();
        serverKey = Array.Empty<byte>();

        if (string.IsNullOrEmpty(hashString))
            return false;

        // Format: SCRAM-SHA-256$<iter>:<salt>$<storedkey>:<serverkey>
        const string prefix = "SCRAM-SHA-256$";
        if (!hashString.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var rest = hashString.Substring(prefix.Length);
        var dollarIdx = rest.IndexOf('$');
        if (dollarIdx < 0)
            return false;

        var iterSalt = rest.Substring(0, dollarIdx);
        var keys = rest.Substring(dollarIdx + 1);

        var colonIdx = iterSalt.IndexOf(':');
        if (colonIdx < 0)
            return false;

        if (!int.TryParse(iterSalt.Substring(0, colonIdx), out iterations))
            return false;

        var saltB64 = iterSalt.Substring(colonIdx + 1);
        if (!TryFromBase64(saltB64, out salt))
            return false;

        var keyColonIdx = keys.IndexOf(':');
        if (keyColonIdx < 0)
            return false;

        var storedKeyB64 = keys.Substring(0, keyColonIdx);
        var serverKeyB64 = keys.Substring(keyColonIdx + 1);

        if (!TryFromBase64(storedKeyB64, out storedKey))
            return false;
        if (!TryFromBase64(serverKeyB64, out serverKey))
            return false;

        return true;
    }

    /// <summary>
    /// Generates a SCRAM-SHA-256 hash string from a plain-text password.
    /// </summary>
    public static string ScramHashPassword(string plainPassword, int iterations = 4096)
    {
        var salt = new byte[16];
        RandomNumberGenerator.Fill(salt);

        var saltedPassword = Rfc2898DeriveBytes.Pbkdf2(
            plainPassword, salt, iterations, HashAlgorithmName.SHA256, 32);

        var clientKey = HmacSha256(saltedPassword, Encoding.UTF8.GetBytes("Client Key"));
        var storedKey = SHA256.HashData(clientKey);
        var serverKey = HmacSha256(saltedPassword, Encoding.UTF8.GetBytes("Server Key"));

        return $"SCRAM-SHA-256${iterations}:{ToBase64(salt)}${ToBase64(storedKey)}:{ToBase64(serverKey)}";
    }

    /// <summary>
    /// Verifies a SCRAM client proof against stored data.
    /// </summary>
    public static bool VerifyClientProof(
        string hashString,
        string clientFirstBare,
        string serverFirst,
        string clientFinalWithoutProof,
        ReadOnlySpan<byte> clientProof)
    {
        if (!TryParseScramHash(hashString, out _, out _, out var storedKey, out _))
            return false;

        var authMessage = Encoding.UTF8.GetBytes(
            $"{clientFirstBare},{serverFirst},{clientFinalWithoutProof}");

        var clientSignature = HmacSha256(storedKey, authMessage);
        var computedClientKey = new byte[32];
        for (int i = 0; i < 32; i++)
            computedClientKey[i] = (byte)(clientProof[i] ^ clientSignature[i]);

        var computedStoredKey = SHA256.HashData(computedClientKey);
        return CryptographicOperations.FixedTimeEquals(computedStoredKey, storedKey);
    }

    /// <summary>
    /// Computes the server signature for the SCRAM final message.
    /// </summary>
    public static byte[] ComputeServerSignature(
        string hashString,
        string clientFirstBare,
        string serverFirst,
        string clientFinalWithoutProof)
    {
        if (!TryParseScramHash(hashString, out _, out _, out _, out var serverKey))
            throw new InvalidOperationException("Invalid SCRAM hash.");

        var authMessage = Encoding.UTF8.GetBytes(
            $"{clientFirstBare},{serverFirst},{clientFinalWithoutProof}");

        return HmacSha256(serverKey, authMessage);
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    private void Load()
    {
        if (_persistPath == null || !File.Exists(_persistPath))
            return;

        try
        {
            var json = File.ReadAllText(_persistPath);
            var dtos = JsonSerializer.Deserialize<List<AuthIdEntryDto>>(json);
            if (dtos == null) return;

            lock (_sync)
            {
                _entries.Clear();
                foreach (var dto in dtos)
                {
                    if (!string.IsNullOrWhiteSpace(dto.Rolname) && !string.IsNullOrEmpty(dto.Rolpassword))
                        _entries[dto.Rolname] = new AuthIdEntry(
                            dto.Rolname,
                            dto.Rolpassword,
                            dto.CanLogin,
                            dto.IsSuperuser);
                }
            }
        }
        catch
        {
            // Best-effort: if file is corrupt, start empty.
        }
    }

    private void SaveLocked()
    {
        if (_persistPath == null) return;

        var dtos = _entries.Values.Select(e => new AuthIdEntryDto(e.Rolname, e.Rolpassword, e.CanLogin, e.IsSuperuser)).ToList();
        var json = JsonSerializer.Serialize(dtos, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var dir = Path.GetDirectoryName(_persistPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(_persistPath, json);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static byte[] HmacSha256(byte[] key, byte[] message)
    {
        return HMACSHA256.HashData(key, message);
    }

    private static string ToBase64(byte[] data)
        => Convert.ToBase64String(data);

    private static bool TryFromBase64(string input, out byte[] result)
    {
        result = Array.Empty<byte>();
        if (string.IsNullOrEmpty(input))
            return false;

        // Re-pad if necessary
        var padded = input;
        switch (input.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }

        try
        {
            result = Convert.FromBase64String(padded);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private sealed record AuthIdEntryDto(string Rolname, string Rolpassword, bool CanLogin = true, bool IsSuperuser = false);
}

/// <summary>
/// A single authentication identity entry.
/// </summary>
public sealed record AuthIdEntry(string Rolname, string Rolpassword, bool CanLogin = true, bool IsSuperuser = false);
