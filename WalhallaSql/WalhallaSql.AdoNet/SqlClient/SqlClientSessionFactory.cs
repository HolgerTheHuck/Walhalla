using System;
using System.Collections.Generic;

namespace WalhallaSql.AdoNet.SqlClient;

public static class SqlClientSessionFactory
{
    public static ISqlClientSession Create(WalhallaEngine? engine, string connectionString)
    {
        return new WalhallaSqlClientSession(
            engine ?? throw new InvalidOperationException("InProcess transport requires a resolved engine instance (DataSource registry or EmbeddedPath/File)."));
    }

    private static int ParseAutoCommitBatchDelayMs(string connectionString)
    {
        var value = ExtractConnectionValue(connectionString, "AutoCommitBatchDelayMs");
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        return int.TryParse(value.Trim(), out var ms) && ms >= 0 ? ms : 0;
    }

    internal static SqlClientTransport GetConfiguredTransport(string connectionString)
    {
        return ParseTransport(connectionString);
    }

    private static SqlClientTransport ParseTransport(string connectionString)
    {
        var value = ExtractConnectionValue(connectionString, "Transport");
        if (string.IsNullOrWhiteSpace(value))
            return SqlClientTransport.InProcess;

        if (Enum.TryParse<SqlClientTransport>(value, ignoreCase: true, out var parsed))
            return parsed;

        throw new NotSupportedException(
            $"Transport '{value}' is not supported. Supported transports are InProcess and PgWire.");
    }

    private static string? ExtractConnectionValue(string connectionString, string key)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return null;

        var segments = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var segment in segments)
        {
            var parts = segment.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
                continue;

            if (parts[0].Equals(key, StringComparison.OrdinalIgnoreCase))
                return parts[1];
        }

        return null;
    }
}
