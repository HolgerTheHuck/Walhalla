using System.Buffers.Binary;
using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WalhallaSql;
using WalhallaSql.AdoNet;
using WalhallaSql.PgWire;

var options = PgWireOptions.Parse(args);
PgWireTrace.Enabled = options.Trace;
Directory.CreateDirectory(options.Path);

using var engine = WalhallaEngine.Open(options.Path);
// Backend uses engine directly; no connection registry needed.


var backend = new WalhallaSqlPgWireBackend(engine);
await using var server = new PgWireServer(backend, options.Host, options.Port);
await server.StartAsync();

Console.WriteLine($"WalhallaSql PgWire listening on {options.Host}:{server.BoundPort} (Database={options.Database}, Path={options.Path})");
if (options.Trace)
    Console.WriteLine("PgWire trace enabled (--trace)");

await Task.Delay(Timeout.Infinite);

// Legacy protocol implementation � kept for reference until all tests pass against PgWireServer.
#pragma warning disable CS8321
static async Task HandleClientAsync(TcpClient client, string connectionString)
{
    using var _ = client;
    using var stream = client.GetStream();

    using var connection = new WalhallaSqlDbConnection(connectionString);
    connection.Open();

    DbSessionState session = new();

    try
    {
        if (!await HandleStartupAsync(stream))
            return;

        await SendAuthenticationOkAsync(stream);
        await SendParameterStatusAsync(stream, "server_version", "16.0-layeredsql");
        await SendParameterStatusAsync(stream, "server_encoding", "UTF8");
        await SendParameterStatusAsync(stream, "client_encoding", "UTF8");
        await SendParameterStatusAsync(stream, "DateStyle", "ISO, MDY");
        await SendParameterStatusAsync(stream, "integer_datetimes", "on");
        await SendParameterStatusAsync(stream, "standard_conforming_strings", "on");
        await SendBackendKeyDataAsync(stream);
        await SendReadyForQueryAsync(stream, session.Transaction == null ? (byte)'I' : (byte)'T');

        while (true)
        {
            var messageType = await ReadByteAsync(stream);
            if (messageType == null)
                break;

            var length = await ReadInt32Async(stream);
            if (length < 4)
                throw new InvalidOperationException("Invalid frontend message length.");

            var payload = await ReadExactlyAsync(stream, length - 4);

            var type = (char)messageType.Value;
            PgWireTrace.Frontend(type, length, payload.Length);

            if (session.IgnoreUntilSync && type != 'S' && type != 'X')
                continue;

            try
            {
                switch (type)
                {
                    case 'Q':
                        await HandleSimpleQueryAsync(stream, connection, session, payload);
                        break;

                    case 'P':
                        HandleParse(session, payload);
                        await SendParseCompleteAsync(stream);
                        break;

                    case 'B':
                        HandleBind(session, payload);
                        await SendBindCompleteAsync(stream);
                        break;

                    case 'D':
                        await HandleDescribeAsync(stream, connection, session, payload);
                        break;

                    case 'E':
                        await HandleExecuteAsync(stream, connection, session, payload);
                        break;

                    case 'C':
                        HandleClose(session, payload);
                        await SendCloseCompleteAsync(stream);
                        break;

                    case 'H':
                        break;

                    case 'S':
                        session.IgnoreUntilSync = false;
                        await SendReadyForQueryAsync(stream, session.Transaction == null ? (byte)'I' : (byte)'T');
                        break;

                    case 'X':
                        return;

                    default:
                        await SendErrorAsync(stream, "0A000", $"Frontend message '{type}' is not supported in PgWire MVP.");
                        session.IgnoreUntilSync = true;
                        break;
                }
            }
            catch (Exception ex)
            {
                await SendErrorAsync(stream, "XX000", ex.Message);
                session.IgnoreUntilSync = true;
            }
        }
    }
    catch (Exception ex)
    {
        try
        {
            await SendErrorAsync(stream, "XX000", ex.Message);
            await SendReadyForQueryAsync(stream, session.Transaction == null ? (byte)'I' : (byte)'T');
        }
        catch
        {
        }
    }
    finally
    {
        if (session.Transaction != null)
        {
            try
            {
                session.Transaction.Rollback();
            }
            catch
            {
            }

            session.Transaction.Dispose();
        }
    }
}

static async Task<bool> HandleStartupAsync(NetworkStream stream)
{
    while (true)
    {
        var lenBytes = await ReadExactlyAsync(stream, 4);
        var length = BinaryPrimitives.ReadInt32BigEndian(lenBytes);
        if (length < 8)
            throw new InvalidOperationException("Invalid startup packet length.");

        var body = await ReadExactlyAsync(stream, length - 4);
        var requestCode = BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(0, 4));
        PgWireTrace.StartupPacket(requestCode, length);

        const int ProtocolV3 = 196608;
        const int SslRequest = 80877103;
        const int CancelRequest = 80877102;

        if (requestCode == SslRequest)
        {
            await stream.WriteAsync(new byte[] { (byte)'N' });
            await stream.FlushAsync();
            PgWireTrace.RawOutbound('N', 1);
            continue;
        }

        if (requestCode == CancelRequest)
            return false;

        if (requestCode != ProtocolV3)
            throw new InvalidOperationException($"Unsupported protocol version/code: {requestCode}.");

        return true;
    }
}

static async Task HandleSimpleQueryAsync(NetworkStream stream, WalhallaSqlDbConnection connection, DbSessionState session, byte[] payload)
{
    var sqlText = DecodeCStringPayload(payload);
    PgWireTrace.Sql("SIMPLE", sqlText);
    var statements = SplitSqlStatements(sqlText)
        .Where(statement => !string.IsNullOrWhiteSpace(statement))
        .ToArray();

    string? currentStatement = null;

    try
    {
        foreach (var statement in statements)
        {
            var trimmed = NormalizeSqlForExecution(statement.Trim());
            currentStatement = trimmed;

            if (IsBegin(trimmed))
            {
                if (session.Transaction == null)
                    session.Transaction = connection.BeginTransaction();

                await SendCommandCompleteAsync(stream, "BEGIN");
                continue;
            }

            if (IsCommit(trimmed))
            {
                if (session.Transaction != null)
                {
                    session.Transaction.Commit();
                    session.Transaction.Dispose();
                    session.Transaction = null;
                }

                await SendCommandCompleteAsync(stream, "COMMIT");
                continue;
            }

            if (IsRollback(trimmed))
            {
                if (session.Transaction != null)
                {
                    session.Transaction.Rollback();
                    session.Transaction.Dispose();
                    session.Transaction = null;
                }

                await SendCommandCompleteAsync(stream, "ROLLBACK");
                continue;
            }

            if (IsSetOrShow(trimmed))
            {
                if (trimmed.StartsWith("SHOW", StringComparison.OrdinalIgnoreCase))
                {
                    await SendRowDescriptionAsync(stream, new[] { ("setting", typeof(string)) });
                    await SendDataRowAsync(stream, new[] { "layeredsql" });
                    await SendCommandCompleteAsync(stream, "SHOW 1");
                }
                else
                {
                    await SendCommandCompleteAsync(stream, "SET");
                }

                continue;
            }

            if (TryResolveVirtualQuery(trimmed, connection.Database, DiscoverTableDefinitions(connection), out var virtualResult))
            {
                await SendVirtualQueryResultAsync(stream, virtualResult);
                continue;
            }

            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = NormalizeSqlForExecution(trimmed);
                if (session.Transaction != null)
                    command.Transaction = session.Transaction;

                if (LooksLikeSelect(trimmed))
                {
                    using var reader = command.ExecuteReader();

                    if (TryExpandSingleJsonColumnResult(reader, out var expandedFields, out var expandedRows))
                    {
                        await SendRowDescriptionAsync(stream, expandedFields);

                        foreach (var expandedRow in expandedRows)
                            await SendDataRowAsync(stream, expandedRow);

                        await SendCommandCompleteAsync(stream, $"SELECT {expandedRows.Count}");
                        continue;
                    }

                    var fields = new (string Name, Type ClrType)[reader.FieldCount];
                    for (var i = 0; i < reader.FieldCount; i++)
                        fields[i] = (reader.GetName(i), reader.GetFieldType(i));

                    await SendRowDescriptionAsync(stream, fields);

                    var rowCount = 0;
                    while (reader.Read())
                    {
                        var row = new string?[reader.FieldCount];
                        for (var i = 0; i < reader.FieldCount; i++)
                        {
                            if (reader.IsDBNull(i))
                            {
                                row[i] = null;
                                continue;
                            }

                            row[i] = ToPgText(reader.GetValue(i));
                        }

                        await SendDataRowAsync(stream, row);
                        rowCount++;
                    }

                    await SendCommandCompleteAsync(stream, $"SELECT {rowCount}");
                }
                else
                {
                    var affected = command.ExecuteNonQuery();
                    var tag = BuildCommandTag(trimmed, affected);
                    await SendCommandCompleteAsync(stream, tag);
                }
            }
            catch (Exception ex) when (LooksLikeSelect(trimmed) && IsCannotInferCollectionError(ex))
            {
                await SendRowDescriptionAsync(stream, Array.Empty<(string Name, Type ClrType)>());
                await SendCommandCompleteAsync(stream, "SELECT 0");
            }
        }
    }
    catch (Exception ex)
    {
        var sqlContext = string.IsNullOrWhiteSpace(currentStatement) ? string.Empty : $" SQL=[{currentStatement}]";
        await SendErrorAsync(stream, "XX000", ex.Message + sqlContext);
    }

    await SendReadyForQueryAsync(stream, session.Transaction == null ? (byte)'I' : (byte)'T');
}

static void HandleParse(DbSessionState session, byte[] payload)
{
    var reader = new PgPayloadReader(payload);
    var statementName = reader.ReadCString();
    var sql = reader.ReadCString();
    PgWireTrace.Sql("PARSE", sql);

    var parameterTypeCount = reader.ReadInt16();
    var parameterTypes = new int[parameterTypeCount];
    for (var i = 0; i < parameterTypeCount; i++)
        parameterTypes[i] = reader.ReadInt32();

    var key = NormalizeName(statementName);
    session.PreparedStatements[key] = new PreparedStatement(sql, parameterTypes);
}

static void HandleBind(DbSessionState session, byte[] payload)
{
    var reader = new PgPayloadReader(payload);
    var portalName = NormalizeName(reader.ReadCString());
    var statementName = NormalizeName(reader.ReadCString());

    if (!session.PreparedStatements.TryGetValue(statementName, out var prepared))
        throw new InvalidOperationException($"Prepared statement '{statementName}' not found.");

    var formatCodes = ReadFormatCodes(reader);

    var parameterCount = reader.ReadInt16();
    var parameterLiterals = new string?[parameterCount];
    for (var i = 0; i < parameterCount; i++)
    {
        var len = reader.ReadInt32();
        if (len < 0)
        {
            parameterLiterals[i] = "NULL";
            continue;
        }

        var raw = reader.ReadBytes(len);
        var formatCode = ResolveFormatCode(formatCodes, i);
        var parameterTypeOid = i < prepared.ParameterTypeOids.Count ? prepared.ParameterTypeOids[i] : 0;
        parameterLiterals[i] = DecodeBindParameterLiteral(raw, formatCode, parameterTypeOid);
    }

    var resultFormatCodes = ReadFormatCodes(reader);
    _ = resultFormatCodes;

    var sql = NormalizeSqlForExecution(RenderSqlWithParameters(prepared.Sql, parameterLiterals));
    var isQuery = ReturnsRows(sql);
    session.Portals[portalName] = new BoundPortal(sql, isQuery, false);
}

static async Task HandleDescribeAsync(NetworkStream stream, WalhallaSqlDbConnection connection, DbSessionState session, byte[] payload)
{
    var reader = new PgPayloadReader(payload);
    var targetType = (char)reader.ReadByte();
    var name = NormalizeName(reader.ReadCString());

    switch (targetType)
    {
        case 'S':
            if (!session.PreparedStatements.TryGetValue(name, out var statement))
                throw new InvalidOperationException($"Prepared statement '{name}' not found.");

            await SendParameterDescriptionAsync(stream, statement.ParameterTypeOids);

            if (!ReturnsRows(statement.Sql))
            {
                await SendNoDataAsync(stream);
                return;
            }

            if (IsSetOrShow(statement.Sql) && statement.Sql.TrimStart().StartsWith("SHOW", StringComparison.OrdinalIgnoreCase))
            {
                await SendRowDescriptionAsync(stream, new[] { ("setting", typeof(string)) });
                statement.MetadataDescribed = true;
                return;
            }

            if (TryResolveVirtualQuery(statement.Sql, connection.Database, DiscoverTableDefinitions(connection), out var statementVirtualResult))
            {
                await SendRowDescriptionAsync(stream, statementVirtualResult.Fields);
                statement.MetadataDescribed = true;
                return;
            }

            var statementFields = InferFieldsFromSelect(statement.Sql);
            if (statementFields.Count == 0)
                statementFields = InferFallbackQueryFields(statement.Sql);

            await SendRowDescriptionAsync(stream, statementFields);
            statement.MetadataDescribed = true;

            return;

        case 'P':
            if (!session.Portals.TryGetValue(name, out var portal))
                throw new InvalidOperationException($"Portal '{name}' not found.");

            var knownTables = DiscoverTableDefinitions(connection);
            var describeSql = RewriteSelectStarWithKnownColumns(portal.Sql, knownTables);

            if (!portal.IsQuery)
            {
                await SendNoDataAsync(stream);
                return;
            }

            if (IsSetOrShow(describeSql) && describeSql.TrimStart().StartsWith("SHOW", StringComparison.OrdinalIgnoreCase))
            {
                await SendRowDescriptionAsync(stream, new[] { ("setting", typeof(string)) });
                portal.MetadataDescribed = true;
                return;
            }

            if (TryResolveVirtualQuery(describeSql, connection.Database, knownTables, out var virtualResult))
            {
                await SendRowDescriptionAsync(stream, virtualResult.Fields);
                portal.MetadataDescribed = true;
                return;
            }

            var fields = InferFieldsFromSelect(describeSql);
            if (fields.Count == 0)
            {
                try
                {
                    fields = TryReadFields(connection, session, describeSql);
                }
                catch
                {
                    fields = Array.Empty<(string Name, Type ClrType)>();
                }
            }

            if (fields.Count == 1 && string.Equals(fields[0].Name, "?column?", StringComparison.OrdinalIgnoreCase))
            {
                var inferredFromTable = InferFieldsFromKnownTables(describeSql, knownTables);
                if (inferredFromTable.Count > 0)
                    fields = inferredFromTable;
                else
                    fields = InferFieldsFromSampleJsonRow(connection, session, describeSql);
            }

            if (fields.Count == 0)
                fields = InferFallbackQueryFields(portal.Sql);

            await SendRowDescriptionAsync(stream, fields);
            portal.MetadataDescribed = true;
            return;

        default:
            throw new InvalidOperationException($"Describe target '{targetType}' is not supported.");
    }
}

static async Task HandleExecuteAsync(NetworkStream stream, WalhallaSqlDbConnection connection, DbSessionState session, byte[] payload)
{
    var reader = new PgPayloadReader(payload);
    var portalName = NormalizeName(reader.ReadCString());
    var maxRows = reader.ReadInt32();
    _ = maxRows;

    if (!session.Portals.TryGetValue(portalName, out var portal))
        throw new InvalidOperationException($"Portal '{portalName}' not found.");

    var knownTables = DiscoverTableDefinitions(connection);
    var trimmedSql = RewriteSelectStarWithKnownColumns(portal.Sql, knownTables).Trim();

    if (IsBegin(trimmedSql))
    {
        if (session.Transaction == null)
            session.Transaction = connection.BeginTransaction();

        await SendCommandCompleteAsync(stream, "BEGIN");
        return;
    }

    if (IsCommit(trimmedSql))
    {
        if (session.Transaction != null)
        {
            session.Transaction.Commit();
            session.Transaction.Dispose();
            session.Transaction = null;
        }

        await SendCommandCompleteAsync(stream, "COMMIT");
        return;
    }

    if (IsRollback(trimmedSql))
    {
        if (session.Transaction != null)
        {
            session.Transaction.Rollback();
            session.Transaction.Dispose();
            session.Transaction = null;
        }

        await SendCommandCompleteAsync(stream, "ROLLBACK");
        return;
    }

    if (IsSetOrShow(trimmedSql))
    {
        if (trimmedSql.StartsWith("SHOW", StringComparison.OrdinalIgnoreCase))
        {
            if (!portal.MetadataDescribed)
                await SendRowDescriptionAsync(stream, new[] { ("setting", typeof(string)) });
            await SendDataRowAsync(stream, new[] { "layeredsql" });
            await SendCommandCompleteAsync(stream, "SHOW 1");
            portal.MetadataDescribed = true;
        }
        else
        {
            await SendCommandCompleteAsync(stream, "SET");
        }

        return;
    }

    if (portal.IsQuery && TryResolveVirtualQuery(trimmedSql, connection.Database, knownTables, out var virtualResult))
    {
        await SendVirtualExecuteResultAsync(stream, virtualResult, !portal.MetadataDescribed);
        portal.MetadataDescribed = true;
        return;
    }

    try
    {
        using var command = connection.CreateCommand();
        command.CommandText = NormalizeSqlForExecution(trimmedSql);
        if (session.Transaction != null)
            command.Transaction = session.Transaction;

        if (portal.IsQuery)
        {
            using var dbReader = command.ExecuteReader();

            if (TryExpandSingleJsonColumnResult(dbReader, out var expandedFields, out var expandedRows))
            {
                if (!portal.MetadataDescribed)
                    await SendRowDescriptionAsync(stream, expandedFields);

                foreach (var expandedRow in expandedRows)
                    await SendDataRowAsync(stream, expandedRow);

                await SendCommandCompleteAsync(stream, $"SELECT {expandedRows.Count}");
                portal.MetadataDescribed = true;
                return;
            }

            var fields = new (string Name, Type ClrType)[dbReader.FieldCount];
            for (var i = 0; i < dbReader.FieldCount; i++)
                fields[i] = (dbReader.GetName(i), dbReader.GetFieldType(i));

            if (!portal.MetadataDescribed)
                await SendRowDescriptionAsync(stream, fields);

            var rowCount = 0;
            while (dbReader.Read())
            {
                var row = new string?[dbReader.FieldCount];
                for (var i = 0; i < dbReader.FieldCount; i++)
                {
                    if (dbReader.IsDBNull(i))
                    {
                        row[i] = null;
                        continue;
                    }

                    row[i] = ToPgText(dbReader.GetValue(i));
                }

                await SendDataRowAsync(stream, row);
                rowCount++;
            }

            await SendCommandCompleteAsync(stream, $"SELECT {rowCount}");
            portal.MetadataDescribed = true;
            return;
        }

        var affected = command.ExecuteNonQuery();
        await SendCommandCompleteAsync(stream, BuildCommandTag(trimmedSql, affected));
    }
    catch (Exception ex) when (portal.IsQuery && IsCannotInferCollectionError(ex))
    {
        if (!portal.MetadataDescribed)
            await SendRowDescriptionAsync(stream, Array.Empty<(string Name, Type ClrType)>());
        await SendCommandCompleteAsync(stream, "SELECT 0");
        portal.MetadataDescribed = true;
    }
    catch (Exception ex)
    {
        throw new InvalidOperationException($"{ex.Message} SQL=[{trimmedSql}]", ex);
    }
}

static string NormalizeSqlForExecution(string sql)
{
    if (string.IsNullOrWhiteSpace(sql))
        return sql;

    var normalized = Regex.Replace(
        sql,
        @"(?<prefix>\b(?:from|join|into|update|table)\s+)(?:""?public""?\s*\.\s*)(?<name>""[^""]+""|[a-zA-Z_][a-zA-Z0-9_]*)",
        "${prefix}${name}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    normalized = Regex.Replace(
        normalized,
        @"(?<prefix>\b(?:from|join|into|update|table)\s+)""(?<name>[^""]+)""",
        "${prefix}${name}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    normalized = Regex.Replace(
        normalized,
        @"\b""?public""?\s*\.\s*""?(?<name>[a-zA-Z_][a-zA-Z0-9_]*)""?",
        "${name}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    normalized = Regex.Replace(
        normalized,
        @"^\s*select\s+(?<alias>[a-zA-Z_][a-zA-Z0-9_]*)\.\*\s+from\s+(?<table>[a-zA-Z_][a-zA-Z0-9_]*)(?:\s+as)?\s+\k<alias>(?<tail>.*)$",
        "SELECT * FROM ${table}${tail}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    return normalized;
}

static bool IsCannotInferCollectionError(Exception exception)
{
    return exception.Message.Contains("Cannot infer collection for SQL command", StringComparison.OrdinalIgnoreCase);
}

static bool TryExpandSingleJsonColumnResult(
    DbDataReader reader,
    out (string Name, Type ClrType)[] fields,
    out List<string?[]> rows)
{
    fields = Array.Empty<(string Name, Type ClrType)>();
    rows = new List<string?[]>();

    if (reader.FieldCount != 1)
        return false;

    var fieldName = reader.GetName(0);
    if (!string.Equals(fieldName, "?column?", StringComparison.OrdinalIgnoreCase))
        return false;

    var columnOrder = new List<string>();
    var objectRows = new List<Dictionary<string, string?>>(16);

    while (reader.Read())
    {
        var rowMap = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        if (!reader.IsDBNull(0))
        {
            var cellValue = reader.GetValue(0);
            var raw = cellValue switch
            {
                string text => text,
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                _ => Convert.ToString(cellValue, CultureInfo.InvariantCulture)
            };

            if (!string.IsNullOrWhiteSpace(raw))
            {
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            if (!columnOrder.Contains(prop.Name, StringComparer.OrdinalIgnoreCase))
                                columnOrder.Add(prop.Name);

                            rowMap[prop.Name] = prop.Value.ValueKind switch
                            {
                                JsonValueKind.Null => null,
                                JsonValueKind.String => prop.Value.GetString(),
                                JsonValueKind.True => "t",
                                JsonValueKind.False => "f",
                                JsonValueKind.Number => prop.Value.GetRawText(),
                                _ => prop.Value.GetRawText()
                            };
                        }
                    }
                    else
                    {
                        rowMap["value"] = raw;
                        if (!columnOrder.Contains("value", StringComparer.OrdinalIgnoreCase))
                            columnOrder.Add("value");
                    }
                }
                catch
                {
                    rowMap["value"] = raw;
                    if (!columnOrder.Contains("value", StringComparer.OrdinalIgnoreCase))
                        columnOrder.Add("value");
                }
            }
        }

        objectRows.Add(rowMap);
    }

    if (columnOrder.Count == 0)
        columnOrder.Add("value");

    fields = columnOrder.Select(name => (name, typeof(string))).ToArray();

    foreach (var objectRow in objectRows)
    {
        var values = new string?[columnOrder.Count];
        for (var i = 0; i < columnOrder.Count; i++)
            values[i] = objectRow.TryGetValue(columnOrder[i], out var value) ? value : null;

        rows.Add(values);
    }

    return true;
}

static IReadOnlyList<(string Name, Type ClrType)> InferFieldsFromKnownTables(
    string sql,
    IReadOnlyList<VirtualTableDefinition> tables)
{
    if (string.IsNullOrWhiteSpace(sql) || tables.Count == 0)
        return Array.Empty<(string Name, Type ClrType)>();

    var normalizedSql = NormalizeSqlForExecution(sql);
    var match = Regex.Match(
        normalizedSql,
        @"\bfrom\s+(?<table>""[^""]+""|[a-zA-Z_][a-zA-Z0-9_]*)(?:\s+as)?(?:\s+[a-zA-Z_][a-zA-Z0-9_]*)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    if (!match.Success)
        return Array.Empty<(string Name, Type ClrType)>();

    var tableToken = match.Groups["table"].Value.Trim().Trim('"');
    if (string.IsNullOrWhiteSpace(tableToken))
        return Array.Empty<(string Name, Type ClrType)>();

    var table = tables.FirstOrDefault(t => string.Equals(t.Name, tableToken, StringComparison.OrdinalIgnoreCase));
    if (table is null)
        return Array.Empty<(string Name, Type ClrType)>();

    if (table.Columns.Count == 0)
        return new[] { ("Id", typeof(int)), ("Name", typeof(string)) };

    return table.Columns
        .Select(c => (c.Name, MapInformationSchemaTypeToClrType(c.DataType)))
        .ToArray();
}

static IReadOnlyList<(string Name, Type ClrType)> InferFieldsFromSampleJsonRow(
    WalhallaSqlDbConnection connection,
    DbSessionState session,
    string sql)
{
    try
    {
        using var command = connection.CreateCommand();
        command.CommandText = NormalizeSqlForExecution(sql);
        if (session.Transaction != null)
            command.Transaction = session.Transaction;

        using var reader = command.ExecuteReader();
        if (reader.FieldCount != 1 || !reader.Read() || reader.IsDBNull(0))
            return Array.Empty<(string Name, Type ClrType)>();

        var raw = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<(string Name, Type ClrType)>();

        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return Array.Empty<(string Name, Type ClrType)>();

        var fields = new List<(string Name, Type ClrType)>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var clrType = prop.Value.ValueKind switch
            {
                JsonValueKind.True or JsonValueKind.False => typeof(bool),
                JsonValueKind.Number => typeof(string),
                JsonValueKind.Null => typeof(string),
                _ => typeof(string)
            };

            fields.Add((prop.Name, clrType));
        }

        return fields;
    }
    catch
    {
        return Array.Empty<(string Name, Type ClrType)>();
    }
}

static Type MapInformationSchemaTypeToClrType(string? dataType)
{
    var normalized = (dataType ?? string.Empty).Trim().ToLowerInvariant();
    return normalized switch
    {
        "smallint" => typeof(short),
        "integer" or "int" => typeof(int),
        "bigint" => typeof(long),
        "double precision" or "real" => typeof(double),
        "boolean" => typeof(bool),
        "bytea" => typeof(byte[]),
        "timestamp with time zone" or "timestamp" => typeof(DateTime),
        _ => typeof(string)
    };
}

static string RewriteSelectStarWithKnownColumns(string sql, IReadOnlyList<VirtualTableDefinition> tables)
{
    if (string.IsNullOrWhiteSpace(sql) || tables.Count == 0)
        return sql;

    var normalized = NormalizeSqlForExecution(sql);
    var match = Regex.Match(
        normalized,
        @"^\s*select\s+(?<proj>\*|[a-zA-Z_][a-zA-Z0-9_]*\.\*)\s+from\s+(?<table>[a-zA-Z_][a-zA-Z0-9_]*)(?<tail>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    if (!match.Success)
        return normalized;

    var tableName = match.Groups["table"].Value;
    var table = tables.FirstOrDefault(t => string.Equals(t.Name, tableName, StringComparison.OrdinalIgnoreCase));
    if (table is null)
        return normalized;

    var columns = table.Columns.Count == 0
        ? new[] { "Id", "Name" }
        : table.Columns.Select(c => c.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();

    if (columns.Length == 0)
        columns = new[] { "Id", "Name" };

    var projection = string.Join(", ", columns.Select(static c => $"\"{c.Replace("\"", "\"\"", StringComparison.Ordinal)}\""));
    var tail = match.Groups["tail"].Value;

    return $"SELECT {projection} FROM {tableName}{tail}";
}

static void HandleClose(DbSessionState session, byte[] payload)
{
    var reader = new PgPayloadReader(payload);
    var targetType = (char)reader.ReadByte();
    var name = NormalizeName(reader.ReadCString());

    if (targetType == 'S')
    {
        session.PreparedStatements.Remove(name);
        if (name == string.Empty)
            session.Portals.Remove(string.Empty);
        return;
    }

    if (targetType == 'P')
    {
        session.Portals.Remove(name);
        return;
    }

    throw new InvalidOperationException($"Close target '{targetType}' is not supported.");
}

static IReadOnlyList<(string Name, Type ClrType)> TryReadFields(WalhallaSqlDbConnection connection, DbSessionState session, string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    if (session.Transaction != null)
        command.Transaction = session.Transaction;

    using var reader = command.ExecuteReader();
    var fields = new (string Name, Type ClrType)[reader.FieldCount];
    for (var i = 0; i < reader.FieldCount; i++)
        fields[i] = (reader.GetName(i), reader.GetFieldType(i));

    return fields;
}

static short[] ReadFormatCodes(PgPayloadReader reader)
{
    var count = reader.ReadInt16();
    var codes = new short[count];
    for (var i = 0; i < count; i++)
        codes[i] = reader.ReadInt16();

    return codes;
}

static short ResolveFormatCode(short[] codes, int index)
{
    if (codes.Length == 0)
        return 0;

    if (codes.Length == 1)
        return codes[0];

    if (index < 0 || index >= codes.Length)
        return 0;

    return codes[index];
}

static string RenderSqlWithParameters(string sql, IReadOnlyList<string?> parameterLiterals)
{
    var rendered = Regex.Replace(
        sql,
        @"\$(\d+)",
        match =>
        {
            if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ordinal))
                return match.Value;

            var index = ordinal - 1;
            if (index < 0 || index >= parameterLiterals.Count)
                throw new InvalidOperationException($"Missing bind value for parameter ${ordinal}.");

            return parameterLiterals[index] ?? "NULL";
        },
        RegexOptions.CultureInvariant);

    if (rendered.IndexOf('?', StringComparison.Ordinal) < 0)
        return rendered;

    var sb = new StringBuilder(rendered.Length + (parameterLiterals.Count * 8));
    var inString = false;
    var nextIndex = 0;

    for (var i = 0; i < rendered.Length; i++)
    {
        var ch = rendered[i];

        if (ch == '\'')
        {
            if (inString && i + 1 < rendered.Length && rendered[i + 1] == '\'')
            {
                sb.Append("''");
                i++;
                continue;
            }

            inString = !inString;
            sb.Append(ch);
            continue;
        }

        if (!inString && ch == '?')
        {
            if (nextIndex >= parameterLiterals.Count)
                throw new InvalidOperationException($"Missing bind value for parameter ? at position {nextIndex + 1}.");

            sb.Append(parameterLiterals[nextIndex] ?? "NULL");
            nextIndex++;
            continue;
        }

        sb.Append(ch);
    }

    return sb.ToString();
}

static string ToSqlLiteral(string input)
{
    if (string.Equals(input, "null", StringComparison.OrdinalIgnoreCase))
        return "NULL";

    if (string.Equals(input, "t", StringComparison.OrdinalIgnoreCase) || string.Equals(input, "true", StringComparison.OrdinalIgnoreCase))
        return "TRUE";

    if (string.Equals(input, "f", StringComparison.OrdinalIgnoreCase) || string.Equals(input, "false", StringComparison.OrdinalIgnoreCase))
        return "FALSE";

    if (decimal.TryParse(input, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
        return input;

    return $"'{input.Replace("'", "''", StringComparison.Ordinal)}'";
}

static string DecodeBindParameterLiteral(byte[] raw, short formatCode, int parameterTypeOid)
{
    if (formatCode == 0)
    {
        var textValue = Encoding.UTF8.GetString(raw);
        return ToSqlLiteral(textValue);
    }

    if (TryDecodeBinaryParameter(raw, parameterTypeOid, out var literal))
        return literal;

    if (TryDecodeUtf8Text(raw, out var utf8Text))
        return ToSqlLiteral(utf8Text);

    return ToSqlLiteral("\\x" + Convert.ToHexString(raw).ToLowerInvariant());
}

static bool TryDecodeBinaryParameter(byte[] raw, int parameterTypeOid, out string literal)
{
    switch (parameterTypeOid)
    {
        case 16 when raw.Length == 1:
            literal = raw[0] == 0 ? "FALSE" : "TRUE";
            return true;

        case 21 when raw.Length == 2:
            literal = BinaryPrimitives.ReadInt16BigEndian(raw).ToString(CultureInfo.InvariantCulture);
            return true;

        case 23 or 26 when raw.Length == 4:
            literal = BinaryPrimitives.ReadInt32BigEndian(raw).ToString(CultureInfo.InvariantCulture);
            return true;

        case 20 when raw.Length == 8:
            literal = BinaryPrimitives.ReadInt64BigEndian(raw).ToString(CultureInfo.InvariantCulture);
            return true;

        case 700 when raw.Length == 4:
            {
                var intBits = BinaryPrimitives.ReadInt32BigEndian(raw);
                var value = BitConverter.Int32BitsToSingle(intBits);
                literal = value.ToString("R", CultureInfo.InvariantCulture);
                return true;
            }

        case 701 when raw.Length == 8:
            {
                var longBits = BinaryPrimitives.ReadInt64BigEndian(raw);
                var value = BitConverter.Int64BitsToDouble(longBits);
                literal = value.ToString("R", CultureInfo.InvariantCulture);
                return true;
            }

        case 18 or 19 or 25 or 1042 or 1043:
            if (TryDecodeUtf8Text(raw, out var text))
            {
                literal = ToSqlLiteral(text);
                return true;
            }

            break;
    }

    if (parameterTypeOid == 0)
    {
        if (raw.Length == 1 && (raw[0] == 0 || raw[0] == 1))
        {
            literal = raw[0] == 0 ? "FALSE" : "TRUE";
            return true;
        }

        if (raw.Length == 2)
        {
            literal = BinaryPrimitives.ReadInt16BigEndian(raw).ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (raw.Length == 4)
        {
            literal = BinaryPrimitives.ReadInt32BigEndian(raw).ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (raw.Length == 8)
        {
            literal = BinaryPrimitives.ReadInt64BigEndian(raw).ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (TryDecodeUtf8Text(raw, out var text))
        {
            literal = ToSqlLiteral(text);
            return true;
        }
    }

    literal = string.Empty;
    return false;
}

static bool TryDecodeUtf8Text(byte[] raw, out string text)
{
    try
    {
        var decoded = Encoding.UTF8.GetString(raw);
        for (var i = 0; i < decoded.Length; i++)
        {
            var ch = decoded[i];
            if (char.IsControl(ch) && ch is not '\r' and not '\n' and not '\t')
            {
                text = string.Empty;
                return false;
            }
        }

        text = decoded;
        return true;
    }
    catch
    {
        text = string.Empty;
        return false;
    }
}

static string NormalizeName(string name)
{
    return name ?? string.Empty;
}

static string BuildCommandTag(string sql, int affected)
{
    var keyword = FirstKeyword(sql);
    return keyword switch
    {
        "INSERT" => $"INSERT 0 {affected}",
        "UPDATE" => $"UPDATE {affected}",
        "DELETE" => $"DELETE {affected}",
        "CREATE" => "CREATE",
        "DROP" => "DROP",
        "ALTER" => "ALTER",
        _ => $"{keyword} {affected}"
    };
}

static string FirstKeyword(string sql)
{
    if (string.IsNullOrWhiteSpace(sql))
        return "OK";

    var match = Regex.Match(
        sql,
        @"^\s*(?:(?:--[^\r\n]*(?:[\r\n]+|$))|(?:/\*.*?\*/\s*)|(?:\(\s*))*([A-Za-z]+)",
        RegexOptions.CultureInvariant | RegexOptions.Singleline);

    if (match.Success)
        return match.Groups[1].Value.ToUpperInvariant();

    var parts = sql.TrimStart().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
    return parts.Length == 0 ? "OK" : parts[0].ToUpperInvariant();
}

static bool LooksLikeSelect(string sql)
{
    var keyword = FirstKeyword(sql);
    return keyword is "SELECT" or "WITH";
}

static bool ReturnsRows(string sql)
{
    var keyword = FirstKeyword(sql);
    return keyword is "SELECT" or "WITH" or "SHOW" or "VALUES" or "TABLE";
}

static bool IsBegin(string sql)
{
    var keyword = FirstKeyword(sql);
    return keyword == "BEGIN" || (keyword == "START" && sql.Contains("TRANSACTION", StringComparison.OrdinalIgnoreCase));
}

static bool IsCommit(string sql) => FirstKeyword(sql) == "COMMIT";

static bool IsRollback(string sql) => FirstKeyword(sql) == "ROLLBACK";

static bool IsSetOrShow(string sql)
{
    var keyword = FirstKeyword(sql);
    return keyword is "SET" or "SHOW";
}

static string DecodeCStringPayload(byte[] payload)
{
    var len = payload.Length;
    if (len > 0 && payload[len - 1] == 0)
        len--;

    return Encoding.UTF8.GetString(payload, 0, len);
}

static IEnumerable<string> SplitSqlStatements(string sql)
{
    if (string.IsNullOrWhiteSpace(sql))
        yield break;

    var inString = false;
    var start = 0;

    for (var i = 0; i < sql.Length; i++)
    {
        var c = sql[i];
        if (c == '\'' && (i == 0 || sql[i - 1] != '\\'))
            inString = !inString;

        if (inString || c != ';')
            continue;

        var statement = sql[start..i].Trim();
        if (statement.Length > 0)
            yield return statement;

        start = i + 1;
    }

    if (start < sql.Length)
    {
        var last = sql[start..].Trim();
        if (last.Length > 0)
            yield return last;
    }
}

static string ToPgText(object value)
{
    return value switch
    {
        null => string.Empty,
        bool b => b ? "t" : "f",
        DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
        byte[] bytes => "\\x" + Convert.ToHexString(bytes).ToLowerInvariant(),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };
}

static bool TryResolveVirtualQuery(string sql, string? databaseName, IReadOnlyList<VirtualTableDefinition>? tables, out VirtualQueryResult result)
{
    var normalized = NormalizeSqlForCatalogDetection(sql);
    if (string.IsNullOrWhiteSpace(normalized))
    {
        result = new VirtualQueryResult(
            Array.Empty<(string Name, Type ClrType)>(),
            Array.Empty<Dictionary<string, object?>>());
        return false;
    }

    if (TryResolveScalarStartupQuery(sql, normalized, databaseName, out result))
    {
        PgWireTrace.Virtual("scalar", normalized, result.Rows.Count);
        return true;
    }

    if (normalized.StartsWith("select version()", StringComparison.Ordinal))
    {
        result = new VirtualQueryResult(
            new[] { ("version", typeof(string)) },
            new[] { new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["version"] = "LayeredSql PgWire 16.0-layeredsql" } });
        PgWireTrace.Virtual("version", normalized, result.Rows.Count);
        return true;
    }

    if (normalized.Contains("current_database()", StringComparison.Ordinal))
    {
        result = new VirtualQueryResult(
            new[] { ("current_database", typeof(string)) },
            new[]
            {
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["current_database"] = string.IsNullOrWhiteSpace(databaseName) ? "App" : databaseName
                }
            });
        PgWireTrace.Virtual("current_database", normalized, result.Rows.Count);
        return true;
    }

    if (normalized.Contains("current_schema()", StringComparison.Ordinal))
    {
        result = new VirtualQueryResult(
            new[] { ("current_schema", typeof(string)) },
            new[] { new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["current_schema"] = "public" } });
        PgWireTrace.Virtual("current_schema", normalized, result.Rows.Count);
        return true;
    }

    if (normalized.Contains("information_schema.schemata", StringComparison.Ordinal))
    {
        var dbName = string.IsNullOrWhiteSpace(databaseName) ? "App" : databaseName;
        var rows = new[]
        {
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["catalog_name"] = dbName,
                ["schema_name"] = "pg_catalog",
                ["schema_owner"] = "postgres"
            },
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["catalog_name"] = dbName,
                ["schema_name"] = "public",
                ["schema_owner"] = "postgres"
            },
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["catalog_name"] = dbName,
                ["schema_name"] = "information_schema",
                ["schema_owner"] = "postgres"
            }
        };

        result = BuildProjectedVirtualResult(sql, rows, new[]
        {
            ("catalog_name", typeof(string)),
            ("schema_name", typeof(string)),
            ("schema_owner", typeof(string))
        });
        PgWireTrace.Virtual("information_schema.schemata", normalized, result.Rows.Count);
        return true;
    }

    if (normalized.Contains("from pg_catalog.pg_namespace", StringComparison.Ordinal))
    {
        var rows = new[]
        {
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["oid"] = 11,
                ["nspname"] = "pg_catalog",
                ["nspowner"] = 10,
                ["nspacl"] = null,
                ["description"] = null
            },
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["oid"] = 2200,
                ["nspname"] = "public",
                ["nspowner"] = 10,
                ["nspacl"] = null,
                ["description"] = null
            },
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["oid"] = 13207,
                ["nspname"] = "information_schema",
                ["nspowner"] = 10,
                ["nspacl"] = null,
                ["description"] = null
            }
        };

        result = BuildProjectedVirtualResult(sql, rows, new[]
        {
            ("oid", typeof(int)),
            ("nspname", typeof(string)),
            ("nspowner", typeof(int)),
            ("nspacl", typeof(string)),
            ("description", typeof(string))
        });
        PgWireTrace.Virtual("pg_namespace", normalized, result.Rows.Count);
        return true;
    }

    if (normalized.Contains("from pg_catalog.pg_database", StringComparison.Ordinal))
    {
        var rows = new[]
        {
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["oid"] = 16384,
                ["datname"] = string.IsNullOrWhiteSpace(databaseName) ? "App" : databaseName,
                ["datdba"] = 10,
                ["encoding"] = 6,
                ["datcollate"] = "C",
                ["datctype"] = "C",
                ["datistemplate"] = false,
                ["datallowconn"] = true
            }
        };

        result = BuildProjectedVirtualResult(sql, rows, new[]
        {
            ("oid", typeof(int)),
            ("datname", typeof(string)),
            ("datdba", typeof(int)),
            ("encoding", typeof(int)),
            ("datcollate", typeof(string)),
            ("datctype", typeof(string)),
            ("datistemplate", typeof(bool)),
            ("datallowconn", typeof(bool))
        });
        return true;
    }

    if (Regex.IsMatch(normalized, @"\bpg_catalog\.pg_tablespace\b", RegexOptions.CultureInvariant))
    {
        var rows = new[]
        {
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["oid"] = 1663,
                ["spcname"] = "pg_default",
                ["spcowner"] = 10,
                ["spcacl"] = null,
                ["spcoptions"] = null,
                ["loc"] = string.Empty
            }
        };

        result = BuildProjectedVirtualResult(sql, rows, new[]
        {
            ("oid", typeof(int)),
            ("spcname", typeof(string)),
            ("spcowner", typeof(int)),
            ("spcacl", typeof(string)),
            ("spcoptions", typeof(string)),
            ("loc", typeof(string))
        });
        PgWireTrace.Virtual("pg_tablespace", normalized, result.Rows.Count);
        return true;
    }

    if (normalized.Contains("information_schema.tables", StringComparison.Ordinal)
        || Regex.IsMatch(normalized, @"\bpg_catalog\.pg_tables\b", RegexOptions.CultureInvariant))
    {
        var dbName = string.IsNullOrWhiteSpace(databaseName) ? "App" : databaseName;
        var resolvedTables = tables ?? Array.Empty<VirtualTableDefinition>();
        var rows = resolvedTables
            .Select(t => t.Name)
            .Where(static n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static n => n, StringComparer.OrdinalIgnoreCase)
            .Select(tableName => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["table_catalog"] = dbName,
                ["table_schema"] = "public",
                ["table_name"] = tableName,
                ["table_type"] = "BASE TABLE",
                ["schemaname"] = "public",
                ["tablename"] = tableName,
                ["tableowner"] = "postgres",
                ["hasindexes"] = true,
                ["hasrules"] = false,
                ["hastriggers"] = false,
                ["rowsecurity"] = false
            })
            .ToArray();

        result = BuildProjectedVirtualResult(sql, rows, new[]
        {
            ("table_catalog", typeof(string)),
            ("table_schema", typeof(string)),
            ("table_name", typeof(string)),
            ("table_type", typeof(string)),
            ("schemaname", typeof(string)),
            ("tablename", typeof(string)),
            ("tableowner", typeof(string)),
            ("hasindexes", typeof(bool)),
            ("hasrules", typeof(bool)),
            ("hastriggers", typeof(bool)),
            ("rowsecurity", typeof(bool))
        });
        PgWireTrace.Virtual("tables", normalized, result.Rows.Count);
        return true;
    }

    if (normalized.Contains("information_schema.columns", StringComparison.Ordinal))
    {
        var dbName = string.IsNullOrWhiteSpace(databaseName) ? "App" : databaseName;
        var resolvedTables = tables ?? Array.Empty<VirtualTableDefinition>();

        var rows = new List<Dictionary<string, object?>>();
        foreach (var table in resolvedTables.OrderBy(static t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            var columns = table.Columns.Count == 0
                ? new[] { new VirtualColumnDefinition("Id", "integer"), new VirtualColumnDefinition("Name", "text") }
                : table.Columns;

            for (var i = 0; i < columns.Count; i++)
            {
                var col = columns[i];
                rows.Add(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["table_catalog"] = dbName,
                    ["table_schema"] = "public",
                    ["table_name"] = table.Name,
                    ["column_name"] = col.Name,
                    ["ordinal_position"] = i + 1,
                    ["is_nullable"] = "YES",
                    ["data_type"] = col.DataType
                });
            }
        }

        result = BuildProjectedVirtualResult(sql, rows, new[]
        {
            ("table_catalog", typeof(string)),
            ("table_schema", typeof(string)),
            ("table_name", typeof(string)),
            ("column_name", typeof(string)),
            ("ordinal_position", typeof(int)),
            ("is_nullable", typeof(string)),
            ("data_type", typeof(string))
        });
        PgWireTrace.Virtual("columns", normalized, result.Rows.Count);
        return true;
    }

    if (normalized.StartsWith("select ", StringComparison.Ordinal)
        && normalized.Contains("information_schema.", StringComparison.Ordinal))
    {
        var dbName = string.IsNullOrWhiteSpace(databaseName) ? "App" : databaseName;
        var resolvedTables = tables ?? Array.Empty<VirtualTableDefinition>();

        var rows = resolvedTables
            .OrderBy(static t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(table => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["table_catalog"] = dbName,
                ["table_schema"] = "public",
                ["table_name"] = table.Name,
                ["column_name"] = table.Columns.Count > 0 ? table.Columns[0].Name : "Id",
                ["data_type"] = table.Columns.Count > 0 ? table.Columns[0].DataType : "integer"
            })
            .ToArray();

        result = BuildProjectedVirtualResult(sql, rows, new[]
        {
            ("table_catalog", typeof(string)),
            ("table_schema", typeof(string)),
            ("table_name", typeof(string)),
            ("column_name", typeof(string)),
            ("data_type", typeof(string))
        });
        PgWireTrace.Virtual("information_schema.generic", normalized, result.Rows.Count);
        return true;
    }

    if (normalized.Contains("from pg_catalog.pg_class", StringComparison.Ordinal)
        || normalized.Contains("from pg_class", StringComparison.Ordinal))
    {
        var resolvedTables = tables ?? Array.Empty<VirtualTableDefinition>();

        var rows = resolvedTables
            .OrderBy(static t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(table => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["oid"] = StableOid(table.Name),
                ["relname"] = table.Name,
                ["relnamespace"] = 2200,
                ["relowner"] = 10,
                ["relkind"] = "r",
                ["relam"] = 0,
                ["relhasindex"] = true,
                ["relnatts"] = table.Columns.Count == 0 ? 2 : table.Columns.Count,
                ["relpersistence"] = "p"
            })
            .ToArray();

        result = BuildProjectedVirtualResult(sql, rows, new[]
        {
            ("oid", typeof(int)),
            ("relname", typeof(string)),
            ("relnamespace", typeof(int)),
            ("relowner", typeof(int)),
            ("relkind", typeof(string)),
            ("relam", typeof(int)),
            ("relhasindex", typeof(bool)),
            ("relnatts", typeof(int)),
            ("relpersistence", typeof(string))
        });
        PgWireTrace.Virtual("pg_class", normalized, result.Rows.Count);
        return true;
    }

    if (normalized.Contains("from pg_catalog.pg_inherits", StringComparison.Ordinal)
        || normalized.Contains("from pg_inherits", StringComparison.Ordinal))
    {
        var rows = Array.Empty<Dictionary<string, object?>>();
        result = BuildProjectedVirtualResult(sql, rows, new[]
        {
            ("inhrelid", typeof(int)),
            ("inhparent", typeof(int)),
            ("inhseqno", typeof(int)),
            ("relnamespace", typeof(int))
        });
        PgWireTrace.Virtual("pg_inherits", normalized, result.Rows.Count);
        return true;
    }

    if (normalized.Contains("from pg_catalog.pg_constraint", StringComparison.Ordinal)
        || normalized.Contains("from pg_constraint", StringComparison.Ordinal))
    {
        var rows = Array.Empty<Dictionary<string, object?>>();
        result = BuildProjectedVirtualResult(sql, rows, new[]
        {
            ("oid", typeof(int)),
            ("conname", typeof(string)),
            ("connamespace", typeof(int)),
            ("contype", typeof(string)),
            ("condeferrable", typeof(bool)),
            ("condeferred", typeof(bool)),
            ("convalidated", typeof(bool)),
            ("conrelid", typeof(int)),
            ("contypid", typeof(int)),
            ("conindid", typeof(int)),
            ("conparentid", typeof(int)),
            ("confrelid", typeof(int)),
            ("confupdtype", typeof(string)),
            ("confdeltype", typeof(string)),
            ("confmatchtype", typeof(string)),
            ("conislocal", typeof(bool)),
            ("coninhcount", typeof(int)),
            ("connoinherit", typeof(bool)),
            ("conkey", typeof(string)),
            ("confkey", typeof(string)),
            ("conpfeqop", typeof(string)),
            ("conppeqop", typeof(string)),
            ("conffeqop", typeof(string)),
            ("confdelsetcols", typeof(string)),
            ("conexclop", typeof(string)),
            ("conbin", typeof(string)),
            ("tabrelname", typeof(string)),
            ("refnamespace", typeof(int)),
            ("description", typeof(string)),
            ("src", typeof(string))
        });
        PgWireTrace.Virtual("pg_constraint", normalized, result.Rows.Count);
        return true;
    }

    if (normalized.Contains("from pg_catalog.pg_type", StringComparison.Ordinal)
        || normalized.Contains("from pg_type", StringComparison.Ordinal))
    {
        var rows = new[]
        {
            BuildPgTypeRow(16, "bool", 1, true, "B", "b", 0),
            BuildPgTypeRow(20, "int8", 8, true, "N", "b", 0),
            BuildPgTypeRow(21, "int2", 2, true, "N", "b", 0),
            BuildPgTypeRow(23, "int4", 4, true, "N", "b", 0),
            BuildPgTypeRow(25, "text", -1, false, "S", "b", 0),
            BuildPgTypeRow(700, "float4", 4, true, "N", "b", 0),
            BuildPgTypeRow(701, "float8", 8, true, "N", "b", 0),
            BuildPgTypeRow(1043, "varchar", -1, false, "S", "b", 0),
            BuildPgTypeRow(1184, "timestamptz", 8, true, "D", "b", 0)
        };

        result = BuildProjectedVirtualResult(sql, rows, new[]
        {
            ("oid", typeof(int)),
            ("typname", typeof(string)),
            ("typnamespace", typeof(int)),
            ("typowner", typeof(int)),
            ("typlen", typeof(short)),
            ("typbyval", typeof(bool)),
            ("typtype", typeof(string)),
            ("typcategory", typeof(string)),
            ("typispreferred", typeof(bool)),
            ("typisdefined", typeof(bool)),
            ("typdelim", typeof(string)),
            ("typrelid", typeof(int)),
            ("typelem", typeof(int)),
            ("typarray", typeof(int)),
            ("typinput", typeof(string)),
            ("typoutput", typeof(string)),
            ("typreceive", typeof(string)),
            ("typsend", typeof(string)),
            ("typmodin", typeof(string)),
            ("typmodout", typeof(string)),
            ("typanalyze", typeof(string)),
            ("typalign", typeof(string)),
            ("typstorage", typeof(string)),
            ("typnotnull", typeof(bool)),
            ("typbasetype", typeof(int)),
            ("typtypmod", typeof(int)),
            ("typndims", typeof(int)),
            ("typcollation", typeof(int)),
            ("typdefaultbin", typeof(string)),
            ("typdefault", typeof(string)),
            ("typacl", typeof(string)),
            ("base_type_name", typeof(string)),
            ("description", typeof(string)),
            ("relkind", typeof(string))
        });
        PgWireTrace.Virtual("pg_type", normalized, result.Rows.Count);
        return true;
    }

    if (normalized.Contains("from pg_catalog.pg_attribute", StringComparison.Ordinal)
        || normalized.Contains("from pg_attribute", StringComparison.Ordinal))
    {
        var resolvedTables = tables ?? Array.Empty<VirtualTableDefinition>();
        var rows = new List<Dictionary<string, object?>>();

        foreach (var table in resolvedTables.OrderBy(static t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            var tableOid = StableOid(table.Name);
            var tableColumns = table.Columns.Count == 0
                ? new[] { new VirtualColumnDefinition("Id", "integer"), new VirtualColumnDefinition("Name", "text") }
                : table.Columns;

            for (var i = 0; i < tableColumns.Count; i++)
            {
                var column = tableColumns[i];
                var typeOid = MapInformationSchemaTypeToPgTypeOid(column.DataType);

                rows.Add(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["relname"] = table.Name,
                    ["tabrelname"] = table.Name,
                    ["attrelid"] = tableOid,
                    ["attname"] = column.Name,
                    ["atttypid"] = typeOid,
                    ["attstattarget"] = -1,
                    ["attlen"] = MapPgTypeSizeFromOid(typeOid),
                    ["attnum"] = i + 1,
                    ["attndims"] = 0,
                    ["attcacheoff"] = -1,
                    ["atttypmod"] = -1,
                    ["attbyval"] = false,
                    ["attalign"] = "i",
                    ["attstorage"] = "p",
                    ["attcompression"] = string.Empty,
                    ["attnotnull"] = false,
                    ["atthasdef"] = false,
                    ["atthasmissing"] = false,
                    ["attidentity"] = string.Empty,
                    ["attgenerated"] = string.Empty,
                    ["attisdropped"] = false,
                    ["attislocal"] = true,
                    ["attinhcount"] = 0,
                    ["attcollation"] = 0,
                    ["attacl"] = null,
                    ["attoptions"] = null,
                    ["attfdwoptions"] = null,
                    ["attmissingval"] = null,
                    ["def_value"] = null,
                    ["description"] = null,
                    ["objid"] = tableOid
                });
            }
        }

        result = BuildProjectedVirtualResult(sql, rows, new[]
        {
            ("relname", typeof(string)),
            ("tabrelname", typeof(string)),
            ("attrelid", typeof(int)),
            ("attname", typeof(string)),
            ("atttypid", typeof(int)),
            ("attstattarget", typeof(int)),
            ("attlen", typeof(short)),
            ("attnum", typeof(int)),
            ("attndims", typeof(int)),
            ("attcacheoff", typeof(int)),
            ("atttypmod", typeof(int)),
            ("attbyval", typeof(bool)),
            ("attalign", typeof(string)),
            ("attstorage", typeof(string)),
            ("attcompression", typeof(string)),
            ("attnotnull", typeof(bool)),
            ("atthasdef", typeof(bool)),
            ("atthasmissing", typeof(bool)),
            ("attidentity", typeof(string)),
            ("attgenerated", typeof(string)),
            ("attisdropped", typeof(bool)),
            ("attislocal", typeof(bool)),
            ("attinhcount", typeof(int)),
            ("attcollation", typeof(int)),
            ("attacl", typeof(string)),
            ("attoptions", typeof(string)),
            ("attfdwoptions", typeof(string)),
            ("attmissingval", typeof(string)),
            ("def_value", typeof(string)),
            ("description", typeof(string)),
            ("objid", typeof(int))
        });
        PgWireTrace.Virtual("pg_attribute", normalized, result.Rows.Count);
        return true;
    }

    if (normalized.StartsWith("select ", StringComparison.Ordinal)
        && (normalized.Contains("pg_catalog.", StringComparison.Ordinal)
            || normalized.Contains("information_schema.", StringComparison.Ordinal)
            || normalized.Contains("from pg_type", StringComparison.Ordinal)
            || normalized.Contains("from pg_class", StringComparison.Ordinal)
            || normalized.Contains("from pg_attribute", StringComparison.Ordinal)
            || normalized.Contains("from pg_index", StringComparison.Ordinal)))
    {
        var rows = Array.Empty<Dictionary<string, object?>>();
        result = BuildProjectedVirtualResult(sql, rows, new[]
        {
            ("oid", typeof(int)),
            ("nspname", typeof(string)),
            ("relname", typeof(string)),
            ("typname", typeof(string)),
            ("attname", typeof(string)),
            ("table_schema", typeof(string)),
            ("table_name", typeof(string)),
            ("column_name", typeof(string)),
            ("data_type", typeof(string))
        });
        PgWireTrace.Virtual("catalog.generic", normalized, result.Rows.Count);
        return true;
    }

    result = new VirtualQueryResult(
        Array.Empty<(string Name, Type ClrType)>(),
        Array.Empty<Dictionary<string, object?>>());
    return false;
}

static IReadOnlyList<VirtualTableDefinition> DiscoverTableDefinitions(WalhallaSqlDbConnection connection)
{
    try
    {
        var handleProperty = connection.GetType().GetProperty("DatabaseHandle", BindingFlags.Instance | BindingFlags.NonPublic);
        var databaseHandle = handleProperty?.GetValue(connection);
        if (databaseHandle is null)
            return Array.Empty<VirtualTableDefinition>();

        var tables = new Dictionary<string, VirtualTableDefinition>(StringComparer.OrdinalIgnoreCase);
        var catalogTables = DiscoverTableDefinitionsFromCatalog(databaseHandle);
        foreach (var table in catalogTables)
            tables[table.Name] = table;

        var runtimeNames = DiscoverCollectionNamesFromRuntimeRows(databaseHandle);
        foreach (var name in runtimeNames)
        {
            if (!tables.ContainsKey(name))
                tables[name] = new VirtualTableDefinition(name, Array.Empty<VirtualColumnDefinition>());
        }

        var masterNames = DiscoverCollectionNamesFromMasterCollection(databaseHandle);
        foreach (var name in masterNames)
        {
            if (!tables.ContainsKey(name))
                tables[name] = new VirtualTableDefinition(name, Array.Empty<VirtualColumnDefinition>());
        }

        if (PgWireTrace.Enabled)
            Console.WriteLine($"[PGWIRE][DISCOVER] catalog={catalogTables.Count} runtime={runtimeNames.Count} master={masterNames.Count} total={tables.Count}");

        return tables.Values.OrderBy(static t => t.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    catch
    {
        return Array.Empty<VirtualTableDefinition>();
    }
}

static IReadOnlyList<VirtualTableDefinition> DiscoverTableDefinitionsFromCatalog(object databaseHandle)
{
    try
    {
        var collectionExistsMethod = databaseHandle.GetType().GetMethod("CollectionExists", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
        var getCollectionMethod = databaseHandle.GetType().GetMethod("GetCollection", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
        if (collectionExistsMethod is null || getCollectionMethod is null)
            return Array.Empty<VirtualTableDefinition>();

        var exists = collectionExistsMethod.Invoke(databaseHandle, new object[] { "__sql_catalog" });
        if (exists is not bool b || !b)
            return Array.Empty<VirtualTableDefinition>();

        var catalogCollection = getCollectionMethod.Invoke(databaseHandle, new object[] { "__sql_catalog" });
        if (catalogCollection is not System.Collections.IEnumerable rows)
            return Array.Empty<VirtualTableDefinition>();

        var tables = new Dictionary<string, VirtualTableDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            try
            {
                if (row is null)
                    continue;

                var ident = row.GetType().GetProperty("Ident", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(row);
                if (ident is null)
                    continue;

                var identType = ident.GetType();
                var attribute = Convert.ToInt32(identType.GetProperty("Attribute", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(ident) ?? -1, CultureInfo.InvariantCulture);
                if (attribute != 0)
                    continue;

                var keyIdent = identType.GetProperty("KeyIdent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(ident);
                if (keyIdent is null)
                    continue;

                var keyIdentType = keyIdent.GetType();
                var keyTypeValue = keyIdentType.GetProperty("Type", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(keyIdent);
                var keyTypeName = keyTypeValue?.ToString();
                if (!string.Equals(keyTypeName, "String", StringComparison.OrdinalIgnoreCase))
                    continue;

                var key = keyIdentType.GetMethod("AsString", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(keyIdent, null) as string;
                if (string.IsNullOrWhiteSpace(key) || !key.StartsWith("table:", StringComparison.OrdinalIgnoreCase))
                    continue;

                var data = row.GetType().GetProperty("Data", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(row) as byte[];
                if (data is null || data.Length == 0)
                    continue;

                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    continue;

                if (root.TryGetProperty("CollectionName", out var collectionNameElement)
                    && collectionNameElement.ValueKind == JsonValueKind.String)
                {
                    var collectionName = collectionNameElement.GetString();
                    if (!string.IsNullOrWhiteSpace(collectionName) && !collectionName.StartsWith("__", StringComparison.Ordinal))
                    {
                        var columns = new List<VirtualColumnDefinition>();
                        if (root.TryGetProperty("Columns", out var columnsElement) && columnsElement.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var col in columnsElement.EnumerateArray())
                            {
                                if (!col.TryGetProperty("Name", out var colNameElement) || colNameElement.ValueKind != JsonValueKind.String)
                                    continue;

                                var columnName = colNameElement.GetString();
                                if (string.IsNullOrWhiteSpace(columnName))
                                    continue;

                                var dataType = "text";
                                if (col.TryGetProperty("Type", out var colTypeElement))
                                    dataType = MapCatalogColumnTypeToInformationSchema(colTypeElement);

                                if (string.Equals(dataType, "text", StringComparison.OrdinalIgnoreCase)
                                    && string.Equals(columnName, "Id", StringComparison.OrdinalIgnoreCase))
                                {
                                    dataType = "integer";
                                }

                                columns.Add(new VirtualColumnDefinition(columnName, dataType));
                            }
                        }

                        tables[collectionName] = new VirtualTableDefinition(collectionName, columns);
                    }
                }
            }
            catch
            {
                continue;
            }
        }

        return tables.Values.OrderBy(static t => t.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    catch
    {
        return Array.Empty<VirtualTableDefinition>();
    }
}

static string MapSqlScalarTypeToInformationSchema(int scalarType)
{
    return scalarType switch
    {
        1 => "integer",
        2 => "bigint",
        3 => "double precision",
        4 => "numeric",
        5 => "text",
        6 => "boolean",
        7 => "timestamp with time zone",
        8 => "bytea",
        9 => "json",
        10 => "uuid",
        _ => "text"
    };
}

static string MapCatalogColumnTypeToInformationSchema(JsonElement typeElement)
{
    if (typeElement.ValueKind == JsonValueKind.Number && typeElement.TryGetInt32(out var typeOrdinal))
        return MapSqlScalarTypeToInformationSchema(typeOrdinal);

    if (typeElement.ValueKind == JsonValueKind.String)
    {
        var raw = typeElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return "text";

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ordinal))
            return MapSqlScalarTypeToInformationSchema(ordinal);

        return raw.ToUpperInvariant() switch
        {
            "INT32" or "INTEGER" or "INT" => "integer",
            "INT64" or "BIGINT" or "LONG" => "bigint",
            "DOUBLE" or "REAL" or "FLOAT" => "double precision",
            "DECIMAL" or "NUMERIC" => "numeric",
            "BOOLEAN" or "BOOL" or "BIT" => "boolean",
            "DATETIME" or "TIMESTAMP" or "DATE" => "timestamp with time zone",
            "BINARY" or "VARBINARY" or "BYTEA" or "BLOB" => "bytea",
            "JSON" or "JSONB" => "json",
            "GUID" or "UUID" => "uuid",
            "STRING" or "TEXT" or "VARCHAR" or "CHAR" => "text",
            _ => "text"
        };
    }

    return "text";
}

static Dictionary<string, object?> BuildPgTypeRow(int oid, string typname, short typlen, bool typbyval, string typcategory, string typtype, int typelem)
{
    return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
    {
        ["oid"] = oid,
        ["typname"] = typname,
        ["typnamespace"] = 11,
        ["typowner"] = 10,
        ["typlen"] = typlen,
        ["typbyval"] = typbyval,
        ["typtype"] = typtype,
        ["typcategory"] = typcategory,
        ["typispreferred"] = true,
        ["typisdefined"] = true,
        ["typdelim"] = ",",
        ["typrelid"] = 0,
        ["typelem"] = typelem,
        ["typarray"] = 0,
        ["typinput"] = typname + "in",
        ["typoutput"] = typname + "out",
        ["typreceive"] = typname + "recv",
        ["typsend"] = typname + "send",
        ["typmodin"] = "-",
        ["typmodout"] = "-",
        ["typanalyze"] = "-",
        ["typalign"] = typlen switch { 8 => "d", 4 => "i", 2 => "s", _ => "i" },
        ["typstorage"] = typlen < 0 ? "x" : "p",
        ["typnotnull"] = false,
        ["typbasetype"] = 0,
        ["typtypmod"] = -1,
        ["typndims"] = 0,
        ["typcollation"] = 0,
        ["typdefaultbin"] = null,
        ["typdefault"] = null,
        ["typacl"] = null,
        ["base_type_name"] = typname,
        ["description"] = null,
        ["relkind"] = null
    };
}

static int MapInformationSchemaTypeToPgTypeOid(string? dataType)
{
    var normalized = (dataType ?? string.Empty).Trim().ToLowerInvariant();
    return normalized switch
    {
        "smallint" => 21,
        "integer" or "int" => 23,
        "bigint" => 20,
        "double precision" => 701,
        "real" => 700,
        "boolean" => 16,
        "bytea" => 17,
        "timestamp with time zone" => 1184,
        "timestamp" => 1114,
        _ => 1043
    };
}

static short MapPgTypeSizeFromOid(int pgTypeOid)
{
    return pgTypeOid switch
    {
        16 => 1,
        21 => 2,
        23 or 26 or 700 => 4,
        20 or 701 => 8,
        _ => -1
    };
}

static int StableOid(string value)
{
    if (string.IsNullOrWhiteSpace(value))
        return 20000;

    unchecked
    {
        var hash = 17;
        foreach (var c in value)
            hash = (hash * 31) + c;

        if (hash == int.MinValue)
            hash = int.MaxValue;

        return 20000 + Math.Abs(hash % 500000);
    }
}

static IReadOnlyList<string> DiscoverCollectionNamesFromRuntimeRows(object databaseHandle)
{
    if (databaseHandle is not System.Collections.IEnumerable rows)
        return Array.Empty<string>();
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var row in rows)
    {
        try
        {
            if (row is null)
                continue;

            var ident = row.GetType().GetProperty("Ident", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(row);
            if (ident is null)
                continue;

            var identType = ident.GetType();
            var attribute = Convert.ToInt32(identType.GetProperty("Attribute", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(ident) ?? -1, CultureInfo.InvariantCulture);
            var index = Convert.ToInt32(identType.GetProperty("Index", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(ident) ?? -1, CultureInfo.InvariantCulture);

            if (attribute != 0 || index != 0)
                continue;

            var keyIdent = identType.GetProperty("KeyIdent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(ident);
            if (keyIdent is null)
                continue;

            var key = TryReadKeyIdentString(keyIdent);
            if (string.IsNullOrWhiteSpace(key))
                continue;

            if (!TryExtractTableNameFromMetadataKey(key, out var suffix))
                continue;

            names.Add(suffix);
        }
        catch
        {
            continue;
        }
    }

    return names.OrderBy(static n => n, StringComparer.OrdinalIgnoreCase).ToArray();
}

static IReadOnlyList<string> DiscoverCollectionNamesFromMasterCollection(object databaseHandle)
{
    try
    {
        var getMasterCollection = databaseHandle.GetType().GetMethod("GetMasterCollection", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var masterCollection = getMasterCollection?.Invoke(databaseHandle, null);
        if (masterCollection is not System.Collections.IEnumerable rows)
            return Array.Empty<string>();

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            try
            {
                if (row is null)
                    continue;

                var ident = row.GetType().GetProperty("Ident", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(row);
                if (ident is null)
                    continue;

                var keyIdent = ident.GetType().GetProperty("KeyIdent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(ident);
                if (keyIdent is null)
                    continue;

                var key = TryReadKeyIdentString(keyIdent);
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                if (!TryExtractTableNameFromMetadataKey(key, out var suffix))
                    continue;

                names.Add(suffix);
            }
            catch
            {
                continue;
            }
        }

        return names.OrderBy(static n => n, StringComparer.OrdinalIgnoreCase).ToArray();
    }
    catch
    {
        return Array.Empty<string>();
    }
}

static bool TryExtractTableNameFromMetadataKey(string key, out string tableName)
{
    tableName = string.Empty;
    if (string.IsNullOrWhiteSpace(key))
        return false;

    var match = Regex.Match(key, @"^Database:\d+:Table:(?<name>[^:]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    if (!match.Success)
        return false;

    var value = match.Groups["name"].Value;
    if (string.IsNullOrWhiteSpace(value)
        || value.StartsWith("__", StringComparison.Ordinal)
        || string.Equals(value, "LastCollectionId", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    tableName = value;
    return true;
}

static string? TryReadKeyIdentString(object keyIdent)
{
    try
    {
        var keyType = keyIdent.GetType();
        var asString = keyType.GetMethod("AsString", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(keyIdent, null) as string;
        if (!string.IsNullOrWhiteSpace(asString))
            return asString;

        var text = keyIdent.ToString();
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var marker = "KY:";
        var markerIndex = text.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex >= 0)
        {
            var extracted = text[(markerIndex + marker.Length)..].Trim();
            if (!string.IsNullOrWhiteSpace(extracted) && !string.Equals(extracted, "Byte", StringComparison.OrdinalIgnoreCase))
                return extracted;
        }

        return text;
    }
    catch
    {
        return null;
    }
}

static bool TryResolveScalarStartupQuery(string sql, string normalized, string? databaseName, out VirtualQueryResult result)
{
    if (!normalized.StartsWith("select ", StringComparison.Ordinal) || normalized.Contains(" from ", StringComparison.Ordinal))
    {
        result = new VirtualQueryResult(Array.Empty<(string Name, Type ClrType)>(), Array.Empty<Dictionary<string, object?>>());
        return false;
    }

    var body = sql.Trim();
    if (body.EndsWith(";", StringComparison.Ordinal))
        body = body[..^1].Trim();

    body = body.StartsWith("select", StringComparison.OrdinalIgnoreCase)
        ? body[6..].Trim()
        : body;

    var compactBody = Regex.Replace(body, "\\s+", string.Empty, RegexOptions.CultureInvariant)
        .Replace("\"", string.Empty, StringComparison.Ordinal)
        .ToLowerInvariant();

    if (compactBody is "current_schema(),session_user" or "session_user,current_schema()"
        or "current_schema(),current_user" or "current_user,current_schema()")
    {
        result = new VirtualQueryResult(
            new[] { ("current_schema", typeof(string)), ("session_user", typeof(string)) },
            new[]
            {
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["current_schema"] = "public",
                    ["session_user"] = "postgres"
                }
            });
        return true;
    }

    if (body.Contains(',', StringComparison.Ordinal))
    {
        result = new VirtualQueryResult(Array.Empty<(string Name, Type ClrType)>(), Array.Empty<Dictionary<string, object?>>());
        return false;
    }

    string expression = body;
    string? alias = null;

    var asMatch = Regex.Match(body, @"^(?<expr>.+?)\s+as\s+(?<alias>""?[_a-zA-Z][_a-zA-Z0-9]*""?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    if (asMatch.Success)
    {
        expression = asMatch.Groups["expr"].Value.Trim();
        alias = asMatch.Groups["alias"].Value.Trim().Trim('"');
    }

    var normalizedExpr = expression.Trim().ToLowerInvariant();

    if (normalizedExpr is "1" or "1::int4" or "1::integer")
    {
        result = OneCell(alias ?? "?column?", typeof(int), 1);
        return true;
    }

    if (normalizedExpr is "current_database()" or "pg_catalog.current_database()")
    {
        result = OneCell(alias ?? "current_database", typeof(string), string.IsNullOrWhiteSpace(databaseName) ? "App" : databaseName);
        return true;
    }

    if (normalizedExpr is "current_schema()" or "pg_catalog.current_schema()")
    {
        result = OneCell(alias ?? "current_schema", typeof(string), "public");
        return true;
    }

    if (normalizedExpr is "version()" or "pg_catalog.version()")
    {
        result = OneCell(alias ?? "version", typeof(string), "LayeredSql PgWire 16.0-layeredsql");
        return true;
    }

    if (normalizedExpr is "current_user" or "session_user" or "user")
    {
        result = OneCell(alias ?? "current_user", typeof(string), "postgres");
        return true;
    }

    var currentSettingMatch = Regex.Match(expression, @"^current_setting\('(?<name>[^']+)'(?:\s*,\s*(?:true|false))?\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    if (currentSettingMatch.Success)
    {
        var settingName = currentSettingMatch.Groups["name"].Value;
        var settingValue = settingName.ToLowerInvariant() switch
        {
            "server_version" => "16.0-layeredsql",
            "server_version_num" => "160000",
            "client_encoding" => "UTF8",
            "standard_conforming_strings" => "on",
            "integer_datetimes" => "on",
            "is_superuser" => "on",
            "datestyle" => "ISO, MDY",
            _ => ""
        };

        result = OneCell(alias ?? "current_setting", typeof(string), settingValue);
        return true;
    }

    var setConfigMatch = Regex.Match(expression, @"^(?:pg_catalog\.)?set_config\('(?<name>[^']*)'\s*,\s*'(?<value>[^']*)'\s*,\s*(?:true|false)\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    if (setConfigMatch.Success)
    {
        var configuredValue = setConfigMatch.Groups["value"].Value;
        result = OneCell(alias ?? "set_config", typeof(string), configuredValue);
        return true;
    }

    result = new VirtualQueryResult(Array.Empty<(string Name, Type ClrType)>(), Array.Empty<Dictionary<string, object?>>());
    return false;
}

static VirtualQueryResult OneCell(string columnName, Type columnType, object? value)
{
    return new VirtualQueryResult(
        new[] { (columnName, columnType) },
        new[]
        {
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                [columnName] = value
            }
        });
}

static VirtualQueryResult BuildProjectedVirtualResult(
    string sql,
    IReadOnlyList<Dictionary<string, object?>> rows,
    IReadOnlyList<(string Name, Type ClrType)> fallbackColumns)
{
    var selectedColumns = ExtractSelectedColumns(sql);
    var columns = selectedColumns.Count == 0
        ? fallbackColumns.ToList()
        : selectedColumns
            .Select(column =>
            {
                var fallback = fallbackColumns.FirstOrDefault(x => string.Equals(x.Name, column, StringComparison.OrdinalIgnoreCase));
                return fallback == default ? (column, typeof(string)) : fallback;
            })
            .ToList();

    var projectedRows = new List<Dictionary<string, object?>>(rows.Count);
    foreach (var row in rows)
    {
        var projected = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, _) in columns)
            projected[name] = row.TryGetValue(name, out var value) ? value : null;

        projectedRows.Add(projected);
    }

    return new VirtualQueryResult(columns, projectedRows);
}

static List<string> ExtractSelectedColumns(string sql)
{
    var lower = sql.ToLowerInvariant();
    var selectIndex = lower.IndexOf("select", StringComparison.Ordinal);
    var fromIndex = lower.IndexOf(" from ", StringComparison.Ordinal);
    if (selectIndex < 0 || fromIndex <= selectIndex)
        return new List<string>();

    var projection = sql[(selectIndex + 6)..fromIndex].Trim();
    if (projection == "*" || projection.Length == 0)
        return new List<string>();

    var parts = projection.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    var columns = new List<string>(parts.Length);

    foreach (var part in parts)
    {
        var token = part.Trim();
        if (token.Length == 0)
            continue;

        if (token == "*" || token.EndsWith(".*", StringComparison.Ordinal))
            return new List<string>();

        var asIndex = token.LastIndexOf(" as ", StringComparison.OrdinalIgnoreCase);
        if (asIndex >= 0 && asIndex + 4 < token.Length)
            token = token[(asIndex + 4)..].Trim();
        else
        {
            var ws = token.LastIndexOf(' ');
            if (ws > 0 && ws + 1 < token.Length && token.IndexOf('(') < 0)
                token = token[(ws + 1)..].Trim();
        }

        var dot = token.LastIndexOf('.');
        if (dot >= 0 && dot + 1 < token.Length)
            token = token[(dot + 1)..];

        var cast = token.IndexOf("::", StringComparison.Ordinal);
        if (cast > 0)
            token = token[..cast];

        token = token.Trim('"');
        if (token.Length > 0)
            columns.Add(token);
    }

    return columns;
}

static IReadOnlyList<(string Name, Type ClrType)> InferFieldsFromSelect(string sql)
{
    var names = ExtractSelectedColumns(sql);
    if (names.Count == 0)
        return Array.Empty<(string Name, Type ClrType)>();

    var fields = new List<(string Name, Type ClrType)>(names.Count);
    foreach (var name in names)
        fields.Add((string.IsNullOrWhiteSpace(name) ? "?column?" : name, typeof(string)));

    return fields;
}

static IReadOnlyList<(string Name, Type ClrType)> InferFallbackQueryFields(string sql)
{
    var names = ExtractSelectedColumns(sql);
    if (names.Count > 0)
    {
        var fields = new List<(string Name, Type ClrType)>(names.Count);
        foreach (var name in names)
            fields.Add((string.IsNullOrWhiteSpace(name) ? "?column?" : name, typeof(string)));

        return fields;
    }

    return new[] { ("?column?", typeof(string)) };
}

static string NormalizeSqlForCatalogDetection(string sql)
{
    if (string.IsNullOrWhiteSpace(sql))
        return string.Empty;

    var normalized = Regex.Replace(sql, "\\s+", " ").Trim().ToLowerInvariant();
    normalized = normalized.Replace("\"", string.Empty, StringComparison.Ordinal);
    return normalized;
}

static async Task SendVirtualQueryResultAsync(NetworkStream stream, VirtualQueryResult result)
{
    await SendRowDescriptionAsync(stream, result.Fields);

    foreach (var row in result.Rows)
    {
        var values = result.Fields
            .Select(field => row.TryGetValue(field.Name, out var value) && value != null ? (string?)ToPgText(value) : null)
            .ToArray();

        await SendDataRowAsync(stream, values);
    }

    await SendCommandCompleteAsync(stream, $"SELECT {result.Rows.Count}");
}

static async Task SendVirtualExecuteResultAsync(NetworkStream stream, VirtualQueryResult result, bool includeRowDescription)
{
    if (includeRowDescription)
        await SendRowDescriptionAsync(stream, result.Fields);

    foreach (var row in result.Rows)
    {
        var values = result.Fields
            .Select(field => row.TryGetValue(field.Name, out var value) && value != null ? (string?)ToPgText(value) : null)
            .ToArray();

        await SendDataRowAsync(stream, values);
    }

    await SendCommandCompleteAsync(stream, $"SELECT {result.Rows.Count}");
}

static int MapPgTypeOid(Type clrType)
{
    var type = Nullable.GetUnderlyingType(clrType) ?? clrType;
    if (type == typeof(short)) return 21;
    if (type == typeof(int)) return 23;
    if (type == typeof(long)) return 20;
    if (type == typeof(float)) return 700;
    if (type == typeof(double) || type == typeof(decimal)) return 701;
    if (type == typeof(bool)) return 16;
    if (type == typeof(byte[])) return 17;
    if (type == typeof(DateTime)) return 1184;
    return 25;
}

static short MapPgTypeSize(Type clrType)
{
    var type = Nullable.GetUnderlyingType(clrType) ?? clrType;
    if (type == typeof(short)) return 2;
    if (type == typeof(int) || type == typeof(float)) return 4;
    if (type == typeof(long) || type == typeof(double)) return 8;
    if (type == typeof(bool)) return 1;
    return -1;
}

static async Task SendAuthenticationOkAsync(NetworkStream stream)
{
    var body = new byte[4];
    BinaryPrimitives.WriteInt32BigEndian(body, 0);
    await WriteMessageAsync(stream, (byte)'R', body);
}

static async Task SendParameterStatusAsync(NetworkStream stream, string key, string value)
{
    var payload = BuildCString(key, value);
    await WriteMessageAsync(stream, (byte)'S', payload);
}

static async Task SendBackendKeyDataAsync(NetworkStream stream)
{
    var payload = new byte[8];
    BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0, 4), Environment.ProcessId);
    BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4, 4), RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue));
    await WriteMessageAsync(stream, (byte)'K', payload);
}

static async Task SendReadyForQueryAsync(NetworkStream stream, byte status)
{
    await WriteMessageAsync(stream, (byte)'Z', new[] { status });
}

static async Task SendErrorAsync(NetworkStream stream, string code, string message)
{
    using var ms = new MemoryStream();
    ms.WriteByte((byte)'S');
    WriteCString(ms, "ERROR");
    ms.WriteByte((byte)'C');
    WriteCString(ms, code);
    ms.WriteByte((byte)'M');
    WriteCString(ms, message);
    ms.WriteByte(0);

    await WriteMessageAsync(stream, (byte)'E', ms.ToArray());
}

static async Task SendParseCompleteAsync(NetworkStream stream)
{
    await WriteMessageAsync(stream, (byte)'1', Array.Empty<byte>());
}

static async Task SendBindCompleteAsync(NetworkStream stream)
{
    await WriteMessageAsync(stream, (byte)'2', Array.Empty<byte>());
}

static async Task SendCloseCompleteAsync(NetworkStream stream)
{
    await WriteMessageAsync(stream, (byte)'3', Array.Empty<byte>());
}

static async Task SendNoDataAsync(NetworkStream stream)
{
    await WriteMessageAsync(stream, (byte)'n', Array.Empty<byte>());
}

static async Task SendParameterDescriptionAsync(NetworkStream stream, IReadOnlyList<int> parameterTypeOids)
{
    using var ms = new MemoryStream();
    WriteInt16(ms, (short)parameterTypeOids.Count);
    foreach (var oid in parameterTypeOids)
        WriteInt32(ms, oid);

    await WriteMessageAsync(stream, (byte)'t', ms.ToArray());
}

static async Task SendRowDescriptionAsync(NetworkStream stream, IReadOnlyList<(string Name, Type ClrType)> fields)
{
    using var ms = new MemoryStream();
    WriteInt16(ms, (short)fields.Count);

    foreach (var field in fields)
    {
        WriteCString(ms, field.Name);
        WriteInt32(ms, 0);
        WriteInt16(ms, 0);
        WriteInt32(ms, MapPgTypeOid(field.ClrType));
        WriteInt16(ms, MapPgTypeSize(field.ClrType));
        WriteInt32(ms, -1);
        WriteInt16(ms, 0);
    }

    await WriteMessageAsync(stream, (byte)'T', ms.ToArray());
}

static async Task SendDataRowAsync(NetworkStream stream, IReadOnlyList<string?> values)
{
    using var ms = new MemoryStream();
    WriteInt16(ms, (short)values.Count);

    foreach (var value in values)
    {
        if (value == null)
        {
            WriteInt32(ms, -1);
            continue;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        WriteInt32(ms, bytes.Length);
        ms.Write(bytes, 0, bytes.Length);
    }

    await WriteMessageAsync(stream, (byte)'D', ms.ToArray());
}

static async Task SendCommandCompleteAsync(NetworkStream stream, string tag)
{
    using var ms = new MemoryStream();
    WriteCString(ms, tag);
    await WriteMessageAsync(stream, (byte)'C', ms.ToArray());
}

static byte[] BuildCString(string key, string value)
{
    using var ms = new MemoryStream();
    WriteCString(ms, key);
    WriteCString(ms, value);
    return ms.ToArray();
}

static void WriteCString(Stream stream, string value)
{
    var bytes = Encoding.UTF8.GetBytes(value);
    stream.Write(bytes, 0, bytes.Length);
    stream.WriteByte(0);
}

static void WriteInt16(Stream stream, short value)
{
    Span<byte> buffer = stackalloc byte[2];
    BinaryPrimitives.WriteInt16BigEndian(buffer, value);
    stream.Write(buffer);
}

static void WriteInt32(Stream stream, int value)
{
    Span<byte> buffer = stackalloc byte[4];
    BinaryPrimitives.WriteInt32BigEndian(buffer, value);
    stream.Write(buffer);
}

static async Task WriteMessageAsync(NetworkStream stream, byte messageType, byte[] payload)
{
    var header = new byte[5];
    header[0] = messageType;
    BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1, 4), payload.Length + 4);

    PgWireTrace.Backend((char)messageType, payload.Length + 4, payload.Length);

    await stream.WriteAsync(header);
    if (payload.Length > 0)
        await stream.WriteAsync(payload);

    await stream.FlushAsync();
}

static async Task<byte[]?> ReadExactlyOrNullAsync(NetworkStream stream, int length)
{
    var buffer = new byte[length];
    var offset = 0;
    while (offset < length)
    {
        var read = await stream.ReadAsync(buffer.AsMemory(offset, length - offset));
        if (read == 0)
            return null;

        offset += read;
    }

    return buffer;
}

static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int length)
{
    var buffer = await ReadExactlyOrNullAsync(stream, length);
    if (buffer == null)
        throw new InvalidOperationException("Unexpected end of stream.");

    return buffer;
}

static async Task<int?> ReadByteAsync(NetworkStream stream)
{
    var one = await ReadExactlyOrNullAsync(stream, 1);
    return one == null ? null : one[0];
}

static async Task<int> ReadInt32Async(NetworkStream stream)
{
    var bytes = await ReadExactlyAsync(stream, 4);
    return BinaryPrimitives.ReadInt32BigEndian(bytes);
}

sealed class DbSessionState
{
    public DbTransaction? Transaction { get; set; }
    public bool IgnoreUntilSync { get; set; }
    public Dictionary<string, PreparedStatement> PreparedStatements { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, BoundPortal> Portals { get; } = new(StringComparer.Ordinal);
}

sealed class PreparedStatement
{
    public PreparedStatement(string sql, IReadOnlyList<int> parameterTypeOids)
    {
        Sql = sql;
        ParameterTypeOids = parameterTypeOids;
        MetadataDescribed = false;
    }

    public string Sql { get; }
    public IReadOnlyList<int> ParameterTypeOids { get; }
    public bool MetadataDescribed { get; set; }
}

sealed class BoundPortal
{
    public BoundPortal(string sql, bool isQuery, bool metadataDescribed)
    {
        Sql = sql;
        IsQuery = isQuery;
        MetadataDescribed = metadataDescribed;
    }

    public string Sql { get; }
    public bool IsQuery { get; }
    public bool MetadataDescribed { get; set; }
}

sealed record VirtualQueryResult(
    IReadOnlyList<(string Name, Type ClrType)> Fields,
    IReadOnlyList<Dictionary<string, object?>> Rows);

sealed record VirtualTableDefinition(
    string Name,
    IReadOnlyList<VirtualColumnDefinition> Columns);

sealed record VirtualColumnDefinition(
    string Name,
    string DataType);

sealed class PgWireBootstrapInitializer
{
    private readonly string _connectionString;

    public PgWireBootstrapInitializer(string connectionString)
    {
        _connectionString = connectionString;
    }

    public void EnsureDBeaverSmoke(bool seedSample)
    {
        try
        {
            using var connection = new WalhallaSqlDbConnection(_connectionString);
            connection.Open();

            ExecuteIgnoreAlreadyExists(connection, "CREATE TABLE DBeaverSmoke (Id INT PRIMARY KEY, Name VARCHAR(200))");

            if (!seedSample)
                return;

            ExecuteIgnoreConflict(connection, "INSERT INTO DBeaverSmoke (Id, Name) VALUES (1, 'Ada')");
            ExecuteIgnoreConflict(connection, "INSERT INTO DBeaverSmoke (Id, Name) VALUES (2, 'Grace')");
        }
        catch
        {
        }
    }

    private static void ExecuteIgnoreAlreadyExists(WalhallaSqlDbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        try
        {
            _ = command.ExecuteNonQuery();
        }
        catch (Exception ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    private static void ExecuteIgnoreConflict(WalhallaSqlDbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        try
        {
            _ = command.ExecuteNonQuery();
        }
        catch (Exception ex) when (ex.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
                                   || ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                                   || ex.Message.Contains("unique", StringComparison.OrdinalIgnoreCase))
        {
        }
    }
}

sealed class PgWireOptions
{
    public required string Path { get; init; }
    public required string Database { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required bool Trace { get; init; }
    public required bool SeedSample { get; init; }

    public static PgWireOptions Parse(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                continue;

            var key = args[i][2..];
            var value = i + 1 < args.Length ? args[i + 1] : string.Empty;
            if (value.StartsWith("--", StringComparison.Ordinal))
                value = string.Empty;
            else
                i++;

            options[key] = value;
        }

        var trace = options.TryGetValue("trace", out var traceText)
            && (string.IsNullOrWhiteSpace(traceText)
                || !(traceText.Equals("false", StringComparison.OrdinalIgnoreCase)
                    || traceText.Equals("0", StringComparison.OrdinalIgnoreCase)
                    || traceText.Equals("no", StringComparison.OrdinalIgnoreCase)
                    || traceText.Equals("off", StringComparison.OrdinalIgnoreCase)));

        var seedSample = options.TryGetValue("seed-sample", out var seedSampleText)
            ? ParseBooleanOption(seedSampleText)
            : trace;

        return new PgWireOptions
        {
            Path = options.TryGetValue("path", out var path) && !string.IsNullOrWhiteSpace(path)
                ? path
                : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LayeredSql", "PgWire"),
            Database = options.TryGetValue("database", out var database) && !string.IsNullOrWhiteSpace(database)
                ? database
                : "App",
            Host = options.TryGetValue("host", out var host) && !string.IsNullOrWhiteSpace(host)
                ? host
                : "127.0.0.1",
            Port = options.TryGetValue("port", out var portText) && int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
                ? port
                : 5432,
            Trace = trace,
            SeedSample = seedSample
        };
    }

    private static bool ParseBooleanOption(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;

        return !(value.Equals("false", StringComparison.OrdinalIgnoreCase)
            || value.Equals("0", StringComparison.OrdinalIgnoreCase)
            || value.Equals("no", StringComparison.OrdinalIgnoreCase)
            || value.Equals("off", StringComparison.OrdinalIgnoreCase));
    }
}
