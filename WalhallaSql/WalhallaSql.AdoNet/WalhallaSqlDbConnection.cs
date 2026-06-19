using System;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WalhallaSql.AdoNet.SqlClient;
using WalhallaSql.Sql;

namespace WalhallaSql.AdoNet;

/// <summary>
/// ADO.NET connection implementation for WalhallaSql, supporting embedded in-process,
/// shared in-memory, and remote transports.
/// </summary>
public sealed class WalhallaSqlDbConnection : DbConnection
{
    public const string DefaultDatabaseName = "App";

    internal const string InMemorySentinel = ":memory:";

    private string _connectionString;
    private WalhallaEngine? _engine;
    private EmbeddedEngineRegistry.EmbeddedEngineLease? _embeddedEngineLease;
    private SharedInMemoryRegistry.SharedInMemoryLease? _sharedInMemoryLease;
    private ISqlClientSession? _sqlClientSession;
    private ConnectionState _state;
    private string _databaseName;
    private readonly bool _hasExplicitEngine;
    private bool _engineFromRegistry;

    // Best-effort Session-Pool für InProcess-Verbindungen. Pro Connection-String
    // (bzw. Engine-Identität) wird eine begrenzte Menge an ISqlClientSession-Instanzen
    // vorgehalten, damit Open()/Close() nicht jedes Mal eine neue Session erstellen muss.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentQueue<ISqlClientSession>> _sessionPool = new(StringComparer.Ordinal);
    private const int MaxPooledSessionsPerKey = 8;

    public WalhallaSqlDbConnection()
        : this(string.Empty)
    {
    }

    public WalhallaSqlDbConnection(string connectionString)
    {
        _connectionString = connectionString ?? string.Empty;
        _databaseName = ExtractDatabaseName(_connectionString) ?? DefaultDatabaseName;
        _hasExplicitEngine = false;
    }

    public WalhallaSqlDbConnection(WalhallaEngine engine, string connectionString = "")
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _connectionString = connectionString ?? string.Empty;
        _databaseName = ExtractDatabaseName(_connectionString) ?? DefaultDatabaseName;
        _sqlClientSession = SqlClientSessionFactory.Create(_engine, _connectionString);
        _hasExplicitEngine = true;
    }

    [AllowNull]
    public override string ConnectionString
    {
        get => _connectionString;
        set
        {
            var nextConnectionString = value ?? string.Empty;

            if (_state != ConnectionState.Closed)
            {
                if (string.Equals(_connectionString, nextConnectionString, StringComparison.Ordinal))
                    return;

                throw new InvalidOperationException("ConnectionString cannot be changed while the connection is open.");
            }

            _connectionString = nextConnectionString;
            _databaseName = ExtractDatabaseName(_connectionString) ?? DefaultDatabaseName;

            if (!_hasExplicitEngine)
                _engine = null;

            _sqlClientSession = _engine == null
                ? null
                : SqlClientSessionFactory.Create(_engine, _connectionString);
        }
    }

    public override string Database => _databaseName;

    public override string DataSource => ExtractEmbeddedPath(_connectionString) ?? ExtractDataSource(_connectionString) ?? "embedded";

    public override string ServerVersion => "0.1";

    public override ConnectionState State => _state;

    internal WalhallaEngine EngineHandle => _engine
        ?? throw new InvalidOperationException("Connection has no engine instance.");

    internal bool HasLocalEngine => _engine != null;

    public WalhallaSqlDatabaseInfo GetStorageInfo()
    {
        var dataSource = DataSource;

        if (IsInMemorySentinel(dataSource))
        {
            return new WalhallaSqlDatabaseInfo(
                "WalhallaSql (In-Memory)",
                Database,
                ":memory:",
                false,
                0,
                null);
        }

        var fullPath = System.IO.Path.GetFullPath(dataSource);
        var isDirectory = System.IO.Directory.Exists(fullPath);
        long size = 0;
        DateTime? createdAt = null;

        if (isDirectory)
        {
            try
            {
                var dir = new System.IO.DirectoryInfo(fullPath);
                if (dir.Exists)
                {
                    createdAt = dir.CreationTimeUtc;
                    foreach (var file in dir.EnumerateFiles("*", System.IO.SearchOption.AllDirectories))
                        size += file.Length;
                }
            }
            catch { }

            return new WalhallaSqlDatabaseInfo(
                "WalhallaSql (Embedded)",
                Database,
                fullPath,
                true,
                size,
                createdAt);
        }

        var fileInfo = new System.IO.FileInfo(fullPath);
        if (fileInfo.Exists)
        {
            createdAt = fileInfo.CreationTimeUtc;
            size = fileInfo.Length;
        }

        return new WalhallaSqlDatabaseInfo(
            "WalhallaSql (Embedded)",
            Database,
            fullPath,
            false,
            size,
            createdAt);
    }

    internal ISqlClientSession SqlClientSession => _sqlClientSession
        ?? throw new InvalidOperationException("Connection has no SQL client session instance.");

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        EnsureOpen();

        if (_engine == null && !SqlClientSession.SupportsTransportTransactions)
            throw new NotSupportedException("Transactions are not available for the configured transport.");

        return new WalhallaSqlDbTransaction(this, isolationLevel);
    }

    public override void ChangeDatabase(string databaseName)
    {
        throw new NotSupportedException("WalhallaSql does not support multiple databases per engine instance.");
    }

    public override void Close()
    {
        var session = _sqlClientSession;
        _sqlClientSession = null;

        if (session != null)
        {
            // Für InProcess-Sessions mit geteilter Engine versuchen wir, die Session
            // in den Pool zurückzugeben, statt sie zu disposen.
            if (CanPoolSession(session))
            {
                try
                {
                    session.Reset();
                    ReturnSession(session);
                }
                catch
                {
                    // Bei jedem Problem (aktive Transaktion, Reset fehlgeschlagen) disposen.
                    if (session is IDisposable disposable)
                        disposable.Dispose();
                }
            }
            else if (session is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        if (_embeddedEngineLease != null)
        {
            _embeddedEngineLease.Dispose();
            _embeddedEngineLease = null;
            _engine = null;
        }

        if (_sharedInMemoryLease != null)
        {
            _sharedInMemoryLease.Dispose();
            _sharedInMemoryLease = null;
            _engine = null;
        }

        // Directly-owned engines (not leased, not from registry, not explicit) are disposed when the connection closes.
        if (_engine != null && _embeddedEngineLease == null && _sharedInMemoryLease == null && !_hasExplicitEngine && !_engineFromRegistry)
        {
            _engine.Dispose();
            _engine = null;
        }

        // Reset flags only after the disposal check above, so that a second
        // Close() call (e.g. from EF Core's RelationalConnection cleanup) does
        // not accidentally dispose a shared engine.
        _engineFromRegistry = false;

        _state = ConnectionState.Closed;
    }

    private bool CanPoolSession(ISqlClientSession session)
    {
        // Der Pool darf nur Sessions für Engines aufbewahren, deren Lebensdauer
        // unabhängig von dieser Connection ist. Refcounted Leases (EmbeddedPath,
        // Shared InMemory) werden beim Schließen der letzten Connection disposed;
        // eine gepoolte Session würde dann auf eine disposed/falsche Engine zeigen.
        if (!_engineFromRegistry && !_hasExplicitEngine)
            return false;

        return _engine != null
            && session is WalhallaSqlClientSession
            && SqlClientSessionFactory.GetConfiguredTransport(_connectionString) == SqlClientTransport.InProcess;
    }

    private string GetSessionPoolKey()
    {
        var transport = SqlClientSessionFactory.GetConfiguredTransport(_connectionString).ToString();
        var dataSource = DataSource;
        var database = Database;
        var engineId = _engine == null
            ? "null"
            : RuntimeHelpers.GetHashCode(_engine).ToString(CultureInfo.InvariantCulture);
        return $"{transport}:{dataSource}:{database}:{engineId}";
    }

    private ISqlClientSession? TryAcquireSession()
    {
        if (_engine == null)
            return null;

        var key = GetSessionPoolKey();
        if (!_sessionPool.TryGetValue(key, out var queue))
            return null;

        while (queue.TryDequeue(out var session))
        {
            try
            {
                session.Reset();
                return session;
            }
            catch
            {
                if (session is IDisposable disposable)
                    disposable.Dispose();
            }
        }

        return null;
    }

    private void ReturnSession(ISqlClientSession session)
    {
        var key = GetSessionPoolKey();
        var queue = _sessionPool.GetOrAdd(key, static _ => new System.Collections.Concurrent.ConcurrentQueue<ISqlClientSession>());

        // Begrenzte Poolgröße: alte Einträge verwerfen, wenn das Limit erreicht ist.
        var spun = 0;
        while (queue.Count >= MaxPooledSessionsPerKey && spun < MaxPooledSessionsPerKey)
        {
            if (queue.TryDequeue(out var surplus) && surplus is IDisposable surplusDisposable)
                surplusDisposable.Dispose();
            spun++;
        }

        queue.Enqueue(session);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Close();

        base.Dispose(disposing);
    }

    public override void Open()
    {
        if (_state == ConnectionState.Open)
            return;

        var configuredTransport = SqlClientSessionFactory.GetConfiguredTransport(_connectionString);

        if (_engine == null)
        {
            if (configuredTransport == SqlClientTransport.InProcess)
            {
                var embeddedPath = ExtractEmbeddedPath(_connectionString);
                var dataSource = ExtractDataSource(_connectionString);

                if (IsInMemorySentinel(embeddedPath) || IsInMemorySentinel(dataSource))
                {
                    if (IsInMemorySentinel(embeddedPath) && !string.IsNullOrWhiteSpace(dataSource) && !IsInMemorySentinel(dataSource))
                    {
                        throw new InvalidOperationException(
                            "EmbeddedPath/File ':memory:' cannot be combined with a non-':memory:' DataSource/Server/Host.");
                    }

                    if (IsInMemorySentinel(dataSource) && !string.IsNullOrWhiteSpace(embeddedPath) && !IsInMemorySentinel(embeddedPath))
                    {
                        throw new InvalidOperationException(
                            "DataSource ':memory:' cannot be combined with a non-':memory:' EmbeddedPath/File.");
                    }

                    var mode = ExtractMode(_connectionString);
                    var sharedName = ExtractSharedName(_connectionString);

                    if (mode?.Equals("Shared", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        if (string.IsNullOrWhiteSpace(sharedName))
                            throw new InvalidOperationException(
                                "Mode=Shared requires a 'Name' key in the connection string.");

                        var lease = SharedInMemoryRegistry.Acquire(sharedName);
                        try
                        {
                            _engine = lease.Engine;
                            _sharedInMemoryLease = lease;
                        }
                        catch
                        {
                            lease.Dispose();
                            throw;
                        }
                    }
                    else
                    {
                        _engine = WalhallaEngine.InMemory();
                    }
                }
                else if (!string.IsNullOrWhiteSpace(embeddedPath))
                {
                    if (!string.IsNullOrWhiteSpace(dataSource))
                    {
                        throw new InvalidOperationException(
                            "EmbeddedPath/File cannot be combined with DataSource/Server/Host for in-process connections.");
                    }

                    EnsureDirectoryExists(embeddedPath);
                    var lease = EmbeddedEngineRegistry.Acquire(embeddedPath);
                    try
                    {
                        _engine = lease.Engine;
                        _embeddedEngineLease = lease;
                    }
                    catch
                    {
                        lease.Dispose();
                        throw;
                    }
                }
                else if (!string.IsNullOrWhiteSpace(dataSource))
                {
                    if (WalhallaSqlConnectionRegistry.TryResolve(dataSource, out var engine) && engine != null)
                    {
                        _engine = engine;
                        _engineFromRegistry = true;
                    }
                    else if (LooksLikeFilePath(dataSource))
                    {
                        EnsureDirectoryExists(dataSource);
                        var lease = EmbeddedEngineRegistry.Acquire(dataSource);
                        try
                        {
                            _engine = lease.Engine;
                            _embeddedEngineLease = lease;
                        }
                        catch
                        {
                            lease.Dispose();
                            throw;
                        }
                    }
                    else
                    {
                        // Unrecognized non-file DataSource (e.g. EF Core spec-test fixture names).
                        // Fall back to a transient in-memory engine so tests can open connections
                        // without explicit registration.
                        _engine = WalhallaEngine.InMemory();
                    }
                }
                else
                {
                    throw new InvalidOperationException(
                        "Cannot open in-process connection without DataSource or EmbeddedPath/File. " +
                        "Configure the connection string with 'EmbeddedPath=mydata.db' " +
                        "or use WalhallaSqlEfCoreOptions.ForEmbeddedPath(\"mydata.db\"). " +
                        "Relative paths are resolved against the current working directory. " +
                        "The database is stored as a directory, not a single file.");
                }
            }
        }

        if (_sqlClientSession == null)
            _sqlClientSession = TryAcquireSession() ?? SqlClientSessionFactory.Create(_engine, _connectionString);

        _state = ConnectionState.Open;
    }

    public override Task OpenAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);

        try
        {
            Open();
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }

    protected override DbCommand CreateDbCommand()
    {
        return new WalhallaSqlDbCommand(this);
    }

    internal SqlExecutionResult ExecuteSql(string sql, bool hasExternalTransaction = false)
    {
        EnsureOpen();

        return SqlClientSession.Execute(new SqlClientCommand(sql, hasExternalTransaction));
    }

    internal SqlExecutionResult ExecuteSql(SqlClientCommand command)
    {
        EnsureOpen();

        return SqlClientSession.Execute(command);
    }

    internal SqlClientStreamResult ExecuteSqlStream(string sql, bool hasExternalTransaction = false)
    {
        EnsureOpen();

        return SqlClientSession.ExecuteStream(new SqlClientCommand(sql, hasExternalTransaction));
    }

    internal SqlClientStreamResult ExecuteSqlStream(SqlClientCommand command)
    {
        EnsureOpen();

        return SqlClientSession.ExecuteStream(command);
    }

    public SqlExecutionResult[] ExecuteBatch(IEnumerable<string> sqlStatements)
    {
        ArgumentNullException.ThrowIfNull(sqlStatements);

        var commands = sqlStatements
            .Select(sql => new SqlClientCommand(sql ?? throw new InvalidOperationException("Batch SQL command must not be null.")))
            .ToArray();

        return ExecuteBatch(commands);
    }

    public SqlExecutionResult[] ExecuteBatch(IEnumerable<SqlClientCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);

        EnsureOpen();
        var batchCommands = commands.ToArray();
        return SqlClientSession.ExecuteBatch(batchCommands);
    }

    private void EnsureOpen()
    {
        if (_state != ConnectionState.Open)
            throw new InvalidOperationException("Connection is not open.");
    }

    private static string? ExtractDataSource(string connectionString)
    {
        var match = Regex.Match(connectionString ?? string.Empty, @"(?:^|;)\s*(Data\s*Source|Server|Host)\s*=\s*(?<value>[^;]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }

    private static string? ExtractEmbeddedPath(string connectionString)
    {
        var match = Regex.Match(connectionString ?? string.Empty, @"(?:^|;)\s*(EmbeddedPath|File)\s*=\s*(?<value>[^;]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }

    private static string? ExtractMode(string connectionString)
    {
        var match = Regex.Match(connectionString ?? string.Empty, @"(?:^|;)\s*Mode\s*=\s*(?<value>[^;]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }

    private static string? ExtractSharedName(string connectionString)
    {
        var match = Regex.Match(connectionString ?? string.Empty, @"(?:^|;)\s*Name\s*=\s*(?<value>[^;]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }

    internal static bool IsInMemorySentinel(string? value)
    {
        return value != null
            && value.Trim().Equals(InMemorySentinel, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractDatabaseName(string connectionString)
    {
        var match = Regex.Match(connectionString ?? string.Empty, @"(?:^|;)\s*(Database|Initial Catalog)\s*=\s*(?<value>[^;]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }

    private static bool LooksLikeFilePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();

        if (trimmed.Contains(Path.DirectorySeparatorChar) || trimmed.Contains(Path.AltDirectorySeparatorChar))
            return true;

        if (trimmed.EndsWith(".layered", StringComparison.OrdinalIgnoreCase))
            return true;

        if (File.Exists(trimmed) || Directory.Exists(trimmed))
            return true;

        if (Path.IsPathRooted(trimmed))
            return true;

        return false;
    }

    private static void EnsureDirectoryExists(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);
    }

    private static string SetConnectionValue(string connectionString, string key, string value)
    {
        var segments = (connectionString ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var replaced = false;
        for (var i = 0; i < segments.Count; i++)
        {
            var parts = segments[i].Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
                continue;

            if (!parts[0].Equals(key, StringComparison.OrdinalIgnoreCase)
                && !(key.Equals("Database", StringComparison.OrdinalIgnoreCase)
                    && parts[0].Equals("Initial Catalog", StringComparison.OrdinalIgnoreCase)))
                continue;

            segments[i] = $"{parts[0]}={value}";
            replaced = true;
            break;
        }

        if (!replaced)
            segments.Add($"{key}={value}");

        return string.Join(';', segments);
    }

}
