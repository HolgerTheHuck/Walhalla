using System;
using System.Buffers.Text;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using WalhallaSql.Catalog;

namespace WalhallaSql.PgWire.Auth;

/// <summary>
/// RFC 7677 SCRAM-SHA-256 SASL server-side implementation for PostgreSQL wire protocol.
/// </summary>
public sealed class ScramSha256Server
{
    private readonly string _storedHash;
    private string? _clientFirstBare;
    private string? _serverFirst;
    private string? _clientFinalWithoutProof;
    private byte[]? _serverSignature;
    private bool _completed;

    /// <summary>
    /// Creates a SCRAM server session for the given stored hash.
    /// The hash must be in Postgres SCRAM format:
    /// <c>SCRAM-SHA-256$&lt;iter&gt;:&lt;salt&gt;$&lt;storedkey&gt;:&lt;serverkey&gt;</c>.
    /// </summary>
    public ScramSha256Server(string storedHash)
    {
        _storedHash = storedHash ?? throw new ArgumentNullException(nameof(storedHash));
    }

    /// <summary>
    /// Processes the client-first-message and returns the server-first-message.
    /// Throws <see cref="InvalidOperationException"/> on invalid client input.
    /// </summary>
    public string Begin(string clientFirstMessage)
    {
        if (string.IsNullOrEmpty(clientFirstMessage))
            throw new InvalidOperationException("Empty client-first-message.");

        // Parse gs2-header and client-first-message-bare.
        // client-first-message = gs2-header client-first-message-bare
        var (gs2Header, clientFirstBare) = SplitClientFirst(clientFirstMessage);
        _clientFirstBare = clientFirstBare;

        // Parse username and client nonce from client-first-message-bare.
        var attrs = ParseAttributes(clientFirstBare);
        if (!attrs.TryGetValue("n", out var username))
            throw new InvalidOperationException("Missing username in client-first-message.");
        if (!attrs.TryGetValue("r", out var clientNonce))
            throw new InvalidOperationException("Missing nonce in client-first-message.");
        if (string.IsNullOrEmpty(clientNonce))
            throw new InvalidOperationException("Empty client nonce.");

        // Parse stored SCRAM hash.
        if (!AuthIdCatalog.TryParseScramHash(_storedHash, out var iterations, out var salt, out _, out _))
            throw new InvalidOperationException("Invalid stored SCRAM hash.");

        // Generate server nonce by appending random bytes to client nonce.
        var serverNoncePart = GenerateNonce(24);
        var nonce = clientNonce + serverNoncePart;

        // Build server-first-message.
        _serverFirst = $"r={nonce},s={ToBase64(salt)},i={iterations}";
        return _serverFirst;
    }

    /// <summary>
    /// Processes the client-final-message and returns the server-final-message.
    /// Throws <see cref="InvalidOperationException"/> on invalid client input or failed verification.
    /// </summary>
    public string Continue(string clientFinalMessage)
    {
        if (string.IsNullOrEmpty(clientFinalMessage))
            throw new InvalidOperationException("Empty client-final-message.");
        if (_clientFirstBare == null || _serverFirst == null)
            throw new InvalidOperationException("SCRAM handshake not started.");
        if (_completed)
            throw new InvalidOperationException("SCRAM handshake already completed.");

        // client-final-message = client-final-message-without-proof "," proof
        var proofIdx = clientFinalMessage.LastIndexOf(",p=", StringComparison.Ordinal);
        if (proofIdx < 0)
            throw new InvalidOperationException("Missing proof in client-final-message.");

        _clientFinalWithoutProof = clientFinalMessage.Substring(0, proofIdx);
        var proofB64 = clientFinalMessage.Substring(proofIdx + 3);
        if (!TryFromBase64(proofB64, out var clientProof) || clientProof.Length == 0)
            throw new InvalidOperationException("Invalid client proof encoding.");

        // Verify the client proof.
        if (!AuthIdCatalog.VerifyClientProof(
            _storedHash,
            _clientFirstBare,
            _serverFirst,
            _clientFinalWithoutProof,
            clientProof))
        {
            throw new InvalidOperationException("SCRAM authentication failed: invalid client proof.");
        }

        // Compute server signature.
        _serverSignature = AuthIdCatalog.ComputeServerSignature(
            _storedHash,
            _clientFirstBare,
            _serverFirst,
            _clientFinalWithoutProof);

        _completed = true;
        return $"v={ToBase64(_serverSignature)}";
    }

    /// <summary>
    /// Returns true if the handshake has completed successfully.
    /// </summary>
    public bool IsCompleted => _completed;

    // ── Parsing helpers ───────────────────────────────────────────────────────

    private static (string Gs2Header, string ClientFirstBare) SplitClientFirst(string message)
    {
        // gs2-header starts with 'n', 'y', or 'p'
        if (message.Length < 3)
            throw new InvalidOperationException("Invalid client-first-message: too short.");

        var firstChar = message[0];
        if (firstChar != 'n' && firstChar != 'y' && firstChar != 'p')
            throw new InvalidOperationException("Invalid GS2 header in client-first-message.");

        // gs2-header ends after the second comma: "n,," or "y,," or "p=tls-unique,,"
        var commaCount = 0;
        var gs2End = 0;
        for (int i = 0; i < message.Length; i++)
        {
            if (message[i] == ',')
            {
                commaCount++;
                if (commaCount == 2)
                {
                    gs2End = i + 1;
                    break;
                }
            }
        }

        if (commaCount != 2)
            throw new InvalidOperationException("Invalid GS2 header: expected two commas.");

        var gs2Header = message.Substring(0, gs2End);
        var clientFirstBare = message.Substring(gs2End);

        if (string.IsNullOrEmpty(clientFirstBare))
            throw new InvalidOperationException("Empty client-first-message-bare.");

        return (gs2Header, clientFirstBare);
    }

    private static Dictionary<string, string> ParseAttributes(string message)
    {
        var result = new Dictionary<string, string>();
        var parts = message.Split(',');
        foreach (var part in parts)
        {
            if (part.Length < 2 || part[1] != '=')
                continue;
            var key = part.Substring(0, 1);
            var value = part.Substring(2);
            result[key] = value;
        }
        return result;
    }

    private static string GenerateNonce(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        var bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        var sb = new StringBuilder(length);
        foreach (var b in bytes)
            sb.Append(chars[b % chars.Length]);
        return sb.ToString();
    }

    private static string ToBase64(byte[] data)
        => Convert.ToBase64String(data);

    private static bool TryFromBase64(string input, out byte[] result)
    {
        result = Array.Empty<byte>();
        if (string.IsNullOrEmpty(input))
            return false;

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
}
