using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WalhallaSql.Caching;
using WalhallaSql.Catalog;
using WalhallaSql.Collation;
using WalhallaSql.Core;
using WalhallaSql.Execution;
using WalhallaSql.Execution.Join;
using WalhallaSql.Execution.Window;
using WalhallaSql.Parsing;
using WalhallaSql.Sql;
using WalhallaSql.Statistics;
using WalhallaSql.Storage;
using Walhalla.Storage.Contract;
using Walhalla.Storage.Mvcc.Transactions;

namespace WalhallaSql;

public sealed class WalhallaEngine : IDisposable
{
    private readonly WalhallaOptions _options;
    private readonly TableStore _store;
    private readonly string? _ownedPath;
    // Exclusive cross-process lock on the database directory (null for InMemory).
    // Held for the engine lifetime via FileShare.None on '<RootPath>/wal.lock'.
    private readonly FileStream? _rootLock;
    private readonly Dictionary<string, SqlCreateViewStatement> _views = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SqlStoredProcedureDefinition> _procedures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<SqlNativeProcedureContext, WalhallaResultSet>> _compiledProcedures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<SqlTriggerDefinition>> _triggersByTable = new(StringComparer.OrdinalIgnoreCase);
    // Guards _views, _procedures, _compiledProcedures, _triggersByTable.
    // Held only while reading/mutating these dictionaries (and trigger lists), never while
    // invoking arbitrary SQL/procedure bodies (those run after snapshotting under this lock).
    private readonly object _metaSync = new();
    private int _disposed;
    private readonly BoundedLruCache<CompiledPlan>? _planCache;
    private long _planCacheHits;
    private long _planCacheMisses;
    private long _analyzeTableCount;
    private long _analyzeDurationMs;
    private volatile IsolationLevel _defaultIsolationLevel = IsolationLevel.Snapshot;
    private long _tempTableCounter;
    private TransactionMode? _transactionMode;
    private readonly StatisticsCatalog _statisticsCatalog = new();
    private readonly AuthIdCatalog _authIdCatalog;

    internal bool UseMvcc => _transactionMode == TransactionMode.Mvcc
        || (_transactionMode == null && _options.StorageMode == StorageMode.MvccBPlusTree);

    public long PlanCacheHits => Volatile.Read(ref _planCacheHits);
    public long PlanCacheMisses => Volatile.Read(ref _planCacheMisses);
    public long AnalyzeTableCount => Volatile.Read(ref _analyzeTableCount);
    public long AnalyzeDurationMs => Volatile.Read(ref _analyzeDurationMs);
    public long EstimatorHits => _statisticsCatalog.Hits;
    public long EstimatorFallbacks => _statisticsCatalog.Misses;

    /// <summary>
    /// Authentication identity catalog (roles / users).
    /// </summary>
    public AuthIdCatalog AuthIdCatalog => _authIdCatalog;

    public WalhallaEngine(WalhallaOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _transactionMode = options.TransactionMode;

        // Acquire exclusive cross-process lock on the database directory before opening any files,
        // so a second process gets a clear error rather than silently corrupting WAL/checkpoint.
        if (_options.StorageMode != StorageMode.InMemory
            && !string.IsNullOrEmpty(_options.RootPath)
            && _options.RootPath != ":memory:")
        {
            try
            {
                Directory.CreateDirectory(_options.RootPath);
                var lockPath = Path.Combine(_options.RootPath, "wal.lock");
                _rootLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                                           FileShare.None, bufferSize: 16, FileOptions.None);
            }
            catch (IOException ex)
            {
                throw new WalhallaException(
                    $"Database directory '{_options.RootPath}' is already in use by another process.", ex);
            }
        }

        _store = new TableStore(options);
        _authIdCatalog = new AuthIdCatalog(
            options.StorageMode != StorageMode.InMemory ? options.RootPath : null);
        LoadStatisticsFromStore();

        var cacheCapacity = ParsePlanCacheCapacity();
        if (cacheCapacity > 0)
            _planCache = new BoundedLruCache<CompiledPlan>(cacheCapacity);
    }

    private WalhallaEngine(WalhallaOptions options, string ownedPath)
        : this(options)
    {
        _ownedPath = ownedPath;
    }

    private int ParsePlanCacheCapacity()
    {
        var env = Environment.GetEnvironmentVariable("WALHALLASQL_PLAN_CACHE_CAPACITY");
        if (env != null && int.TryParse(env, NumberStyles.Integer, CultureInfo.InvariantCulture, out var capacity))
            return capacity;
        return _options.PlanCacheCapacity;
    }

    private void LoadStatisticsFromStore()
    {
        foreach (var (tableId, stats) in _store.LoadAllStatistics())
            _statisticsCatalog.Set(tableId, stats);
    }

    private void InvalidatePlanCache()
    {
        _planCache?.Clear();
    }

    private readonly Dictionary<string, int> _schemaVersions = new(StringComparer.OrdinalIgnoreCase);

    private int GetSchemaVersion(string tableName)
    {
        lock (_schemaVersions)
        {
            if (_schemaVersions.TryGetValue(tableName, out var version))
                return version;
            return 0;
        }
    }

    private void BumpSchemaVersion(string tableName)
    {
        lock (_schemaVersions)
        {
            _schemaVersions.TryGetValue(tableName, out var current);
            _schemaVersions[tableName] = current + 1;
        }
    }

    private string BuildPlanCacheKey(string sql, string? tableName)
    {
        var version = tableName != null ? GetSchemaVersion(tableName) : 0;
        return $"plan:{sql}:v{version}";
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // No active transactions to rollback in table-level locking model.
        // Each transaction is independently managed � users must dispose their own TXs.

        try
        {
            _store.Dispose();
        }
        finally
        {
            try { _rootLock?.Dispose(); }
            catch { /* best-effort */ }

            if (_ownedPath != null)
            {
                try { Directory.Delete(_ownedPath, true); }
                catch { /* best-effort cleanup */ }
            }
        }
    }

    public static WalhallaEngine Open(string rootPath)
    {
        ArgumentNullException.ThrowIfNull(rootPath);
        if (":memory:".Equals(rootPath.Trim(), StringComparison.OrdinalIgnoreCase))
            return InMemory();

        var options = new WalhallaOptions(rootPath);
        return new WalhallaEngine(options);
    }

    public static WalhallaEngine InMemory()
    {
        var options = new WalhallaOptions(":memory:")
        {
            StorageMode = StorageMode.InMemory,
            WalSyncMode = WalSyncMode.None,
            AutoCheckpointWalThresholdBytes = 0
        };
        return new WalhallaEngine(options);
    }

    /// <summary>
    /// Opens an on-disk WalhallaSql database asynchronously.
    /// This is a convenience wrapper around the synchronous constructor;
    /// the method returns a completed task so it can be awaited in async entry points.
    /// </summary>
    public static Task<WalhallaEngine> OpenAsync(string rootPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rootPath);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Open(rootPath));
    }

    /// <summary>
    /// Opens a WalhallaSql database from existing options asynchronously.
    /// This is a convenience wrapper around the synchronous constructor;
    /// the method returns a completed task so it can be awaited in async entry points.
    /// </summary>
    public static Task<WalhallaEngine> OpenAsync(WalhallaOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new WalhallaEngine(options));
    }

    // ── Blob sidecar helpers (Phase H) ───────────────────────────────────────

    /// <summary>
    /// Konvertiert einen einzelnen Spaltenwert in den Zieltyp (für ALTER COLUMN).
    /// </summary>
    private static object? ConvertColumnValue(object? value, SqlScalarType targetType, bool nullable)
    {
        if (value == null || value == DBNull.Value)
            return nullable ? null : throw new WalhallaException("Cannot convert NULL to a non-nullable column.");

        try
        {
            return targetType switch
            {
                SqlScalarType.Int32 => Convert.ToInt32(value),
                SqlScalarType.Int64 => Convert.ToInt64(value),
                SqlScalarType.Double => Convert.ToDouble(value),
                SqlScalarType.Decimal => Convert.ToDecimal(value),
                SqlScalarType.Boolean => Convert.ToBoolean(value),
                SqlScalarType.DateTime => Convert.ToDateTime(value),
                SqlScalarType.Guid => Guid.Parse(value.ToString()!),
                SqlScalarType.Binary => value is byte[] bytes ? bytes : Convert.FromBase64String(value.ToString()!),
                SqlScalarType.String => value.ToString()!,
                _ => value
            };
        }
        catch (Exception ex) when (ex is not WalhallaException)
        {
            throw new WalhallaException($"Cannot convert column value '{value}' to {targetType}: {ex.Message}", ex);
        }
    }

    private byte[] EncodeRowWithBlobs(int tableId, object?[] values, SqlTableDefinition def)
    {
        // Phase H: reuse existing BlobRef when re-encoding a row (e.g. UPDATE of
        // a non-blob column).  PendingBlobValue created by ResolveBlobs carries
        // the original BlobRef so we can avoid re-appending the payload.
        bool mutated = false;
        for (int i = 0; i < def.Columns.Count; i++)
        {
            if (def.Columns[i].Type == SqlScalarType.Binary && values[i] is PendingBlobValue pb)
            {
                if (!mutated)
                {
                    values = (object?[])values.Clone();
                    mutated = true;
                }
                values[i] = pb.BlobRef != null ? pb.BlobRef : pb.ToArray();
            }
        }

        var offloaded = _store.OffloadBlobs(tableId, values, def);
        return RowCodec.Encode(offloaded, def);
    }

    private object?[] DecodeRowWithBlobs(int tableId, byte[] encoded, SqlTableDefinition def)
    {
        var values = RowCodec.DecodeToArray(encoded, def);
        _store.ResolveBlobs(tableId, values, def);
        return values;
    }

    private object?[] DecodeRowWithBlobs(int tableId, ReadOnlySpan<byte> encoded, SqlTableDefinition def)
    {
        var values = RowCodec.DecodeToArray(encoded, def);
        _store.ResolveBlobs(tableId, values, def);
        return values;
    }

    private object?[] DecodeRowPooledWithBlobs(int tableId, byte[] encoded, SqlTableDefinition def)
    {
        var values = RowCodec.DecodeToPooledArray(encoded, def);
        _store.ResolveBlobs(tableId, values, def);
        return values;
    }

    private object?[] DecodeRowPooledWithBlobs(int tableId, ReadOnlySpan<byte> encoded, SqlTableDefinition def)
    {
        var values = RowCodec.DecodeToPooledArray(encoded.ToArray(), def);
        _store.ResolveBlobs(tableId, values, def);
        return values;
    }

    private object?[] DecodeColumnsWithBlobs(int tableId, ReadOnlySpan<byte> encoded, SqlTableDefinition def, int[] indices)
    {
        var values = RowCodec.DecodeColumns(encoded, def, indices);
        _store.ResolveBlobs(tableId, values, def, indices);
        return values;
    }

    public WalhallaResultSet Execute(string sql)
    {
        // B.5: Point-query fast path � skip parser for simple PK lookups
        var fastResult = TryFastPathSelect(sql);
        if (fastResult != null)
            return fastResult;

        sql = CorrelatedSubqueryRewriter.Rewrite(sql);
        var statement = SqlStatementParser.Parse(sql);

        switch (statement)
        {
            case SqlExplainStatement explain:
                return ExecuteExplain(explain);

            case SqlSelectStatement select:
                return ExecuteSelect(select, planCacheKey: sql);

            case SqlInsertStatement insert:
                return ExecuteInsert(insert);

            case SqlInsertSelectStatement insertSelect:
                return ExecuteInsertSelect(insertSelect);

            case SqlMergeStatement merge:
                return ExecuteMerge(merge);

            case SqlCompoundSelectStatement compound:
                return ExecuteCompoundSelect(compound);

            case SqlWithStatement withStmt:
                return ExecuteWith(withStmt);

            case SqlAlterTableStatement alterTable:
                return ExecuteAlterTable(alterTable);

            case SqlCreateViewStatement createView:
                return ExecuteCreateView(createView);

            case SqlDropViewStatement dropView:
                return ExecuteDropView(dropView);

            case SqlCreateTableStatement create:
                return ExecuteCreateTable(create);

            case SqlDropTableStatement drop:
                return ExecuteDropTable(drop);

            case SqlTruncateTableStatement truncate:
                return ExecuteTruncateTable(truncate);

            case SqlUpdateStatement update:
                return ExecuteUpdate(update);

            case SqlDeleteStatement delete:
                return ExecuteDelete(delete);

            case SqlCreateIndexStatement createIndex:
                return ExecuteCreateIndex(createIndex);

            case SqlDropIndexStatement dropIndex:
                return ExecuteDropIndex(dropIndex);

            case SqlCreateProcedureStatement createProc:
                return ExecuteCreateProcedure(createProc);

            case SqlDropProcedureStatement dropProc:
                return ExecuteDropProcedure(dropProc);

            case SqlExecStatement exec:
                return ExecuteExec(exec);

            case SqlCreateTriggerStatement createTrigger:
                return ExecuteCreateTrigger(createTrigger);

            case SqlDropTriggerStatement dropTrigger:
                return ExecuteDropTrigger(dropTrigger);

            case SqlSetTransactionStatement setTx:
                _defaultIsolationLevel = MapIsolationLevel(setTx.IsolationLevelName);
                return WalhallaResultSet.Affected(0);

            case SqlSetTransactionModeStatement setMode:
                return ExecuteSetTransactionMode(setMode);

            case SqlVacuumStatement vacuum:
                return ExecuteVacuum(vacuum);

            case SqlAnalyzeStatement analyze:
                return ExecuteAnalyze(analyze);

            default:
                throw new NotSupportedException($"Statement type '{statement.GetType().Name}' is not supported.");
        }
    }

    public WalhallaPreparedStatement Prepare(string sql)
    {
        sql = CorrelatedSubqueryRewriter.Rewrite(sql);
        var statement = SqlStatementParser.Parse(sql);
        if (statement is not SqlSelectStatement select)
            throw new NotSupportedException("Only SELECT statements can be prepared.");

        // Views: redirect to the underlying SELECT
        SqlCreateViewStatement? viewDef;
        lock (_metaSync)
            _views.TryGetValue(select.TableName, out viewDef);
        if (viewDef != null)
            select = viewDef.SelectStatement;

        // Schema-version-aware plan cache lookup
        var cacheKey = BuildPlanCacheKey(sql, select.TableName);
        CompiledPlan plan;
        if (_planCache != null && _planCache.TryGet(cacheKey, out var cachedPlan))
        {
            Interlocked.Increment(ref _planCacheHits);
            plan = cachedPlan;
        }
        else
        {
            Interlocked.Increment(ref _planCacheMisses);
            plan = QueryPlanner.Build(select, _store, ResolveSubquery, _statisticsCatalog);
            _planCache?.Set(cacheKey, plan);
        }

        var paramOrdinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (select.Parameters != null)
        {
            for (int i = 0; i < select.Parameters.Count; i++)
                paramOrdinals[select.Parameters[i]] = i;
        }

        return new WalhallaPreparedStatement(plan, paramOrdinals, _store);
    }

    public void CreateTable(SqlTableDefinition table)
    {
        _store.CreateTable(table);
    }

    public void DropTable(string tableName)
    {
        _store.DropTable(tableName);
    }

    public SqlTableDefinition? GetTable(string tableName)
    {
        return _store.GetTableDefinition(tableName);
    }

    public SqlTableDefinition? GetTableDefinition(string name) => _store.GetTableDefinition(name);

    /// <summary>
    /// Aktualisiert die Tabellen-Definition im Katalog (z. B. zum Nachtragen von Projektionen).
    /// </summary>
    public void UpdateTableDefinition(string name, SqlTableDefinition newDef)
    {
        var tableId = _store.GetTableId(name);
        if (tableId < 0)
            throw new WalhallaException($"Table '{name}' not found.");
        _store.UpdateTableDefinition(name, tableId, newDef);
    }

    public IReadOnlyList<SqlTableDefinition> GetAllTables()
    {
        return _store.GetAllTables();
    }

    public void InsertBatch(string tableName, IReadOnlyList<object?[]> rows)
    {
        if (rows.Count == 0) return;

        var tableDef = _store.GetTableDefinition(tableName)
            ?? throw new WalhallaException($"Table '{tableName}' not found.");

        var tableId = _store.GetTableId(tableName);
        var encodedRows = new byte[rows.Count][];
        for (int i = 0; i < rows.Count; i++)
            encodedRows[i] = EncodeRowWithBlobs(tableId, rows[i], tableDef);

        // SQLite-style "INTEGER PRIMARY KEY" alias: when the table has a single BIGINT PK,
        // the user-supplied value becomes the storage row id. Otherwise we fall back to the
        // monotonic auto-rowid allocator inside TableStore.InsertRows.
        long[]? explicitRowIds = null;
        long startRowId;
        if (tableDef.TryGetRowIdAliasPk(out var pkIdx))
        {
            var pkColName = tableDef.PrimaryKeyColumns[0].Name;
            explicitRowIds = new long[rows.Count];
            for (int i = 0; i < rows.Count; i++)
            {
                var pkValue = rows[i][pkIdx]
                    ?? throw new WalhallaException(
                        $"PRIMARY KEY column '{pkColName}' in table '{tableName}' cannot be NULL.");
                explicitRowIds[i] = Convert.ToInt64(pkValue);
            }
            startRowId = 0; // unused; index entries use explicitRowIds[i] below
        }
        else
        {
            startRowId = _store.GetNextRowId(tableId);
        }

        // Collect index entries � build full keys in one allocation.
        var indexMetas = GetOrBuildIndexMetadata(tableName, tableDef);
        var allIndexEntries = indexMetas.Length == 0
            ? null
            : new (int IndexId, byte[] SortKey, int TableId, long RowId)[rows.Count * indexMetas.Length];
        int entryIdx = 0;
        foreach (var meta in indexMetas)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                var rowId = explicitRowIds != null ? explicitRowIds[i] : startRowId + i;
                var fullKey = IndexKeyCodec.BuildIndexEntryKey(
                    rows[i], meta.IndexId, meta.ColumnIndices, meta.KeyTypes, tableId, rowId);
                // IndexId = -1 signals InsertRows that SortKey is already the full key.
                allIndexEntries![entryIdx++] = (-1, fullKey, 0, 0);
            }
        }

        // Single WAL batch: rows + all index entries.
        _store.InsertRows(tableId, encodedRows, explicitRowIds, allIndexEntries);
    }

    /// <summary>
    /// For tables with INTEGER PRIMARY KEY alias semantics, returns the user-supplied PK
    /// value as the storage row id; otherwise allocates a fresh auto row id.
    /// </summary>
    private long ResolvePkAliasRowId(SqlTableDefinition tableDef, object?[] row, int tableId)
    {
        if (tableDef.TryGetRowIdAliasPk(out var pkIdx))
        {
            var pkVal = row[pkIdx]
                ?? throw new WalhallaException(
                    $"PRIMARY KEY column '{tableDef.PrimaryKeyColumns[0].Name}' in table '{tableDef.CollectionName}' cannot be NULL.");
            return Convert.ToInt64(pkVal);
        }
        return _store.GetNextRowId(tableId);
    }

    public void Checkpoint()
    {
        _store.Checkpoint();
    }

    public int Vacuum(string? tableName = null)
    {
        return _store.Vacuum(tableName);
    }

    /// <summary>Returns the most recently computed statistics for <paramref name="tableName"/>, or null if not yet analyzed.</summary>
    public TableStatistics? GetStatistics(string tableName)
    {
        int tableId = _store.GetTableId(tableName);
        if (tableId < 0) return null;
        return _statisticsCatalog.TryGet(tableId, out var stats) ? stats : null;
    }

    // -- Transaction API --------------------------------------------------------

    internal static IsolationLevel MapIsolationLevel(string isoLevelName)
    {
        return isoLevelName.ToUpperInvariant() switch
        {
            "READ UNCOMMITTED" => IsolationLevel.ReadCommitted,
            "READ COMMITTED" => IsolationLevel.ReadCommitted,
            "REPEATABLE READ" => IsolationLevel.Snapshot,
            "SERIALIZABLE" => IsolationLevel.Serializable,
            _ => throw new ArgumentException($"Unsupported isolation level: {isoLevelName}")
        };
    }

    private WalhallaResultSet ExecuteSetTransactionMode(SqlSetTransactionModeStatement stmt)
    {
        if (!UseMvcc && !string.Equals(stmt.ModeName, "LOCKING", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(
                "Transaction mode 'mvcc' requires MvccBPlusTree storage mode. The current storage mode does not support MVCC.");
        _transactionMode = string.Equals(stmt.ModeName, "MVCC", StringComparison.OrdinalIgnoreCase)
            ? TransactionMode.Mvcc
            : TransactionMode.Locking;
        return WalhallaResultSet.Affected(0);
    }

    public WalhallaSqlTransaction BeginTransaction()
    {
        var tx = new WalhallaSqlTransaction(this);
        if (UseMvcc)
        {
            var storageTx = _store.CreateTransaction(_defaultIsolationLevel);
            if (storageTx != null)
            {
                tx.SetStorageTransaction(storageTx);
                tx.SetIsolationLevel(_defaultIsolationLevel);
            }
        }
        return tx;
    }

    internal void CommitTransaction(WalhallaSqlTransaction tx)
    {
        bool hasWrites = tx.Writes.Count > 0;
        bool hasIndexOps = tx.IndexOps.Count > 0;

        if (!hasWrites && !hasIndexOps)
        {
            CleanupStorageTransaction(tx);
            _store.LockManager.ReleaseAllRowLocks(tx);
            return;
        }

        if (!UseMvcc)
        {
            if (hasWrites)
                _store.ApplyBatch(tx.Writes);
            if (hasIndexOps)
                _store.ApplyIndexBatch(tx.IndexOps);
            _store.CommitStore();
            _store.LockManager.ReleaseAllRowLocks(tx);
            return;
        }

        // MVCC mode: retry on conflict
        int maxRetries = _options.MaxTransactionRetries;
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            // Reuse the transaction created at BeginTransaction() time, or create on retry
            var storageTx = tx.StorageTransaction;
            if (storageTx == null)
            {
                storageTx = _store.CreateTransaction(tx.StorageIsolationLevel ?? IsolationLevel.Snapshot);
                if (storageTx == null) break;
                tx.SetStorageTransaction(storageTx);
                tx.SetIsolationLevel(tx.StorageIsolationLevel ?? IsolationLevel.Snapshot);
            }

            try
            {
                if (hasWrites)
                    _store.ApplyBatch(tx.Writes, storageTx);
                if (hasIndexOps)
                    _store.ApplyIndexBatch(tx.IndexOps, storageTx);
                storageTx.Commit();
                _store.CommitStore();
                break; // success
            }
            catch (TransactionConflictException) when (attempt < maxRetries - 1)
            {
                // On conflict, dispose the failed tx and create a fresh one for retry
                tx.SetStorageTransaction(null);
                storageTx.Dispose();
                int delayMs = Math.Min(10 << attempt, 200);
                Thread.Sleep(delayMs);
            }
            catch
            {
                storageTx.Rollback();
                CleanupStorageTransaction(tx);
                throw;
            }
        }

        CleanupStorageTransaction(tx);
        _store.LockManager.ReleaseAllRowLocks(tx);
    }

    private static void CleanupStorageTransaction(WalhallaSqlTransaction tx)
    {
        tx.StorageTransaction?.Dispose();
        tx.SetStorageTransaction(null);
    }

    internal void RollbackTransaction(WalhallaSqlTransaction tx)
    {
        var storageTx = tx.StorageTransaction;
        if (storageTx != null)
        {
            try { storageTx.Rollback(); } catch { /* best-effort */ }
            CleanupStorageTransaction(tx);
        }
        _store.LockManager.ReleaseAllRowLocks(tx);
    }

    /// <summary>Execute SQL within an optional transaction context.</summary>
    public WalhallaResultSet Execute(string sql, WalhallaSqlTransaction? transaction)
    {
        if (transaction == null)
            return Execute(sql);

        // Inside a transaction � check for transaction control statements first
        var trimmed = sql.TrimStart();
        if (trimmed.StartsWith("COMMIT", StringComparison.OrdinalIgnoreCase))
        {
            transaction.Commit();
            return WalhallaResultSet.Affected(0);
        }
        if (trimmed.StartsWith("ROLLBACK", StringComparison.OrdinalIgnoreCase))
        {
            // ROLLBACK TO <savepoint> is handled below via parser
            if (trimmed.Length > 8 && char.IsWhiteSpace(trimmed[8]))
            {
                var afterRollback = trimmed[8..].TrimStart();
                if (afterRollback.StartsWith("TO", StringComparison.OrdinalIgnoreCase) &&
                    afterRollback.Length > 2 && char.IsWhiteSpace(afterRollback[2]))
                {
                    var name = afterRollback[2..].Trim();
                    transaction.RollbackTo(name);
                    return WalhallaResultSet.Affected(0);
                }
            }
            transaction.Rollback();
            return WalhallaResultSet.Affected(0);
        }
        if (trimmed.StartsWith("SAVEPOINT", StringComparison.OrdinalIgnoreCase))
        {
            var name = trimmed[9..].Trim();
            transaction.Savepoint(name);
            return WalhallaResultSet.Affected(0);
        }
        if (trimmed.StartsWith("SET TRANSACTION", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("SET TRANSACTION ISOLATION LEVEL must be executed outside any transaction.");
        }
        if (trimmed.StartsWith("RELEASE SAVEPOINT", StringComparison.OrdinalIgnoreCase))
        {
            var name = trimmed[17..].Trim();
            transaction.Release(name);
            return WalhallaResultSet.Affected(0);
        }

        // Check for SELECT � must handle read-your-own-writes
        // Use the existing Execute logic with transaction-aware path
        var fastResult = TryFastPathSelect(sql, transaction);
        if (fastResult != null)
            return fastResult;

        sql = CorrelatedSubqueryRewriter.Rewrite(sql);
        var statement = SqlStatementParser.Parse(sql);

        switch (statement)
        {
            case SqlSelectStatement select:
                return ExecuteSelect(select, transaction, planCacheKey: sql);

            case SqlInsertStatement insert:
                return ExecuteInsert(insert, transaction);

            case SqlInsertSelectStatement insertSelect:
                return ExecuteInsertSelect(insertSelect, transaction);

            case SqlMergeStatement merge:
                return ExecuteMerge(merge, transaction);

            case SqlUpdateStatement update:
                return ExecuteUpdate(update, transaction);

            case SqlDeleteStatement delete:
                return ExecuteDelete(delete, transaction);

            case SqlCreateTableStatement create:
                return ExecuteCreateTable(create);

            case SqlDropTableStatement drop:
                return ExecuteDropTable(drop);

            case SqlCreateIndexStatement createIndex:
                return ExecuteCreateIndex(createIndex);

            case SqlDropIndexStatement dropIndex:
                return ExecuteDropIndex(dropIndex);

            case SqlAlterTableStatement alter:
                return ExecuteAlterTable(alter);

            case SqlSavepointStatement savepoint:
                transaction.Savepoint(savepoint.Name);
                return WalhallaResultSet.Affected(0);

            case SqlRollbackToStatement rollbackTo:
                transaction.RollbackTo(rollbackTo.Name);
                return WalhallaResultSet.Affected(0);

            case SqlReleaseSavepointStatement release:
                transaction.Release(release.Name);
                return WalhallaResultSet.Affected(0);

            case SqlVacuumStatement vacuum:
                return ExecuteVacuum(vacuum);

            case SqlAnalyzeStatement analyze:
                return ExecuteAnalyze(analyze);

            default:
                throw new NotSupportedException(
                    $"Statement type '{statement.GetType().Name}' is not supported inside a transaction.");
        }
    }

    private IReadOnlyList<object?[]> ResolveSubquery(string sql)
    {
        var result = Execute(sql);
        var values = new object?[result.Rows.Count][];
        for (int i = 0; i < result.Rows.Count; i++)
            values[i] = result.Rows[i].Values.ToArray();
        return values;
    }

    // -- VACUUM ------------------------------------------------------------------

    private WalhallaResultSet ExecuteVacuum(SqlVacuumStatement vacuum)
    {
        if (vacuum.TableName != null)
        {
            var tableDef = _store.GetTableDefinition(vacuum.TableName);
            if (tableDef == null)
                throw new WalhallaException($"Table '{vacuum.TableName}' not found.");
        }

        int count = _store.Vacuum(vacuum.TableName);
        return WalhallaResultSet.Affected(count);
    }

    // -- ANALYZE -----------------------------------------------------------------

    private WalhallaResultSet ExecuteAnalyze(SqlAnalyzeStatement analyze)
    {
        IReadOnlyList<SqlTableDefinition> tables;
        if (analyze.TableName != null)
        {
            var tableDef = _store.GetTableDefinition(analyze.TableName);
            if (tableDef == null)
                throw new WalhallaException($"Table '{analyze.TableName}' not found.");
            tables = [tableDef];
        }
        else
        {
            tables = _store.GetAllTables();
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int analyzed = 0;
        foreach (var tableDef in tables)
        {
            int tableId = _store.GetTableId(tableDef.CollectionName);
            if (tableId < 0) continue;
            var stats = StatisticsBuilder.Build(tableId, _store, tableDef);
            _statisticsCatalog.Set(tableId, stats);
            _store.PersistStatistics(tableId, stats);
            analyzed++;
        }
        sw.Stop();

        Interlocked.Add(ref _analyzeTableCount, analyzed);
        Interlocked.Add(ref _analyzeDurationMs, sw.ElapsedMilliseconds);
        WalhallaDiagnostics.AnalyzeTables.Add(analyzed);
        WalhallaDiagnostics.AnalyzeDurationMs.Record(sw.ElapsedMilliseconds);

        return WalhallaResultSet.Affected(analyzed);
    }

    // -- Execute implementations ------------------------------------------------

    private WalhallaResultSet ExecuteExplain(SqlExplainStatement explain)
    {
        var innerSql = CorrelatedSubqueryRewriter.Rewrite(explain.SelectSql);
        var statement = SqlStatementParser.Parse(innerSql);
        if (statement is not SqlSelectStatement select)
            throw new NotSupportedException("EXPLAIN currently only supports SELECT statements.");

        var plan = QueryPlanner.Build(select, _store, ResolveSubquery, _statisticsCatalog);
        var rows = new List<WalhallaRow>();
        var columnNames = new[] { "Operation", "Target", "Details" };
        var schema = new ColumnSchema(columnNames);

        void Add(string op, string target, string details)
        {
            var row = new WalhallaRow(schema, new object?[] { op, target, details });
            rows.Add(row);
        }

        // C.7.5/C.7.6: Statistics-based row estimate. Uses ANALYZE stats when available;
        // falls back to the raw CountRows when there are no stats or no WHERE clause.
        int EstimateFilteredRows(int tableId, SqlWhereExpression? where)
        {
            int rawCount = _store.CountRows(tableId);
            if (where == null || !_statisticsCatalog.TryGet(tableId, out var tblStats))
                return rawCount;
            Func<string, ColumnStatistics?> colLookup = n => tblStats.Columns.GetValueOrDefault(n);
            return (int)Math.Max(1L, SelectivityEstimator.EstimateRows(rawCount, where, colLookup));
        }

        // Table access
        if (plan.Join != null)
        {
            var jp = plan.Join;
            // C.7.5: Use stats-based estimate for the base scan so join strategy decisions
            // (especially RIGHT-join leftCount) reflect post-filter cardinality.
            int estLeft = EstimateFilteredRows(jp.BaseTableId, select.Where);
            Add("SCAN", jp.BaseTableDef.CollectionName, $"Full table scan (base, ~{estLeft} rows)");

            foreach (var step in jp.Steps)
            {
                int rightCount = _store.CountRows(step.TableId);

                // Plan-time proxy for sort-merge eligibility: both join columns are primary keys, so
                // the base/right scans yield rows in key order. (Left side checked against the base
                // table definition; chained joins fall back to the size-based rule.)
                bool leftPk = step.LeftColumnIndices.Length > 0
                    && step.LeftColumnIndices.All(idx => idx < jp.BaseTableDef.Columns.Count && jp.BaseTableDef.Columns[idx].IsPrimaryKey);
                bool rightPk = step.RightColumnIndices.Length > 0
                    && step.RightColumnIndices.All(idx => idx < step.TableDef.Columns.Count && step.TableDef.Columns[idx].IsPrimaryKey);

                var strategy = JoinStrategyEstimator.Estimate(step.Kind, estLeft, rightCount, leftPk && rightPk);
                var label = JoinStepExecutor.StrategyLabel(strategy, step.Kind);
                var keyDesc = step.Kind == SqlJoinKind.Cross
                    ? ""
                    : string.Join(" AND ", step.LeftColumnNames.Zip(step.RightColumnNames, (l, r) => $"{l} = {r}")) + "; ";
                var onDetail = step.Kind == SqlJoinKind.Cross
                    ? $"strategy={JoinStepExecutor.StrategyTraceName(strategy)} (~{estLeft}x{rightCount})"
                    : $"{keyDesc}strategy={JoinStepExecutor.StrategyTraceName(strategy)} (~{estLeft}x{rightCount})";
                Add(label, step.TableDef.CollectionName, onDetail);

                // Carry a coarse cardinality estimate forward for multi-step joins.
                estLeft = step.Kind == SqlJoinKind.Cross
                    ? estLeft * rightCount
                    : Math.Max(estLeft, rightCount);
            }
        }
        else if (plan.PkLookupColumnIndex.HasValue)
        {
            var pkCol = plan.TableDefinition.Columns[plan.PkLookupColumnIndex.Value];
            var detail = plan.PkLookupConstant != null
                ? $"WHERE {pkCol.Name} = {plan.PkLookupConstant}"
                : $"WHERE {pkCol.Name} = @param";
            Add("PK_LOOKUP", plan.TableDefinition.CollectionName, detail + " (est_rows=~1)");
        }
        else if (plan.PkRange != null)
        {
            var range = plan.PkRange;
            var pkCol = plan.TableDefinition.Columns[range.ColumnIndex];
            var loSym = range.MinInclusive ? ">=" : ">";
            var hiSym = range.MaxInclusive ? "<=" : "<";
            var lo = range.MinParameterIndex >= 0 ? "@param" : range.LiteralMin.ToString();
            var hi = range.MaxParameterIndex >= 0 ? "@param" : range.LiteralMax.ToString();
            Add("PK_RANGE", plan.TableDefinition.CollectionName,
                $"WHERE {pkCol.Name} {loSym} {lo} AND {pkCol.Name} {hiSym} {hi}");
        }
        else if (plan.SelectedIndex != null)
        {
            var idx = plan.SelectedIndex;
            int estRows = EstimateFilteredRows(plan.TableId, select.Where);
            Add("INDEX_SCAN", plan.TableDefinition.CollectionName,
                $"Using index '{idx.Index.IndexName}' with {idx.MatchedPredicates.Count} sargable predicate(s) (est_rows=~{estRows})");
        }
        else
        {
            int estRows = EstimateFilteredRows(plan.TableId, select.Where);
            Add("SCAN", plan.TableDefinition.CollectionName, $"Full table scan (est_rows=~{estRows})");
        }

        // Filter
        if (plan.WhereDelegate != null && plan.PkLookupColumnIndex == null)
            Add("FILTER", "-", select.Where?.ToString() ?? "<compiled>");

        // GROUP BY / Aggregates
        if (plan.GroupByColumns is { Count: > 0 })
            Add("GROUP_BY", string.Join(", ", plan.GroupByColumns), "Aggregate query");

        // Order
        if (plan.OrderByColumns is { Count: > 0 })
        {
            var orderDetails = string.Join(", ",
                plan.OrderByColumns.Select(o => o.ColumnName + (o.Descending ? " DESC" : " ASC")));
            Add("SORT", "-", orderDetails);
        }

        // Projection
        Add("PROJECT", "-", string.Join(", ", plan.OutputColumnNames));

        // Paging
        if (plan.Limit.HasValue || plan.Offset.HasValue)
        {
            var pageDetail = "";
            if (plan.Offset.HasValue) pageDetail += $"OFFSET {plan.Offset.Value}";
            if (plan.Limit.HasValue)
                pageDetail += (pageDetail.Length > 0 ? " " : "") + $"LIMIT {plan.Limit.Value}";
            Add("PAGE", "-", pageDetail);
        }

        if (plan.IsDistinct)
            Add("DISTINCT", "-", "Deduplication on output columns");

        return new WalhallaResultSet(rows, columnNames);
    }

    private WalhallaResultSet ExecuteSelect(SqlSelectStatement select, string? planCacheKey = null,
        ITransaction<byte[], byte[]>? storageTx = null)
    {
        // Resolve views: redirect to the underlying SELECT
        SqlCreateViewStatement? viewDef;
        lock (_metaSync)
            _views.TryGetValue(select.TableName, out viewDef);
        if (viewDef != null)
        {
            var resolved = viewDef.SelectStatement;
            // Apply the caller's WHERE and other clauses on top of the view
            if (select.Where != null || select.OrderBy is { Count: > 0 } || select.Limit.HasValue)
            {
                // Merge: use view's SELECT as derived table, outer query applies additional clauses
                var outerSelect = new SqlSelectStatement(
                    select.TableName, select.TableAlias, select.Columns, select.Where,
                    select.Parameters, select.Joins, select.GroupByColumns, select.Having,
                    select.OrderBy, select.Limit, select.Offset, select.IsDistinct,
                    DerivedTable: resolved);
                return ExecuteSelect(outerSelect);
            }
            return ExecuteSelect(resolved);
        }

        // Materialize derived tables if present (base table or JOINs)
        var joinsWithDerived = select.Joins?.Where(j => j.DerivedTable != null).ToList();
        if (select.DerivedTable != null || (joinsWithDerived?.Count > 0))
        {
            var tempTables = new List<string>();

            // Materialize base derived table
            string? outerTableName = null;
            if (select.DerivedTable != null)
            {
                var innerResult = ExecuteSelect(select.DerivedTable);

                var columnDefs = new List<SqlColumnDefinition>();
                foreach (var name in innerResult.ColumnNames)
                    columnDefs.Add(new SqlColumnDefinition(name, SqlScalarType.String));

                var tempId = Interlocked.Increment(ref _tempTableCounter);
                var tempTableName = $"__dt_{select.TableName}_{tempId}";
                outerTableName = tempTableName;

                var tempTableDef = new SqlTableDefinition(
                    tempTableName, columnDefs,
                    new List<SqlIndexDefinition>(), new List<SqlForeignKeyDefinition>(),
                    new List<SqlProjectionDefinition>());

                _store.CreateTable(tempTableDef);

                var rows = innerResult.Rows
                    .Select(r => r.Values.ToArray())
                    .ToList() as IReadOnlyList<object?[]>;
                if (rows.Count > 0)
                    InsertBatch(tempTableName, rows);

                tempTables.Add(tempTableName);
            }

            // Materialize join derived tables
            var newJoins = new List<SqlJoinClause>();
            var joinTableNameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var join in select.Joins ?? Array.Empty<SqlJoinClause>())
            {
                if (join.DerivedTable != null)
                {
                    var innerResult = ExecuteSelect(join.DerivedTable);

                    var columnDefs = new List<SqlColumnDefinition>();
                    foreach (var name in innerResult.ColumnNames)
                        columnDefs.Add(new SqlColumnDefinition(name, SqlScalarType.String));

                    var tempId = Interlocked.Increment(ref _tempTableCounter);
                    var tempTableName = $"__dt_{join.TableName}_{tempId}";
                    joinTableNameMap[join.TableName] = tempTableName;

                    var tempTableDef = new SqlTableDefinition(
                        tempTableName, columnDefs,
                        new List<SqlIndexDefinition>(), new List<SqlForeignKeyDefinition>(),
                        new List<SqlProjectionDefinition>());

                    _store.CreateTable(tempTableDef);

                    var rows = innerResult.Rows
                        .Select(r => r.Values.ToArray())
                        .ToList() as IReadOnlyList<object?[]>;
                    if (rows.Count > 0)
                        InsertBatch(tempTableName, rows);

                    tempTables.Add(tempTableName);
                    newJoins.Add(new SqlJoinClause(join.Kind, tempTableName, join.Alias, join.OnPredicate));
                }
                else
                {
                    newJoins.Add(join);
                }
            }

            var outerSelect = new SqlSelectStatement(
                outerTableName ?? select.TableName, select.TableAlias, select.Columns, select.Where,
                select.Parameters, newJoins, select.GroupByColumns, select.Having,
                select.OrderBy, select.Limit, select.Offset, select.IsDistinct);

            try
            {
                return ExecuteSelect(outerSelect);
            }
            finally
            {
                foreach (var name in tempTables)
                    _store.DropTable(name);
            }
        }

        // Plan cache lookup (skip for recursive/synthetic calls and within transactions)
        CompiledPlan plan;
        if (planCacheKey != null && _planCache != null && _planCache.TryGet(planCacheKey, out var cachedPlan))
        {
            Interlocked.Increment(ref _planCacheHits);
            plan = cachedPlan;
        }
        else
        {
            if (planCacheKey != null && _planCache != null)
                Interlocked.Increment(ref _planCacheMisses);
            plan = QueryPlanner.Build(select, _store, ResolveSubquery, _statisticsCatalog);
            if (planCacheKey != null && _planCache != null)
                _planCache.Set(planCacheKey, plan);
        }

        // JOIN execution path
        if (plan.Join != null)
            return ExecuteJoinSelect(plan, select);

        // GROUP BY / aggregate execution path
        bool hasAggregates = select.Columns.Any(c => c.Aggregate != null && c.WindowFunction == null);
        if (select.GroupByColumns is { Count: > 0 } || hasAggregates)
            return ExecuteAggregateSelect(plan, select);

        // PK point-lookup fast path: WHERE PK = literal
        if (plan.PkLookupConstant != null && plan.PkLookupColumnIndex.HasValue)
        {
            var pkValue = Convert.ToInt64(plan.PkLookupConstant);
            var encoded = storageTx != null
                ? _store.GetRow(plan.TableId, pkValue, storageTx)
                : _store.GetRow(plan.TableId, pkValue);
            if (encoded == null)
                return new WalhallaResultSet(Array.Empty<WalhallaRow>(), plan.OutputColumnNames);

            // B.2: Decode only projected columns � skip full row decode + ProjectRow
            object?[] pkProjected;
            if (plan.ComputedProjections != null)
            {
                var fullRow = DecodeRowWithBlobs(plan.TableId, encoded, plan.TableDefinition);
                pkProjected = ProjectRow(fullRow, plan);
            }
            else if (plan.IsFullProjection)
            {
                pkProjected = DecodeRowWithBlobs(plan.TableId, encoded, plan.TableDefinition);
            }
            else
            {
                pkProjected = DecodeColumnsWithBlobs(plan.TableId, encoded.AsSpan(), plan.TableDefinition, plan.ProjectionIndices);
            }
            var pkSchema = new ColumnSchema(plan.OutputColumnNames);
            var pkRow = new WalhallaRow(pkSchema, pkProjected);
            return new WalhallaResultSet(new[] { pkRow }, plan.OutputColumnNames);
        }

        // PK range scan path: WHERE PK BETWEEN literal AND literal (direct Execute).
        if (plan.PkRange != null)
        {
            var range = plan.PkRange;
            long minRowId = range.HasLiteralBounds ? range.LiteralMin : long.MinValue;
            long maxRowId = range.HasLiteralBounds ? range.LiteralMax : long.MaxValue;

            if (!range.MinInclusive) minRowId++;
            if (!range.MaxInclusive) maxRowId--;

            var fullResults = new List<object?[]>();
            var whereDelegate = plan.WhereDelegate;
            var emptyParams = Array.Empty<object?>();

            _store.ScanRowKeyRange(plan.TableId, minRowId, maxRowId,
                encoded => DecodeRowPooledWithBlobs(plan.TableId, encoded, plan.TableDefinition),
                whereDelegate != null ? row => whereDelegate(row, emptyParams) : null, fullResults);

            return ApplyPostProcessing(fullResults, plan, select);
        }

        // GIN index path: inverted index lookup for JSONB operators.
        if (plan.SelectedIndex != null && plan.SelectedIndex.Index.IndexType == SqlIndexType.Gin)
        {
            var sel = plan.SelectedIndex;
            var ginPred = sel.MatchedPredicates.FirstOrDefault(
                p => p.GinOperator != GinPredicateType.None);

            if (ginPred != null)
            {
                HashSet<long> candidateRowIds;
                switch (ginPred.GinOperator)
                {
                    case GinPredicateType.Contains:
                        candidateRowIds = GinIndexLookup.LookupContains(
                            sel.IndexId, plan.TableId,
                            ginPred.GinQueryJson ?? "{}", _store);
                        break;
                    case GinPredicateType.KeyExists:
                        candidateRowIds = GinIndexLookup.LookupKeyExists(
                            sel.IndexId, plan.TableId,
                            ginPred.GinQueryJson ?? "", _store);
                        break;
                    case GinPredicateType.AnyKey:
                        var anyKeys = ParseKeyList(ginPred.GinQueryJson ?? "[]");
                        candidateRowIds = GinIndexLookup.LookupAnyKey(
                            sel.IndexId, plan.TableId, anyKeys, _store);
                        break;
                    case GinPredicateType.AllKeys:
                        var allKeys = ParseKeyList(ginPred.GinQueryJson ?? "[]");
                        candidateRowIds = GinIndexLookup.LookupAllKeys(
                            sel.IndexId, plan.TableId, allKeys, _store);
                        break;
                    default:
                        candidateRowIds = new HashSet<long>();
                        break;
                }

                var fullRows = new List<object?[]>();
                var whereDelegate = plan.WhereDelegate;
                var emptyParams = Array.Empty<object?>();

                foreach (var rowId in candidateRowIds)
                {
                    var encoded = storageTx != null
                        ? _store.GetRow(plan.TableId, rowId, storageTx)
                        : _store.GetRow(plan.TableId, rowId);
                    if (encoded == null) continue;

                    var fullRow = DecodeRowPooledWithBlobs(plan.TableId, encoded, plan.TableDefinition);

                    if (whereDelegate == null || whereDelegate(fullRow, emptyParams))
                        fullRows.Add(fullRow);
                }

                return ApplyPostProcessing(fullRows, plan, select);
            }
        }

        // Index scan path: secondary index range scan ? table lookup.
        if (plan.SelectedIndex != null)
        {
            var sel = plan.SelectedIndex;
            var sargablePredicates = sel.MatchedPredicates;

            var (startKey, endKey, startInclusive, endInclusive) =
                IndexKeyCodec.BuildRangeBounds(sargablePredicates, sel.IndexKeyTypes, null);

            var indexKeys = _store.ScanIndex(
                sel.IndexId, startKey, endKey, startInclusive, endInclusive);

            var fullRows = new List<object?[]>();
            var whereDelegate = plan.WhereDelegate;
            var emptyParams = Array.Empty<object?>();

            foreach (var (tid, rowId) in indexKeys)
            {
                var encoded = storageTx != null
                    ? _store.GetRow(tid, rowId, storageTx)
                    : _store.GetRow(tid, rowId);
                if (encoded == null) continue;

                var fullRow = DecodeRowPooledWithBlobs(tid, encoded, plan.TableDefinition);

                if (whereDelegate == null || whereDelegate(fullRow, emptyParams))
                    fullRows.Add(fullRow);
            }

            return ApplyPostProcessing(fullRows, plan, select);
        }

        var results = new List<object?[]>();

        RowDecoder decoder = (ReadOnlySpan<byte> encoded) =>
            DecodeRowPooledWithBlobs(plan.TableId, encoded, plan.TableDefinition);
        // For parameterless selects, wrap the delegate
        Func<object?[], bool>? predicate = null;
        if (plan.WhereDelegate != null && plan.ParameterCount == 0)
        {
            var where = plan.WhereDelegate;
            var emptyParams = Array.Empty<object?>();
            predicate = row => where(row, emptyParams);
        }
        else if (plan.WhereDelegate != null)
        {
            // This path shouldn't happen for regular Execute() (no params)
            throw new WalhallaException("SELECT with parameters requires a prepared statement. Use Prepare().");
        }

        _store.ScanWithPredicateFirst(plan.TableId, plan.TableDefinition,
            plan.PredicateColumnIndices ?? Array.Empty<int>(),
            decoder, predicate, results, int.MaxValue);

        return ApplyPostProcessing(results, plan, select);
    }

    private WalhallaResultSet ApplyPostProcessing(List<object?[]> fullRows, CompiledPlan plan, SqlSelectStatement select)
    {
        // Compute window function values (keyed by output column index).
        // Each value array is indexed by the row's ORIGINAL (pre-sort) position.
        var windowResults = WindowFunctionEvaluator.Compute(fullRows, select.Columns, plan.TableDefinition);
        bool hasWindow = windowResults.Count > 0;

        // Apply ORDER BY on full rows before projection.
        // When window functions are present, the precomputed value arrays are indexed by the
        // original position, so we must remember where each row came from after the sort and
        // look the window values up by that original index � otherwise the value?row mapping
        // breaks whenever ORDER BY reorders the rows.
        int[]? origIndex = null;
        if (select.OrderBy != null && select.OrderBy.Count > 0)
        {
            var colIndices = new int[plan.TableDefinition.Columns.Count];
            for (int i = 0; i < colIndices.Length; i++) colIndices[i] = i;

            if (hasWindow)
            {
                var n = fullRows.Count;
                var order = new int[n];
                for (int i = 0; i < n; i++) order[i] = i;
                var comparer = OrderByExecutor.CreateRowComparer(select.OrderBy, colIndices, plan.TableDefinition);
                // Stable sort (tie-break on original index) keeps output deterministic.
                Array.Sort(order, (a, b) =>
                {
                    var c = comparer.Compare(fullRows[a], fullRows[b]);
                    return c != 0 ? c : a.CompareTo(b);
                });
                var sorted = new List<object?[]>(n);
                origIndex = new int[n];
                for (int i = 0; i < n; i++)
                {
                    sorted.Add(fullRows[order[i]]);
                    origIndex[i] = order[i];
                }
                fullRows = sorted;
            }
            else
            {
                OrderByExecutor.SortInPlace(fullRows, select.OrderBy, colIndices, plan.TableDefinition);
            }
        }

        // Project rows, injecting window function and computed expression values.
        var projected = new List<object?[]>();
        var compProj = plan.ComputedProjections;
        for (int ri = 0; ri < fullRows.Count; ri++)
        {
            var row = fullRows[ri];
            var srcIdx = origIndex != null ? origIndex[ri] : ri;
            var proj = new object?[plan.OutputColumnNames.Length];
            for (int ci = 0; ci < plan.ProjectionIndices.Length; ci++)
            {
                // Check if this column is a window function
                if (windowResults.TryGetValue(ci, out var wfValues))
                    proj[ci] = wfValues[srcIdx];
                else if (compProj != null && compProj[ci] != null)
                    proj[ci] = compProj[ci]!(row);
                else
                {
                    var colIdx = plan.ProjectionIndices[ci];
                    proj[ci] = colIdx < row.Length ? row[colIdx] : null;
                }
            }
            projected.Add(proj);
        }

        // Apply DISTINCT after projection.
        if (select.IsDistinct)
        {
            var seen = new HashSet<RowKey>(new RowKeyComparer());
            projected = projected.Where(r => seen.Add(new RowKey(r))).ToList();
        }

        // Apply paging (LIMIT/OFFSET).
        if (select.Offset.HasValue)
        {
            projected = projected.Skip(select.Offset.Value).ToList();
        }
        if (select.Limit.HasValue)
        {
            projected = projected.Take(select.Limit.Value).ToList();
        }

        var schema = new ColumnSchema(plan.OutputColumnNames);
        var rows = projected.ConvertAll(r => new WalhallaRow(schema, r));

        // Return rented arrays to pool after projection.
        foreach (var row in fullRows)
            RowCodec.ReturnPooledArray(row);

        return new WalhallaResultSet(rows, plan.OutputColumnNames);
    }

    private WalhallaResultSet ExecuteJoinSelect(CompiledPlan plan, SqlSelectStatement select)
    {
        var joinPlan = plan.Join!;

        // Read base table rows.
        var baseRows = new List<object?[]>();
        RowDecoder baseDecoder = encoded => DecodeRowWithBlobs(joinPlan.BaseTableId, encoded, joinPlan.BaseTableDef);
        _store.ScanWithPredicate(joinPlan.BaseTableId, baseDecoder, null, baseRows, int.MaxValue);

        // Apply base-table WHERE if present.
        var where = plan.WhereDelegate;
        var emptyParams = Array.Empty<object?>();
        if (where != null)
            baseRows.RemoveAll(r => !where(r, emptyParams));

        // Accumulate results through joins.
        var accumulated = baseRows;

        foreach (var step in joinPlan.Steps)
        {
            // Read right (join) table rows.
            var rightRows = new List<object?[]>();
            RowDecoder rightDecoder = encoded => DecodeRowWithBlobs(step.TableId, encoded, step.TableDef);
            _store.ScanWithPredicate(step.TableId, rightDecoder, null, rightRows, int.MaxValue);

            accumulated = JoinStepExecutor.ExecuteStep(accumulated, rightRows, step, emptyParams);
        }

        // Apply ORDER BY on combined rows (match output column names to projection indices).
        if (select.OrderBy != null && select.OrderBy.Count > 0)
        {
            SortJoinRows(accumulated, select.OrderBy, joinPlan.ProjectionIndices, plan.OutputColumnNames);
        }

        // Build final projection.
        var projectedRows = new List<object?[]>();
        foreach (var row in accumulated)
        {
            var projected = new object?[plan.OutputColumnNames.Length];
            for (int i = 0; i < plan.OutputColumnNames.Length; i++)
            {
                var colIdx = joinPlan.ProjectionIndices[i];
                if (colIdx >= 0)
                {
                    projected[i] = colIdx < row.Length ? row[colIdx] : null;
                }
                else if (plan.ComputedProjections != null && i < plan.ComputedProjections.Length && plan.ComputedProjections[i] != null)
                {
                    projected[i] = plan.ComputedProjections[i]!(row);
                }
                else
                {
                    projected[i] = null;
                }
            }
            projectedRows.Add(projected);
        }

        // Apply DISTINCT after projection.
        if (select.IsDistinct)
        {
            var seen = new HashSet<RowKey>(new RowKeyComparer());
            projectedRows = projectedRows.Where(r => seen.Add(new RowKey(r))).ToList();
        }

        // Apply paging.
        if (select.Offset.HasValue)
            projectedRows = projectedRows.Skip(select.Offset.Value).ToList();
        if (select.Limit.HasValue)
            projectedRows = projectedRows.Take(select.Limit.Value).ToList();

        var schema = new ColumnSchema(plan.OutputColumnNames);
        var rows = projectedRows.ConvertAll(r => new WalhallaRow(schema, r));
        return new WalhallaResultSet(rows, plan.OutputColumnNames);
    }

    private static void SortJoinRows(
        List<object?[]> rows,
        IReadOnlyList<SqlOrderByColumn> orderBy,
        int[] projectionIndices,
        string[] outputNames)
    {
        if (rows.Count <= 1) return;

        // Build mapping: ORDER BY column name ? combined row index
        var colMap = new (int Index, bool Descending)[orderBy.Count];
        for (int i = 0; i < orderBy.Count; i++)
        {
            var colIdx = -1;
            for (int j = 0; j < outputNames.Length; j++)
            {
                if (string.Equals(outputNames[j], orderBy[i].ColumnName, StringComparison.OrdinalIgnoreCase))
                {
                    colIdx = projectionIndices[j];
                    break;
                }
            }
            colMap[i] = (colIdx, orderBy[i].Descending);
        }

        rows.Sort((a, b) =>
        {
            for (int i = 0; i < colMap.Length; i++)
            {
                var (colIdx, descending) = colMap[i];
                if (colIdx < 0) continue;
                var av = colIdx < a.Length ? a[colIdx] : null;
                var bv = colIdx < b.Length ? b[colIdx] : null;
                var cmp = CompareOrderValues(av, bv);
                if (cmp != 0) return descending ? -cmp : cmp;
            }
            return 0;
        });
    }

    private static int CompareOrderValues(object? left, object? right)
    {
        if (left == null && right == null) return 0;
        if (left == null) return -1;
        if (right == null) return 1;

        if (left is long l && right is long r) return l.CompareTo(r);
        if (left is int il && right is int ir) return il.CompareTo(ir);
        if (left is string ls && right is string rs) return CollationManager.Compare(ls, rs, null);
        if (left is double ld && right is double rd) return ld.CompareTo(rd);

        if (left is IComparable lc && right.GetType() == left.GetType())
            return lc.CompareTo(right);

        var sLeft = Convert.ToString(left) ?? string.Empty;
        var sRight = Convert.ToString(right) ?? string.Empty;
        return CollationManager.Compare(sLeft, sRight, null);
    }

    private WalhallaResultSet ExecuteAggregateSelect(CompiledPlan plan, SqlSelectStatement select)
    {
        // Scan all rows with WHERE filter.
        var rows = new List<object?[]>();
        RowDecoder decoder = encoded => DecodeRowWithBlobs(plan.TableId, encoded, plan.TableDefinition);

        Func<object?[], bool>? predicate = null;
        if (plan.WhereDelegate != null && plan.ParameterCount == 0)
        {
            var where = plan.WhereDelegate;
            var emptyParams = Array.Empty<object?>();
            predicate = row => where(row, emptyParams);
        }

        _store.ScanWithPredicateFirst(plan.TableId, plan.TableDefinition,
            plan.PredicateColumnIndices ?? Array.Empty<int>(),
            decoder, predicate, rows, int.MaxValue);

        // Execute GROUP BY with aggregates.
        var aggregated = AggregateExecutor.ExecuteGroupBy(rows, select.GroupByColumns, select.Columns, plan.TableDefinition, plan.OutputColumnNames);

        // Apply HAVING filter.
        aggregated = AggregateExecutor.ApplyHaving(aggregated, select.Having, plan.OutputColumnNames);

        // Build result set.
        var schema = new ColumnSchema(plan.OutputColumnNames);
        var resultRows = aggregated.ConvertAll(r => new WalhallaRow(schema, r));
        return new WalhallaResultSet(resultRows, plan.OutputColumnNames);
    }

    private sealed class RowKey
    {
        internal readonly object?[] _values;
        public RowKey(object?[] values) => _values = values;
    }

    private sealed class RowKeyComparer : IEqualityComparer<RowKey>
    {
        public bool Equals(RowKey? x, RowKey? y)
        {
            if (x == null || y == null) return false;
            var a = x._values;
            var b = y._values;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (!EqualsValue(a[i], b[i])) return false;
            }
            return true;
        }

        public int GetHashCode(RowKey obj)
        {
            var hash = new HashCode();
            foreach (var v in obj._values) hash.Add(v);
            return hash.ToHashCode();
        }

        private static bool EqualsValue(object? x, object? y)
        {
            if (x == null && y == null) return true;
            if (x == null || y == null) return false;
            if (x is string sx && y is string sy)
                return CollationManager.Equals(sx, sy, null);
            return x.Equals(y);
        }
    }

    // -- Helpers (continued) ------------------------------------------------------


    // B.5: Fast path for SELECT <cols> FROM <table> WHERE <pkCol> = <literal>
    // bypasses parser + planner entirely for the most common hot-path query shape.
    private WalhallaResultSet? TryFastPathSelect(string sql)
    {
        return TryFastPathSelectCore(sql, null);
    }

    private WalhallaResultSet? TryFastPathSelect(string sql, WalhallaSqlTransaction? transaction)
    {
        return TryFastPathSelectCore(sql, transaction);
    }

    private WalhallaResultSet? TryFastPathSelectCore(string sql, WalhallaSqlTransaction? transaction)
    {
        var span = sql.AsSpan().TrimStart();
        if (span.Length < 20) return null;

        // Must start with "SELECT "
        if (!SliceEq(span, 0, "SELECT ")) return null;
        span = span.Slice(7); // skip "SELECT "

        // Find " FROM " (case-insensitive, whitespace-normalized)
        var fromIdx = IndexOfKeyword(span, " FROM ");
        if (fromIdx < 0) return null;
        var colsSpan = span.Slice(0, fromIdx).Trim();
        span = span.Slice(fromIdx + 6); // skip " FROM "

        // Find " WHERE " � reject any extra clauses before WHERE
        var whereIdx = IndexOfKeyword(span, " WHERE ");
        if (whereIdx < 0) return null;
        var tableSpan = span.Slice(0, whereIdx).Trim();
        span = span.Slice(whereIdx + 7); // skip " WHERE "

        // Parse "<col> = <literal>" � reject multi-condition WHERE
        var eqIdx = span.IndexOf('=');
        if (eqIdx < 0) return null;
        var colSpan = span.Slice(0, eqIdx).Trim();
        var valSpan = span.Slice(eqIdx + 1).Trim();

        // Reject anything that would need the full parser
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] == '\'') return null; // string literals need quoting
        }
        if (span.IndexOfAny('(', ')', ';') >= 0) return null;
        if (KeywordContains(span, " AND ") || KeywordContains(span, " OR ")
            || KeywordContains(span, " ORDER ") || KeywordContains(span, " GROUP ")
            || KeywordContains(span, " LIMIT ") || KeywordContains(span, " JOIN "))
            return null;

        var tableName = tableSpan.ToString();
        var tableDef = _store.GetTableDefinition(tableName);
        if (tableDef == null) return null;

        // Verify WHERE column is the PK
        var pkCols = tableDef.PrimaryKeyColumns;
        if (pkCols.Count != 1) return null;
        if (!colSpan.Equals(pkCols[0].Name.AsSpan(), StringComparison.OrdinalIgnoreCase))
            return null;

        // Parse literal value
        if (!long.TryParse(valSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pkValue))
            return null;

        var tableId = _store.GetTableId(tableName);
        if (tableId < 0) return null;

        // Check transaction buffer first (read-your-own-writes)
        if (transaction != null && transaction.TryGetBufferedRow(tableId, pkValue, out var buffered))
        {
            if (buffered == null)
                return new WalhallaResultSet(Array.Empty<WalhallaRow>(), Array.Empty<string>());

            return BuildFastPathResult(tableId, buffered, tableDef, colsSpan);
        }

        var encoded = _store.GetRow(tableId, pkValue);
        if (encoded == null)
            return new WalhallaResultSet(Array.Empty<WalhallaRow>(), Array.Empty<string>());

        return BuildFastPathResult(tableId, encoded, tableDef, colsSpan);
    }

    private WalhallaResultSet BuildFastPathResult(
        int tableId, byte[] encoded, SqlTableDefinition tableDef, ReadOnlySpan<char> colsSpan)
    {
        var isStar = colsSpan.Length == 1 && colsSpan[0] == '*';
        string[] outputNames;
        object?[] projected;

        if (isStar)
        {
            projected = DecodeRowWithBlobs(tableId, encoded, tableDef);
            outputNames = new string[tableDef.Columns.Count];
            for (int i = 0; i < tableDef.Columns.Count; i++)
                outputNames[i] = tableDef.Columns[i].Name;
        }
        else
        {
            var colNames = ParseSimpleColumnList(colsSpan);
            if (colNames == null)
            {
                // Fallback: return all columns
                projected = DecodeRowWithBlobs(tableId, encoded, tableDef);
                outputNames = new string[tableDef.Columns.Count];
                for (int i = 0; i < tableDef.Columns.Count; i++)
                    outputNames[i] = tableDef.Columns[i].Name;
            }
            else
            {
                var indices = new int[colNames.Length];
                for (int i = 0; i < colNames.Length; i++)
                {
                    var idx = FindColumnIndex(tableDef, colNames[i]);
                    if (idx < 0)
                    {
                        projected = DecodeRowWithBlobs(tableId, encoded, tableDef);
                        outputNames = new string[tableDef.Columns.Count];
                        for (int j = 0; j < tableDef.Columns.Count; j++)
                            outputNames[j] = tableDef.Columns[j].Name;
                        goto buildResult;
                    }
                    indices[i] = idx;
                }

                var fullIndices = Enumerable.Range(0, tableDef.Columns.Count).ToArray();
                var isFull = indices.Length == fullIndices.Length
                    && indices.SequenceEqual(fullIndices);

                projected = isFull
                    ? DecodeRowWithBlobs(tableId, encoded, tableDef)
                    : DecodeColumnsWithBlobs(tableId, encoded.AsSpan(), tableDef, indices);
                outputNames = colNames;
            }
        }

        buildResult:
        var schema = new ColumnSchema(outputNames);
        var row = new WalhallaRow(schema, projected);
        return new WalhallaResultSet(new[] { row }, outputNames);
    }

    // -- Fast-path string helpers ----------------------------------------------

    private static bool SliceEq(ReadOnlySpan<char> s, int start, string literal)
    {
        if (start + literal.Length > s.Length) return false;
        return s.Slice(start, literal.Length).Equals(literal.AsSpan(), StringComparison.OrdinalIgnoreCase);
    }

    private static int IndexOfKeyword(ReadOnlySpan<char> s, string keyword)
    {
        // Case-insensitive search for a keyword surrounded by whitespace or at boundaries.
        // Keywords start with a space, e.g. " FROM ". We trim the leading space for boundary.
        var search = keyword.TrimStart();
        var len = search.Length;
        for (int i = 0; i <= s.Length - len; i++)
        {
            // Leading boundary: must be at start or preceded by whitespace
            if (i > 0 && s[i - 1] != ' ' && s[i - 1] != '\t' && s[i - 1] != '\r' && s[i - 1] != '\n')
                continue;

            if (!s.Slice(i, len).Equals(search.AsSpan(), StringComparison.OrdinalIgnoreCase))
                continue;

            // Trailing boundary: must be at end or followed by whitespace
            var end = i + len;
            if (end < s.Length && s[end] != ' ' && s[end] != '\t' && s[end] != '\r' && s[end] != '\n')
                continue;

            return i;
        }
        return -1;
    }

    private static bool KeywordContains(ReadOnlySpan<char> s, string keyword)
    {
        return IndexOfKeyword(s, keyword) >= 0;
    }

    private static string[]? ParseSimpleColumnList(ReadOnlySpan<char> s)
    {
        // Only handles "col1, col2, ..." � no expressions, no aliases, no quotes
        var names = new List<string>();
        var start = 0;
        for (int i = 0; i <= s.Length; i++)
        {
            if (i == s.Length || s[i] == ',')
            {
                var name = s.Slice(start, i - start).Trim();
                if (name.IsEmpty) return null;
                for (int j = 0; j < name.Length; j++)
                {
                    if (!char.IsLetterOrDigit(name[j]) && name[j] != '_')
                        return null;
                }
                names.Add(name.ToString());
                start = i + 1;
            }
        }
        return names.ToArray();
    }

    private static object?[] ProjectRow(object?[] row, CompiledPlan plan)
    {
        if (plan.IsFullProjection && plan.ComputedProjections == null) return row;
        var result = new object?[plan.ProjectionIndices.Length];
        var comp = plan.ComputedProjections;
        for (int i = 0; i < plan.ProjectionIndices.Length; i++)
        {
            if (comp != null && comp[i] != null)
                result[i] = comp[i]!(row);
            else
                result[i] = row[plan.ProjectionIndices[i]];
        }
        return result;
    }

    private WalhallaResultSet ExecuteInsert(SqlInsertStatement insert)
    {
        return ExecuteInsert(insert, null);
    }

    private WalhallaResultSet ExecuteInsert(SqlInsertStatement insert, WalhallaSqlTransaction? transaction)
    {
        if (transaction != null)
            return ExecuteInsertBuffered(insert, transaction);

        var tableDef = _store.GetTableDefinition(insert.TableName)
            ?? throw new WalhallaException($"Table '{insert.TableName}' not found.");

        var tableId = _store.GetTableId(insert.TableName);

        // Build column index map: INSERT column name ? table column index
        var colIndexMap = new Dictionary<int, int>();
        for (int i = 0; i < insert.Columns.Count; i++)
        {
            var idx = FindColumnIndex(tableDef, insert.Columns[i]);
            if (idx < 0)
                throw new WalhallaException($"Column '{insert.Columns[i]}' not found in table '{insert.TableName}'.");
            colIndexMap[i] = idx;
        }

        // Build index metadata once.
        var indexMetas = GetOrBuildIndexMetadata(insert.TableName, tableDef);

        // Fire BEFORE triggers
        FireTriggers(insert.TableName, SqlTriggerEvent.Insert, SqlTriggerTiming.Before);

        // Convert string value rows to typed object rows, then encode.
        var decodedRows = new List<object?[]>(insert.ValueRows.Count);
        var encodedRows = new List<byte[]>(insert.ValueRows.Count);
        foreach (var valueRow in insert.ValueRows)
        {
            var rowValues = new object?[tableDef.Columns.Count];
            for (int i = 0; i < insert.Columns.Count; i++)
            {
                var type = tableDef.Columns[colIndexMap[i]].Type;
                rowValues[colIndexMap[i]] = ParseLiteral(valueRow[i], type);
            }
            decodedRows.Add(rowValues);
            encodedRows.Add(EncodeRowWithBlobs(tableId, rowValues, tableDef));
        }

        // Enforce foreign key and CHECK constraints on all inserted rows
        foreach (var row in decodedRows)
        {
            EnforceForeignKeyInsert(tableDef, row, decodedRows);
            CheckConstraintEvaluator.Enforce(tableDef, row);
        }

        // ON CONFLICT path: row-by-row processing
        if (insert.OnConflict != null)
            return ExecuteInsertRowByRow(insert.TableName, tableDef, tableId, indexMetas, decodedRows, insert.OnConflict);

        if (encodedRows.Count == 1)
        {
            var rowId = ResolvePkAliasRowId(tableDef, decodedRows[0], tableId);
            var singleIndexEntries = new List<(int, byte[], int, long)>();
            foreach (var meta in indexMetas)
            {
                if (meta.Definition.IndexType == SqlIndexType.Gin)
                {
                    var jsonbColIdx = meta.ColumnIndices[0];
                    var jsonbValue = decodedRows[0][jsonbColIdx];
                    if (jsonbValue != null && jsonbValue != DBNull.Value)
                    {
                        var elements = GinElementExtractor.ExtractElements(jsonbValue);
                        foreach (var element in elements)
                            singleIndexEntries.Add((meta.IndexId, element, tableId, rowId));
                    }
                }
                else
                {
                    CheckUniqueConstraint(meta, tableId, rowId, decodedRows[0]);
                    var sortKey = IndexKeyCodec.BuildIndexKey(decodedRows[0], meta.ColumnIndices, meta.KeyTypes);
                    singleIndexEntries.Add((meta.IndexId, sortKey, tableId, rowId));
                }
            }
            long[]? explicitRowIds = tableDef.TryGetRowIdAliasPk(out _) ? new[] { rowId } : null;
            _store.InsertRows(tableId, encodedRows, explicitRowIds, singleIndexEntries);
        }
        else
        {
            long[]? explicitRowIds = null;
            long startRowId;
            if (tableDef.TryGetRowIdAliasPk(out var pkIdx))
            {
                explicitRowIds = new long[decodedRows.Count];
                var pkColName = tableDef.PrimaryKeyColumns[0].Name;
                for (int i = 0; i < decodedRows.Count; i++)
                {
                    var pkVal = decodedRows[i][pkIdx]
                        ?? throw new WalhallaException(
                            $"PRIMARY KEY column '{pkColName}' in table '{insert.TableName}' cannot be NULL.");
                    explicitRowIds[i] = Convert.ToInt64(pkVal);
                }
                startRowId = 0; // unused
            }
            else
            {
                startRowId = _store.GetNextRowId(tableId);
            }

            var allIndexEntries = new List<(int, byte[], int, long)>();
            foreach (var meta in indexMetas)
            {
                for (int i = 0; i < encodedRows.Count; i++)
                {
                    var rowId = explicitRowIds != null ? explicitRowIds[i] : startRowId + i;
                    if (meta.Definition.IndexType == SqlIndexType.Gin)
                    {
                        var jsonbColIdx = meta.ColumnIndices[0];
                        var jsonbValue = decodedRows[i][jsonbColIdx];
                        if (jsonbValue != null && jsonbValue != DBNull.Value)
                        {
                            var elements = GinElementExtractor.ExtractElements(jsonbValue);
                            foreach (var element in elements)
                                allIndexEntries.Add((meta.IndexId, element, tableId, rowId));
                        }
                    }
                    else
                    {
                        var sortKey = IndexKeyCodec.BuildIndexKey(decodedRows[i], meta.ColumnIndices, meta.KeyTypes);
                        allIndexEntries.Add((meta.IndexId, sortKey, tableId, rowId));
                    }
                }
            }
            _store.InsertRows(tableId, encodedRows, explicitRowIds, allIndexEntries);
        }

        // Fire AFTER triggers
        FireTriggers(insert.TableName, SqlTriggerEvent.Insert, SqlTriggerTiming.After);

        return WalhallaResultSet.Affected(encodedRows.Count);
    }

    private WalhallaResultSet ExecuteInsertSelect(SqlInsertSelectStatement insertSelect)
    {
        var tableDef = _store.GetTableDefinition(insertSelect.TableName)
            ?? throw new WalhallaException($"Table '{insertSelect.TableName}' not found.");

        FireTriggers(insertSelect.TableName, SqlTriggerEvent.Insert, SqlTriggerTiming.Before);

        var tableId = _store.GetTableId(insertSelect.TableName);

        // Build column index map
        var colIndexMap = new Dictionary<int, int>();
        for (int i = 0; i < insertSelect.Columns.Count; i++)
        {
            var idx = FindColumnIndex(tableDef, insertSelect.Columns[i]);
            if (idx < 0)
                throw new WalhallaException($"Column '{insertSelect.Columns[i]}' not found in table '{insertSelect.TableName}'.");
            colIndexMap[i] = idx;
        }

        // Execute the SELECT
        var result = ExecuteSelect(insertSelect.SelectStatement);

        if (result.Rows.Count == 0)
            return WalhallaResultSet.Affected(0);

        // Build index metadata
        var indexMetas = GetOrBuildIndexMetadata(insertSelect.TableName, tableDef);

        // Encode source rows as target table rows
        var decodedRows = new List<object?[]>(result.Rows.Count);
        var encodedRows = new List<byte[]>(result.Rows.Count);
        var insertColumns = insertSelect.Columns;

        foreach (var sourceRow in result.Rows)
        {
            var sourceValues = sourceRow.Values.ToArray();
            var rowValues = new object?[tableDef.Columns.Count];
            for (int i = 0; i < insertColumns.Count && i < sourceValues.Length; i++)
            {
                var type = tableDef.Columns[colIndexMap[i]].Type;
                rowValues[colIndexMap[i]] = ConvertValue(sourceValues[i], type);
            }
            decodedRows.Add(rowValues);
            encodedRows.Add(EncodeRowWithBlobs(tableId, rowValues, tableDef));
        }

        // Enforce foreign key and CHECK constraints on all inserted rows
        foreach (var row in decodedRows)
        {
            EnforceForeignKeyInsert(tableDef, row, decodedRows);
            CheckConstraintEvaluator.Enforce(tableDef, row);
        }

        if (insertSelect.OnConflict != null)
            return ExecuteInsertRowByRow(insertSelect.TableName, tableDef, tableId, indexMetas, decodedRows, insertSelect.OnConflict);

        long[]? insertSelectExplicitRowIds = null;
        long startRowId;
        if (tableDef.TryGetRowIdAliasPk(out var insertSelectPkIdx))
        {
            insertSelectExplicitRowIds = new long[decodedRows.Count];
            var pkColName = tableDef.PrimaryKeyColumns[0].Name;
            for (int i = 0; i < decodedRows.Count; i++)
            {
                var pkVal = decodedRows[i][insertSelectPkIdx]
                    ?? throw new WalhallaException(
                        $"PRIMARY KEY column '{pkColName}' in table '{insertSelect.TableName}' cannot be NULL.");
                insertSelectExplicitRowIds[i] = Convert.ToInt64(pkVal);
            }
            startRowId = 0; // unused
        }
        else
        {
            startRowId = _store.GetNextRowId(tableId);
        }

        var allIndexEntries = new List<(int, byte[], int, long)>();
        foreach (var meta in indexMetas)
        {
            for (int i = 0; i < decodedRows.Count; i++)
            {
                var rowId = insertSelectExplicitRowIds != null ? insertSelectExplicitRowIds[i] : startRowId + i;
                CheckUniqueConstraint(meta, tableId, rowId, decodedRows[i]);
                var sortKey = IndexKeyCodec.BuildIndexKey(decodedRows[i], meta.ColumnIndices, meta.KeyTypes);
                allIndexEntries.Add((meta.IndexId, sortKey, tableId, rowId));
            }
        }

        _store.InsertRows(tableId, encodedRows, insertSelectExplicitRowIds, allIndexEntries);

        FireTriggers(insertSelect.TableName, SqlTriggerEvent.Insert, SqlTriggerTiming.After);
        return WalhallaResultSet.Affected(encodedRows.Count);
    }

    private static object? ConvertValue(object? value, SqlScalarType targetType)
    {
        if (value == null) return null;
        return targetType switch
        {
            SqlScalarType.Int32 => Convert.ToInt32(value),
            SqlScalarType.Int64 => Convert.ToInt64(value),
            SqlScalarType.Double => Convert.ToDouble(value),
            SqlScalarType.Decimal => Convert.ToDecimal(value),
            SqlScalarType.Boolean => Convert.ToBoolean(value),
            SqlScalarType.DateTime => Convert.ToDateTime(value),
            SqlScalarType.Guid => value is Guid g ? g : Guid.Parse(value.ToString()!),
            SqlScalarType.String => value.ToString(),
            _ => value
        };
    }

    private WalhallaResultSet ExecuteInsertRowByRow(
        string tableName, SqlTableDefinition tableDef, int tableId,
        IndexMeta[] indexMetas, List<object?[]> decodedRows,
        SqlOnConflictClause onConflict)
    {
        int affected = 0;

        foreach (var row in decodedRows)
        {
            var rowId = ResolvePkAliasRowId(tableDef, row, tableId);

            // Check for PK conflict (row already exists)
            bool pkConflict = tableDef.TryGetRowIdAliasPk(out _) && _store.GetRow(tableId, rowId) != null;

            // Check for conflict on any unique index
            IndexMeta conflictMeta = default;
            bool hasConflict = pkConflict;

            if (!hasConflict)
            {
                foreach (var meta in indexMetas)
                {
                    if (!meta.Definition.IsUnique)
                        continue;

                    var sortKey = IndexKeyCodec.BuildIndexKey(row, meta.ColumnIndices, meta.KeyTypes);
                    if (_store.IndexEntryExists(meta.IndexId, sortKey))
                    {
                        conflictMeta = meta;
                        hasConflict = true;
                        break;
                    }
                }
            }

            if (!hasConflict)
            {
                // No conflict: single-row insert
                var indexEntries = new List<(int, byte[], int, long)>();
                foreach (var meta in indexMetas)
                {
                    var sortKey = IndexKeyCodec.BuildIndexKey(row, meta.ColumnIndices, meta.KeyTypes);
                    indexEntries.Add((meta.IndexId, sortKey, tableId, rowId));
                }
                long[]? explicitRowIds = tableDef.TryGetRowIdAliasPk(out _) ? new[] { rowId } : null;
                _store.InsertRows(tableId, new[] { EncodeRowWithBlobs(tableId, row, tableDef) }, explicitRowIds, indexEntries);
                FireTriggers(tableName, SqlTriggerEvent.Insert, SqlTriggerTiming.After);
                affected++;
                continue;
            }

            if (onConflict.Action == SqlConflictAction.DoNothing)
                continue;

            // DO UPDATE: find existing row by PK or index
            long existingRowId;
            if (pkConflict)
            {
                existingRowId = rowId;
            }
            else
            {
                var conflictSortKey = IndexKeyCodec.BuildIndexKey(
                    row, conflictMeta.ColumnIndices, conflictMeta.KeyTypes);
                var existing = _store.ScanIndex(
                    conflictMeta.IndexId, conflictSortKey, conflictSortKey, true, true);
                if (existing.Count == 0)
                    continue;
                existingRowId = existing[0].RowId;
            }

            var encodedExisting = _store.GetRow(tableId, existingRowId);
            if (encodedExisting == null)
                continue;

            var existingValues = RowCodec.DecodeToArray(encodedExisting, tableDef);

            // Check optional WHERE clause
            if (onConflict.Where != null)
            {
                var whereFunc = WhereCompiler.Compile(onConflict.Where, tableDef, 0);
                if (whereFunc != null && !whereFunc(existingValues, Array.Empty<object?>()))
                    continue;
            }

            // Apply assignments
            var newRow = new object?[tableDef.Columns.Count];
            Array.Copy(existingValues, newRow, existingValues.Length);

            foreach (var kv in onConflict.UpdateAssignments!)
            {
                var colIdx = FindColumnIndex(tableDef, kv.Key);
                if (colIdx < 0)
                    throw new WalhallaException($"Column '{kv.Key}' not found in table '{tableName}'.");

                var type = tableDef.Columns[colIdx].Type;
                object? value;
                if (kv.Value.StartsWith("EXCLUDED.", StringComparison.OrdinalIgnoreCase))
                {
                    var excludedCol = kv.Value["EXCLUDED.".Length..];
                    var excludedIdx = FindColumnIndex(tableDef, excludedCol);
                    if (excludedIdx < 0)
                        throw new WalhallaException($"Column '{excludedCol}' in EXCLUDED not found in table '{tableName}'.");
                    value = row[excludedIdx];
                }
                else
                {
                    value = ParseLiteral(kv.Value, type);
                }
                newRow[colIdx] = value;
            }

            // Enforce constraints on updated row
            EnforceForeignKeyUpdate(tableDef, existingValues, newRow);
            CheckConstraintEvaluator.Enforce(tableDef, newRow);

            // Fire BEFORE UPDATE trigger
            FireTriggers(tableName, SqlTriggerEvent.Update, SqlTriggerTiming.Before);

            var encodedNew = EncodeRowWithBlobs(tableId, newRow, tableDef);
            _store.UpdateRow(tableId, existingRowId, encodedNew);
            MaintainIndexesForUpdate(tableDef, tableId, existingRowId, existingValues, newRow, indexMetas);

            // Fire AFTER UPDATE trigger
            FireTriggers(tableName, SqlTriggerEvent.Update, SqlTriggerTiming.After);

            affected++;
        }

        return WalhallaResultSet.Affected(affected);
    }

    private WalhallaResultSet ExecuteMerge(SqlMergeStatement merge, WalhallaSqlTransaction? transaction = null)
    {
        var targetDef = _store.GetTableDefinition(merge.TargetTable)
            ?? throw new WalhallaException($"Target table '{merge.TargetTable}' not found.");
        var sourceDef = _store.GetTableDefinition(merge.SourceTable)
            ?? throw new WalhallaException($"Source table '{merge.SourceTable}' not found.");

        var targetTableId = _store.GetTableId(merge.TargetTable);
        var targetIndexMetas = GetOrBuildIndexMetadata(merge.TargetTable, targetDef);

        // Scan all source rows by selecting * from source table
        var sourceColumns = sourceDef.Columns.Select(c =>
            new SqlSelectColumn(c.Name, null)).ToList();
        var sourceSelect = new SqlSelectStatement(
            merge.SourceTable, null, sourceColumns, null);
        var sourceResult = transaction != null
            ? ExecuteSelect(sourceSelect, transaction)
            : ExecuteSelect(sourceSelect);

        // Determine if ON predicate is a simple PK equality for fast lookup
        string? targetPkName = null;
        int? targetPkIdx = null;
        string? sourceJoinCol = null;
        if (targetDef.TryGetRowIdAliasPk(out var pkIdx))
        {
            targetPkName = targetDef.Columns[pkIdx].Name;
            targetPkIdx = pkIdx;

            if (merge.OnPredicate is SqlWhereComparisonExpression cmp
                && cmp.Operator == SqlWhereComparisonOperator.Equal
                && cmp.Left is SqlWhereColumnExpression leftCol
                && leftCol.SimpleName.Equals(targetPkName, StringComparison.OrdinalIgnoreCase)
                && cmp.Right is SqlWhereColumnExpression rightCol)
            {
                sourceJoinCol = rightCol.SimpleName;
            }
        }

        if (sourceJoinCol == null)
            throw new NotSupportedException(
                "MERGE requires ON condition to be target.PK = source.col (only PK equality is currently supported).");

        int affected = 0;

        foreach (var sourceRow in sourceResult.Rows)
        {
            var sourceVal = GetSourceColumnValue(sourceRow, sourceDef, sourceJoinCol);
            if (sourceVal == null)
            {
                // Source PK value is NULL ? can't match anything ? NOT MATCHED
                if (merge.InsertColumns != null)
                {
                    InsertMergeRow(targetDef, targetTableId, targetIndexMetas,
                        merge, sourceRow, sourceDef);
                    affected++;
                }
                continue;
            }

            var lookupRowId = Convert.ToInt64(sourceVal);
            var encodedExisting = _store.GetRow(targetTableId, lookupRowId);

            if (encodedExisting != null && merge.UpdateAssignments != null)
            {
                // MATCHED ? UPDATE
                var existingValues = RowCodec.DecodeToArray(encodedExisting, targetDef);

                // Apply assignments
                var newRow = new object?[targetDef.Columns.Count];
                Array.Copy(existingValues, newRow, existingValues.Length);

                foreach (var kv in merge.UpdateAssignments)
                {
                    var colIdx = FindColumnIndex(targetDef, kv.Key);
                    if (colIdx < 0)
                        throw new WalhallaException(
                            $"Column '{kv.Key}' not found in target table '{merge.TargetTable}'.");

                    var type = targetDef.Columns[colIdx].Type;
                    object? value;
                    if (kv.Value.StartsWith(merge.SourceAlias + ".",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        var srcCol = kv.Value[(merge.SourceAlias.Length + 1)..];
                        value = GetSourceColumnValue(sourceRow, sourceDef, srcCol);
                    }
                    else
                    {
                        value = ParseLiteral(kv.Value, type);
                    }
                    newRow[colIdx] = value;
                }

                // Enforce constraints
                EnforceForeignKeyUpdate(targetDef, existingValues, newRow);
                CheckConstraintEvaluator.Enforce(targetDef, newRow);

                FireTriggers(merge.TargetTable, SqlTriggerEvent.Update, SqlTriggerTiming.Before);

                _store.UpdateRow(targetTableId, lookupRowId, EncodeRowWithBlobs(targetTableId, newRow, targetDef));
                MaintainIndexesForUpdate(targetDef, targetTableId, lookupRowId,
                    existingValues, newRow, targetIndexMetas);

                FireTriggers(merge.TargetTable, SqlTriggerEvent.Update, SqlTriggerTiming.After);
                affected++;
            }
            else if (encodedExisting == null && merge.InsertColumns != null)
            {
                // NOT MATCHED ? INSERT
                InsertMergeRow(targetDef, targetTableId, targetIndexMetas,
                    merge, sourceRow, sourceDef);
                affected++;
            }
        }

        return WalhallaResultSet.Affected(affected);
    }

    private void InsertMergeRow(
        SqlTableDefinition targetDef, int targetTableId, IndexMeta[] indexMetas,
        SqlMergeStatement merge, WalhallaRow sourceRow, SqlTableDefinition sourceDef)
    {
        var newRow = new object?[targetDef.Columns.Count];

        for (int i = 0; i < merge.InsertColumns!.Count && i < targetDef.Columns.Count; i++)
        {
            var srcColName = merge.InsertColumns[i];
            var value = GetSourceColumnValue(sourceRow, sourceDef, srcColName);

            // Convert type if needed
            var targetType = targetDef.Columns[i].Type;
            if (value != null && targetType != SqlScalarType.String)
                value = ConvertValue(value, targetType);

            newRow[i] = value;
        }

        // Fill remaining columns with null
        for (int i = merge.InsertColumns.Count; i < targetDef.Columns.Count; i++)
        {
            newRow[i] = null;
        }

        var rowId = ResolvePkAliasRowId(targetDef, newRow, targetTableId);

        // Check PK conflict
        if (targetDef.TryGetRowIdAliasPk(out _) && _store.GetRow(targetTableId, rowId) != null)
        {
            // Skip if row already exists (shouldn't happen if ON predicate worked correctly)
            return;
        }

        FireTriggers(merge.TargetTable, SqlTriggerEvent.Insert, SqlTriggerTiming.Before);

        var indexEntries = new List<(int, byte[], int, long)>();
        foreach (var meta in indexMetas)
        {
            var sortKey = IndexKeyCodec.BuildIndexKey(newRow, meta.ColumnIndices, meta.KeyTypes);
            indexEntries.Add((meta.IndexId, sortKey, targetTableId, rowId));
        }

        long[]? explicitRowIds = targetDef.TryGetRowIdAliasPk(out _) ? new[] { rowId } : null;
        _store.InsertRows(targetTableId, new[] { EncodeRowWithBlobs(targetTableId, newRow, targetDef) },
            explicitRowIds, indexEntries);

        FireTriggers(merge.TargetTable, SqlTriggerEvent.Insert, SqlTriggerTiming.After);
    }

    private static object? GetSourceColumnValue(WalhallaRow sourceRow,
        SqlTableDefinition sourceDef, string columnName)
    {
        for (int i = 0; i < sourceDef.Columns.Count; i++)
        {
            if (sourceDef.Columns[i].Name.Equals(columnName, StringComparison.OrdinalIgnoreCase))
                return sourceRow.GetValue(i);
        }
        throw new WalhallaException($"Column '{columnName}' not found in source table.");
    }

    private WalhallaResultSet ExecuteCompoundSelect(SqlCompoundSelectStatement compound)
    {
        var leftResult = ExecuteSelect(compound.Left);
        var rightResult = ExecuteSelect(compound.Right);

        var rows = compound.Operator switch
        {
            SqlSetOperator.UnionAll => leftResult.Rows.Concat(rightResult.Rows).ToList(),
            SqlSetOperator.Union => leftResult.Rows.Union(rightResult.Rows, new RowEqualityComparer()).ToList(),
            SqlSetOperator.Except => leftResult.Rows.Except(rightResult.Rows, new RowEqualityComparer()).ToList(),
            SqlSetOperator.Intersect => leftResult.Rows.Intersect(rightResult.Rows, new RowEqualityComparer()).ToList(),
            _ => throw new NotSupportedException($"Set operator '{compound.Operator}' is not supported.")
        };

        var outputNames = compound.Left.Columns
            .Select(c => c.Alias ?? c.Expression)
            .ToArray();
        return new WalhallaResultSet(rows, outputNames);
    }

    private sealed class RowEqualityComparer : IEqualityComparer<WalhallaRow>
    {
        public bool Equals(WalhallaRow x, WalhallaRow y)
        {
            var xv = x.Values.ToArray();
            var yv = y.Values.ToArray();
            if (xv.Length != yv.Length) return false;
            for (int i = 0; i < xv.Length; i++)
            {
                if (!EqualsValue(xv[i], yv[i])) return false;
            }
            return true;
        }

        public int GetHashCode(WalhallaRow obj)
        {
            var hash = new HashCode();
            foreach (var v in obj.Values) hash.Add(v);
            return hash.ToHashCode();
        }

        private static bool EqualsValue(object? x, object? y)
        {
            if (x == null && y == null) return true;
            if (x == null || y == null) return false;
            if (x is string sx && y is string sy)
                return CollationManager.Equals(sx, sy, null);
            return x.Equals(y);
        }
    }

    private WalhallaResultSet ExecuteWith(SqlWithStatement withStmt)
    {
        return withStmt.IsRecursive
            ? ExecuteRecursiveWith(withStmt)
            : ExecuteNonRecursiveWith(withStmt);
    }

    private static List<SqlColumnDefinition> InferColumnTypes(WalhallaResultSet result)
    {
        var defs = new List<SqlColumnDefinition>();
        // Use the first row to infer types; fall back to String for empty results or null values.
        var hasRows = result.Rows.Count > 0;
        for (int i = 0; i < result.ColumnNames.Count; i++)
        {
            var type = hasRows
                ? InferScalarType(result.Rows[0].GetValue(i))
                : SqlScalarType.String;
            defs.Add(new SqlColumnDefinition(result.ColumnNames[i], type));
        }
        return defs;
    }

    private static SqlScalarType InferScalarType(object? value)
    {
        return value switch
        {
            null => SqlScalarType.String,
            int => SqlScalarType.Int32,
            long => SqlScalarType.Int64,
            short => SqlScalarType.Int16,
            string => SqlScalarType.String,
            double => SqlScalarType.Double,
            float => SqlScalarType.Double,
            decimal => SqlScalarType.Decimal,
            bool => SqlScalarType.Boolean,
            DateTime => SqlScalarType.DateTime,
            TimeSpan => SqlScalarType.Time,
            Guid => SqlScalarType.Guid,
            byte[] => SqlScalarType.Binary,
            _ => SqlScalarType.String
        };
    }

    private WalhallaResultSet ExecuteNonRecursiveWith(SqlWithStatement withStmt)
    {
        // Execute each CTE and register as temp table
        var tempNames = new List<string>();
        try
        {
            foreach (var cte in withStmt.Ctes)
            {
                var innerResult = ExecuteSelect((SqlSelectStatement)cte.Body);
                var columnDefs = InferColumnTypes(innerResult);

                var tempTableDef = new SqlTableDefinition(
                    cte.Name, columnDefs,
                    new List<SqlIndexDefinition>(), new List<SqlForeignKeyDefinition>(),
                    new List<SqlProjectionDefinition>());

                _store.CreateTable(tempTableDef);
                tempNames.Add(cte.Name);

                var rows = innerResult.Rows
                    .Select(r => r.Values.ToArray())
                    .ToList() as IReadOnlyList<object?[]>;
                if (rows.Count > 0)
                    InsertBatch(cte.Name, rows);
            }

            // Execute the main statement
            return ExecuteMainStatement(withStmt.MainStatement);
        }
        finally
        {
            foreach (var name in tempNames)
            {
                try { _store.DropTable(name); }
                catch { /* best-effort cleanup */ }
            }
        }
    }

    private WalhallaResultSet ExecuteRecursiveWith(SqlWithStatement withStmt)
    {
        if (withStmt.Ctes.Count != 1)
            throw new NotSupportedException("WITH RECURSIVE supports exactly one CTE in this version.");

        var cte = withStmt.Ctes[0];
        var compound = (SqlCompoundSelectStatement)cte.Body;
        var anchor = compound.Left;
        var recursive = compound.Right;
        var setOp = compound.Operator;

        // Execute anchor
        var anchorResult = ExecuteSelect(anchor);
        var accumulatedRows = new List<WalhallaRow>(anchorResult.Rows);
        var workingRows = accumulatedRows.ToList();

        var columnDefs = InferColumnTypes(anchorResult);

        var tempTableDef = new SqlTableDefinition(
            cte.Name, columnDefs,
            new List<SqlIndexDefinition>(), new List<SqlForeignKeyDefinition>(),
            new List<SqlProjectionDefinition>());

        var tempCreated = false;
        try
        {
            var iteration = 0;
            var comparer = new RowEqualityComparer();

            while (workingRows.Count > 0)
            {
                iteration++;
                if (iteration > _options.RecursiveCteMaxIterations)
                    throw new WalhallaException(
                        $"Recursive CTE query exceeded maximum iterations ({_options.RecursiveCteMaxIterations}). " +
                        "Consider increasing RecursiveCteMaxIterations or adding a termination condition.",
                        "42P19");

                // Replace temp table with current working rows
                if (tempCreated)
                {
                    _store.DropTable(cte.Name);
                    _store.CreateTable(tempTableDef);
                }
                else
                {
                    _store.CreateTable(tempTableDef);
                    tempCreated = true;
                }

                if (workingRows.Count > 0)
                    InsertBatch(cte.Name, workingRows.Select(r => r.Values.ToArray()).ToList());

                var recursiveResult = ExecuteSelect(recursive);
                var newRows = recursiveResult.Rows;

                if (setOp == SqlSetOperator.Union)
                {
                    // Deduplicate within new rows first, then against accumulated rows.
                    newRows = newRows.Distinct(comparer).ToList();
                    newRows = newRows
                        .Where(r => !accumulatedRows.Contains(r, comparer))
                        .ToList();
                }

                if (newRows.Count == 0)
                    break;

                accumulatedRows.AddRange(newRows);
                workingRows = newRows.ToList();
            }

            // Rebuild temp table with all accumulated rows for the main statement
            if (tempCreated)
            {
                _store.DropTable(cte.Name);
                _store.CreateTable(tempTableDef);
            }
            else
            {
                _store.CreateTable(tempTableDef);
                tempCreated = true;
            }

            if (accumulatedRows.Count > 0)
                InsertBatch(cte.Name, accumulatedRows.Select(r => r.Values.ToArray()).ToList());

            return ExecuteMainStatement(withStmt.MainStatement);
        }
        finally
        {
            if (tempCreated)
            {
                try { _store.DropTable(cte.Name); }
                catch { /* best-effort cleanup */ }
            }
        }
    }

    private WalhallaResultSet ExecuteMainStatement(SqlStatement stmt)
    {
        return stmt switch
        {
            SqlSelectStatement select => ExecuteSelect(select),
            SqlCompoundSelectStatement compound => ExecuteCompoundSelect(compound),
            _ => throw new NotSupportedException($"Statement type '{stmt.GetType().Name}' not supported after WITH.")
        };
    }

    private WalhallaResultSet ExecuteCreateTable(SqlCreateTableStatement create)
    {
        InvalidatePlanCache();
        BumpSchemaVersion(create.Definition.CollectionName);
        if (create.Definition.CheckConstraints is { Count: > 0 } checks)
            CheckConstraintEvaluator.Validate(checks, create.Definition);
        _store.CreateTable(create.Definition);
        return WalhallaResultSet.Affected(0);
    }

    private WalhallaResultSet ExecuteDropTable(SqlDropTableStatement drop)
    {
        InvalidatePlanCache();
        BumpSchemaVersion(drop.TableName);

        // Check for inbound foreign key references before dropping.
        var allTables = _store.GetAllTables();
        foreach (var otherTable in allTables)
        {
            if (otherTable.ForeignKeys != null)
            {
                foreach (var fk in otherTable.ForeignKeys)
                {
                    if (string.Equals(fk.ReferencedCollection, drop.TableName, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new WalhallaException(
                            $"Cannot DROP TABLE '{drop.TableName}': " +
                            $"referenced by {otherTable.CollectionName}.{fk.ConstraintName}.");
                    }
                }
            }
        }

        var tableId = _store.GetTableId(drop.TableName);
        _store.DropTable(drop.TableName);
        if (tableId >= 0)
        {
            _statisticsCatalog.Invalidate(tableId);
            _store.DeleteStatistics(tableId);
        }
        lock (_metaSync)
            _views.Remove(drop.TableName);
        return WalhallaResultSet.Affected(0);
    }

    private WalhallaResultSet ExecuteTruncateTable(SqlTruncateTableStatement truncate)
    {
        var tableDef = _store.GetTableDefinition(truncate.TableName)
            ?? throw new WalhallaException($"Table '{truncate.TableName}' not found.");
        var tableId = _store.GetTableId(truncate.TableName);

        InvalidatePlanCache();
        BumpSchemaVersion(truncate.TableName);
        _store.TruncateTable(truncate.TableName);
        _statisticsCatalog.Invalidate(tableId);
        return WalhallaResultSet.Affected(0);
    }

    private WalhallaResultSet ExecuteAlterTable(SqlAlterTableStatement alter)
    {
        InvalidatePlanCache();
        BumpSchemaVersion(alter.TableName);
        var tableDef = _store.GetTableDefinition(alter.TableName)
            ?? throw new WalhallaException($"Table '{alter.TableName}' not found.");
        var tableId = _store.GetTableId(alter.TableName);

        switch (alter.Action)
        {
            case SqlAlterActionType.AddColumn:
            {
                // Guardrail: reject if column already exists.
                if (tableDef.Columns.Any(c => string.Equals(c.Name, alter.ColumnName, StringComparison.OrdinalIgnoreCase)))
                    throw new WalhallaException($"Column '{alter.ColumnName}' already exists in collection '{alter.TableName}'.");

                var colNotNull = alter.NotNull == true;
                var newCol = new SqlColumnDefinition(
                    alter.ColumnName!, alter.NewType!.Value, !colNotNull, Collation: alter.Collation);
                var newColumns = new List<SqlColumnDefinition>(tableDef.Columns) { newCol };
                var newTableDef = tableDef with { Columns = newColumns };
                _store.UpdateTableDefinition(alter.TableName, tableId, newTableDef);

                // Re-encode existing rows to include the new default value.
                // Use ScanWithRowIds to track the actual stored rowId (not the PK column value).
                var defaultValue = alter.DefaultValue;
                var existingRows = new List<(long RowId, object?[] Row)>();
                _store.ScanWithRowIds(tableId,
                    encoded => RowCodec.DecodeToArray(encoded, tableDef),
                    null, existingRows, int.MaxValue);
                foreach (var (storedRowId, oldRow) in existingRows)
                {
                    var newRow = new object?[newColumns.Count];
                    for (int i = 0; i < oldRow.Length; i++)
                        newRow[i] = oldRow[i];
                    newRow[newColumns.Count - 1] = defaultValue;
                    _store.UpdateRow(tableId, storedRowId, EncodeRowWithBlobs(tableId, newRow, newTableDef));
                }
                return WalhallaResultSet.Affected(0);
            }

            case SqlAlterActionType.DropColumn:
            {
                int dropColIdx = -1;
                for (int i = 0; i < tableDef.Columns.Count; i++)
                {
                    if (string.Equals(tableDef.Columns[i].Name, alter.ColumnName, StringComparison.OrdinalIgnoreCase))
                    { dropColIdx = i; break; }
                }
                if (dropColIdx < 0)
                    throw new WalhallaException($"Column '{alter.ColumnName}' not found.");

                var newColumns = tableDef.Columns
                    .Where(c => !string.Equals(c.Name, alter.ColumnName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var newTableDef = tableDef with { Columns = newColumns };
                _store.UpdateTableDefinition(alter.TableName, tableId, newTableDef);

                // Re-encode existing rows without the dropped column.
                // If the dropped column was Binary, its BlobRefs become orphaned
                // and will be reclaimed by the next VACUUM.
                var existingRows = new List<(long RowId, object?[] Row)>();
                _store.ScanWithRowIds(tableId,
                    encoded => RowCodec.DecodeToArray(encoded, tableDef),
                    null, existingRows, int.MaxValue);
                foreach (var (storedRowId, oldRow) in existingRows)
                {
                    var newRow = new object?[newColumns.Count];
                    int newIdx = 0;
                    for (int i = 0; i < oldRow.Length; i++)
                    {
                        if (i == dropColIdx) continue;
                        newRow[newIdx++] = oldRow[i];
                    }
                    _store.UpdateRow(tableId, storedRowId, EncodeRowWithBlobs(tableId, newRow, newTableDef));
                }
                return WalhallaResultSet.Affected(0);
            }

            case SqlAlterActionType.AlterColumn:
            {
                int alterColIdx = -1;
                for (int i = 0; i < tableDef.Columns.Count; i++)
                {
                    if (string.Equals(tableDef.Columns[i].Name, alter.ColumnName, StringComparison.OrdinalIgnoreCase))
                    { alterColIdx = i; break; }
                }
                if (alterColIdx < 0)
                    throw new WalhallaException($"Column '{alter.ColumnName}' not found in collection '{alter.TableName}'.");

                var oldColumn = tableDef.Columns[alterColIdx];
                var newType = alter.NewType ?? oldColumn.Type;
                var newNullable = alter.NotNull.HasValue ? !alter.NotNull.Value : oldColumn.IsNullable;
                var newColumn = oldColumn with { Type = newType, IsNullable = newNullable };
                var newColumns = tableDef.Columns.ToList();
                newColumns[alterColIdx] = newColumn;
                var newTableDef = tableDef with { Columns = newColumns };
                _store.UpdateTableDefinition(alter.TableName, tableId, newTableDef);

                // Re-encode existing rows so stored values match the new column type.
                var existingRows = new List<(long RowId, object?[] Row)>();
                _store.ScanWithRowIds(tableId,
                    encoded => RowCodec.DecodeToArray(encoded, tableDef),
                    null, existingRows, int.MaxValue);
                foreach (var (storedRowId, oldRow) in existingRows)
                {
                    var newRow = new object?[newColumns.Count];
                    for (int i = 0; i < oldRow.Length; i++)
                    {
                        if (i == alterColIdx && newType != oldColumn.Type)
                        {
                            newRow[i] = ConvertColumnValue(oldRow[i], newType, newNullable);
                        }
                        else
                        {
                            newRow[i] = oldRow[i];
                        }
                    }
                    _store.UpdateRow(tableId, storedRowId, EncodeRowWithBlobs(tableId, newRow, newTableDef));
                }
                return WalhallaResultSet.Affected(0);
            }

            case SqlAlterActionType.RenameColumn:
            {
                // Validate source column exists
                if (!tableDef.Columns.Any(c => string.Equals(c.Name, alter.ColumnName, StringComparison.OrdinalIgnoreCase)))
                    throw new WalhallaException($"Column '{alter.ColumnName}' not found in collection '{alter.TableName}'.");

                // Validate target column name does not already exist
                if (tableDef.Columns.Any(c => string.Equals(c.Name, alter.NewColumnName, StringComparison.OrdinalIgnoreCase)))
                    throw new WalhallaException($"Column '{alter.NewColumnName}' already exists in collection '{alter.TableName}'.");

                var newColumns = tableDef.Columns.Select(c =>
                    string.Equals(c.Name, alter.ColumnName, StringComparison.OrdinalIgnoreCase)
                        ? c with { Name = alter.NewColumnName! }
                        : c).ToList();
                var newTableDef = tableDef with { Columns = newColumns };
                _store.UpdateTableDefinition(alter.TableName, tableId, newTableDef);
                return WalhallaResultSet.Affected(0);
            }

            case SqlAlterActionType.RenameTable:
            {
                _store.RenameTable(alter.TableName, alter.NewTableName!);
                return WalhallaResultSet.Affected(0);
            }

            case SqlAlterActionType.AddConstraint:
            {
                // FOREIGN KEY constraint
                if (alter.ForeignKey != null)
                {
                    var fk = alter.ForeignKey;
                    var existingFks = tableDef.ForeignKeys ?? Array.Empty<SqlForeignKeyDefinition>();

                    // Validate constraint name uniqueness
                    var constraintName = string.IsNullOrWhiteSpace(fk.ConstraintName)
                        ? $"FK_{alter.TableName}_{string.Join("_", fk.ColumnNames)}"
                        : fk.ConstraintName;
                    if (existingFks.Any(existing =>
                            string.Equals(existing.ConstraintName, constraintName, StringComparison.OrdinalIgnoreCase)))
                        throw new WalhallaConstraintException(
                            $"Constraint '{constraintName}' already exists in collection '{alter.TableName}'.");

                    // Validate referenced table exists
                    var refTableDef = _store.GetTableDefinition(fk.ReferencedCollection);
                    if (refTableDef == null)
                        throw new WalhallaConstraintException(
                            $"Foreign key '{constraintName}' references unknown table '{fk.ReferencedCollection}'.");

                    // Validate referenced columns exist
                    foreach (var refCol in fk.ReferencedColumns)
                    {
                        if (!refTableDef.Columns.Any(c =>
                                string.Equals(c.Name, refCol, StringComparison.OrdinalIgnoreCase)))
                            throw new WalhallaConstraintException(
                                $"Foreign key '{constraintName}' references unknown column '{fk.ReferencedCollection}.{refCol}'.");
                    }

                    // Validate FK columns exist in this table
                    foreach (var colName in fk.ColumnNames)
                    {
                        if (!tableDef.Columns.Any(c =>
                                string.Equals(c.Name, colName, StringComparison.OrdinalIgnoreCase)))
                            throw new WalhallaConstraintException(
                                $"Foreign key '{constraintName}' references unknown column '{alter.TableName}.{colName}'.");
                    }

                    // Add FK to table definition
                    var normalizedFk = new SqlForeignKeyDefinition(
                        constraintName,
                        fk.ColumnNames,
                        fk.ReferencedCollection,
                        fk.ReferencedColumns,
                        fk.OnDelete,
                        fk.OnUpdate);
                    var newFks = new List<SqlForeignKeyDefinition>(existingFks) { normalizedFk };
                    var newTableDef = tableDef with { ForeignKeys = newFks };

                    // Validate existing rows satisfy the FK constraint
                    var existingRows = new List<object?[]>();
                    _store.ScanWithPredicate(tableId,
                        encoded => RowCodec.DecodeToArray(encoded, tableDef),
                        null, existingRows, int.MaxValue);
                    foreach (var row in existingRows)
                        EnforceForeignKeyInsert(newTableDef, row);

                    _store.UpdateTableDefinition(alter.TableName, tableId, newTableDef);
                    return WalhallaResultSet.Affected(0);
                }

                // CHECK constraint (existing logic)
                var checkName = alter.ConstraintName!;
                var existingChecks = tableDef.CheckConstraints;
                if (existingChecks != null && existingChecks.Any(c =>
                        string.Equals(c.Name, checkName, StringComparison.OrdinalIgnoreCase)))
                    throw new WalhallaException(
                        $"Constraint '{checkName}' already exists on table '{alter.TableName}'.");

                var newCheck = new SqlCheckConstraint(checkName, alter.CheckExpression!);
                var newChecks = new List<SqlCheckConstraint>(existingChecks ?? Array.Empty<SqlCheckConstraint>())
                {
                    newCheck
                };
                var checkTableDef = tableDef with { CheckConstraints = newChecks };

                // Validate the new constraint expression itself (unknown columns, unsupported constructs).
                CheckConstraintEvaluator.Validate(newChecks, checkTableDef);

                // Validate all existing rows satisfy the new constraint before persisting.
                var checkExistingRows = new List<object?[]>();
                _store.ScanWithPredicate(tableId,
                    encoded => RowCodec.DecodeToArray(encoded, tableDef),
                    null, checkExistingRows, int.MaxValue);
                foreach (var row in checkExistingRows)
                    CheckConstraintEvaluator.Enforce(checkTableDef, row);

                _store.UpdateTableDefinition(alter.TableName, tableId, checkTableDef);
                return WalhallaResultSet.Affected(0);
            }

            case SqlAlterActionType.DropConstraint:
            {
                var dropName = alter.ConstraintName!;

                // Check FOREIGN KEY constraints first
                var existingFks = tableDef.ForeignKeys;
                if (existingFks != null)
                {
                    var fkMatch = existingFks.FirstOrDefault(fk =>
                        string.Equals(fk.ConstraintName, dropName, StringComparison.OrdinalIgnoreCase));
                    if (fkMatch != null)
                    {
                        var newFks = existingFks
                            .Where(fk => !string.Equals(fk.ConstraintName, dropName, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        var newTableDef = tableDef with
                        {
                            ForeignKeys = newFks.Count > 0 ? newFks : null
                        };
                        _store.UpdateTableDefinition(alter.TableName, tableId, newTableDef);
                        return WalhallaResultSet.Affected(0);
                    }
                }

                // Fall through to CHECK constraints
                var existingChecks = tableDef.CheckConstraints;
                if (existingChecks == null || !existingChecks.Any(c =>
                        string.Equals(c.Name, dropName, StringComparison.OrdinalIgnoreCase)))
                    throw new WalhallaException(
                        $"Constraint '{dropName}' not found in collection '{alter.TableName}'.");

                var newChecks = existingChecks
                    .Where(c => !string.Equals(c.Name, dropName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var newCheckTableDef = tableDef with
                {
                    CheckConstraints = newChecks.Count > 0 ? newChecks : null
                };
                _store.UpdateTableDefinition(alter.TableName, tableId, newCheckTableDef);
                return WalhallaResultSet.Affected(0);
            }

            default:
                throw new NotSupportedException($"ALTER TABLE action '{alter.Action}' is not supported.");
        }
    }

    private WalhallaResultSet ExecuteCreateView(SqlCreateViewStatement createView)
    {
        InvalidatePlanCache();
        lock (_metaSync)
            _views[createView.ViewName] = createView;
        return WalhallaResultSet.Affected(0);
    }

    private WalhallaResultSet ExecuteDropView(SqlDropViewStatement dropView)
    {
        InvalidatePlanCache();
        lock (_metaSync)
            _views.Remove(dropView.ViewName);
        return WalhallaResultSet.Affected(0);
    }

    private WalhallaResultSet ExecuteCreateIndex(SqlCreateIndexStatement createIndex)
    {
        InvalidatePlanCache();
        BumpSchemaVersion(createIndex.TableName);
        var tableDef = _store.GetTableDefinition(createIndex.TableName)
            ?? throw new WalhallaException($"Table '{createIndex.TableName}' not found.");

        var tableId = _store.GetTableId(createIndex.TableName);

        // Check index doesn't already exist.
        if (_store.GetIndexId(createIndex.TableName, createIndex.IndexName) >= 0)
            throw new WalhallaException(
                $"Index '{createIndex.IndexName}' already exists on table '{createIndex.TableName}'.");

        // Validate GIN index: must be on a single JSONB column.
        if (createIndex.IndexType == SqlIndexType.Gin)
        {
            if (createIndex.ColumnNames.Count != 1)
                throw new WalhallaException("GIN index requires exactly one column.");
            var ginColIdx = FindColumnIndex(tableDef, createIndex.ColumnNames[0]);
            if (ginColIdx < 0)
                throw new WalhallaException($"Column '{createIndex.ColumnNames[0]}' not found in table '{createIndex.TableName}'.");
            var ginColType = tableDef.Columns[ginColIdx].Type;
            if (ginColType != SqlScalarType.Json)
                throw new WalhallaException(
                    $"GIN index requires a JSONB column, but '{createIndex.ColumnNames[0]}' has type {ginColType}.");
        }

        // Create new index definition.
        var idxDef = new SqlIndexDefinition(createIndex.IndexName, createIndex.ColumnNames, createIndex.IsUnique)
        {
            IndexType = createIndex.IndexType
        };
        var newIndexes = new List<SqlIndexDefinition>(tableDef.Indexes) { idxDef };

        // Build new table definition with the added index.
        var newTableDef = new SqlTableDefinition(
            tableDef.CollectionName, tableDef.Columns, newIndexes,
            tableDef.ForeignKeys, tableDef.Projections, tableDef.CheckConstraints);

        // Allocate index ID and register.
        var indexId = _store.AllocateIndexId();
        _store.AddIndexToTable(createIndex.TableName, idxDef.IndexName, indexId, newTableDef);

        // Build index metadata for the new index.
        var colIndices = new int[idxDef.ColumnNames.Count];
        var keyTypes = new SqlScalarType[idxDef.ColumnNames.Count];
        for (int i = 0; i < idxDef.ColumnNames.Count; i++)
        {
            colIndices[i] = FindColumnIndex(tableDef, idxDef.ColumnNames[i]);
            keyTypes[i] = tableDef.Columns[colIndices[i]].Type;
        }

        // Build index entries for all existing rows.
        var existingRows = new List<object?[]>();
        RowDecoder decoder = encoded => RowCodec.DecodeToArray(encoded, tableDef);
        _store.ScanWithPredicate(tableId, decoder, null, existingRows, int.MaxValue);

        if (existingRows.Count > 0)
        {
            if (createIndex.IndexType == SqlIndexType.Gin)
            {
                var allEntries = new List<(int IndexId, byte[] SortKey, int TableId, long RowId)>();
                var jsonbColIdx = FindColumnIndex(tableDef, createIndex.ColumnNames[0]);
                foreach (var row in existingRows)
                {
                    var pkIdx = GetPrimaryKeyIndex(tableDef);
                    var rowId = Convert.ToInt64(row[pkIdx]);
                    var jsonbValue = row[jsonbColIdx];
                    if (jsonbValue == null || jsonbValue == DBNull.Value) continue;
                    var elements = GinElementExtractor.ExtractElements(jsonbValue);
                    foreach (var element in elements)
                    {
                        allEntries.Add((indexId, element, tableId, rowId));
                    }
                }
                _store.InsertIndexEntries(allEntries);
            }
            else
            {
                var entries = new List<(byte[] SortKey, int TableId, long RowId)>(existingRows.Count);
                foreach (var row in existingRows)
                {
                    var pkIdx = GetPrimaryKeyIndex(tableDef);
                    var rowId = Convert.ToInt64(row[pkIdx]);
                    var sortKey = IndexKeyCodec.BuildIndexKey(row, colIndices, keyTypes);
                    entries.Add((sortKey, tableId, rowId));
                }
                _store.InsertIndexEntries(indexId, entries);
            }
        }

        return WalhallaResultSet.Affected(0);
    }

    private WalhallaResultSet ExecuteDropIndex(SqlDropIndexStatement dropIndex)
    {
        InvalidatePlanCache();
        BumpSchemaVersion(dropIndex.TableName);
        var tableDef = _store.GetTableDefinition(dropIndex.TableName)
            ?? throw new WalhallaException($"Table '{dropIndex.TableName}' not found.");

        var indexId = _store.GetIndexId(dropIndex.TableName, dropIndex.IndexName);
        if (indexId < 0)
            throw new WalhallaException(
                $"Index '{dropIndex.IndexName}' does not exist on table '{dropIndex.TableName}'.");

        // Build new table definition without the index.
        var newIndexes = new List<SqlIndexDefinition>();
        foreach (var idx in tableDef.Indexes)
        {
            if (!string.Equals(idx.IndexName, dropIndex.IndexName, StringComparison.OrdinalIgnoreCase))
                newIndexes.Add(idx);
        }
        var newTableDef = new SqlTableDefinition(
            tableDef.CollectionName, tableDef.Columns, newIndexes,
            tableDef.ForeignKeys, tableDef.Projections, tableDef.CheckConstraints);

        _store.RemoveIndexFromTable(dropIndex.TableName, dropIndex.IndexName, indexId, newTableDef);

        return WalhallaResultSet.Affected(0);
    }

    private WalhallaResultSet ExecuteUpdate(SqlUpdateStatement update)
    {
        var tableDef = _store.GetTableDefinition(update.TableName)
            ?? throw new WalhallaException($"Table '{update.TableName}' not found.");

        FireTriggers(update.TableName, SqlTriggerEvent.Update, SqlTriggerTiming.Before);

        var tableId = _store.GetTableId(update.TableName);

        // PK fast path: WHERE PK = constant
        var pkConst = TryExtractPkLiteral(update.Where, tableDef);
        if (pkConst != null)
        {
            var rowId = Convert.ToInt64(pkConst);
            var encoded = _store.GetRow(tableId, rowId);
            if (encoded == null)
                return WalhallaResultSet.Affected(0);

            var row = RowCodec.DecodeToArray(encoded, tableDef);
            var oldRow = new object?[row.Length];
            Array.Copy(row, oldRow, row.Length);

            foreach (var kv in update.Assignments)
            {
                var colIdx = FindColumnIndex(tableDef, kv.Key);
                if (colIdx < 0) continue;
                row[colIdx] = ParseLiteral(kv.Value, tableDef.Columns[colIdx].Type);
            }

            // Enforce foreign key and CHECK constraints on updated row
            EnforceForeignKeyUpdate(tableDef, oldRow, row);
            CheckConstraintEvaluator.Enforce(tableDef, row);

            var newEncoded = EncodeRowWithBlobs(tableId, row, tableDef);
            _store.UpdateRow(tableId, rowId, newEncoded);

            var indexMetas = GetOrBuildIndexMetadata(update.TableName, tableDef);
            MaintainIndexesForUpdate(tableDef, tableId, rowId, oldRow, row, indexMetas);

            FireTriggers(update.TableName, SqlTriggerEvent.Update, SqlTriggerTiming.After);
            return WalhallaResultSet.Affected(1);
        }

        // Build WHERE delegate (parameterless)
        Func<object?[], bool>? predicate = null;
        if (update.Where != null)
        {
            var whereDelegate = WhereCompiler.Compile(update.Where, tableDef, 0);
            if (whereDelegate != null)
            {
                var emptyParams = Array.Empty<object?>();
                predicate = row => whereDelegate(row, emptyParams);
            }
        }

        // Find matching rows
        var matchingRows = new List<object?[]>();
        var matchingRowIds = new List<long>();
        RowDecoder decoder = encoded => RowCodec.DecodeToArray(encoded, tableDef);
        _store.ScanWithPredicateFirst(tableId, tableDef,
            update.Where?.CollectColumnIndices(tableDef) ?? Array.Empty<int>(),
            decoder, predicate, matchingRows, int.MaxValue, matchingRowIds);

        // Update each matching row with assignments
        var affected = 0;
        var indexMetasAll = GetOrBuildIndexMetadata(update.TableName, tableDef);
        for (var i = 0; i < matchingRows.Count; i++)
        {
            var row = matchingRows[i];
            var rowId = matchingRowIds[i];

            var oldRow = new object?[row.Length];
            Array.Copy(row, oldRow, row.Length);

            foreach (var kv in update.Assignments)
            {
                var colIdx = FindColumnIndex(tableDef, kv.Key);
                if (colIdx < 0) continue;
                row[colIdx] = ParseLiteral(kv.Value, tableDef.Columns[colIdx].Type);
            }

            // Enforce foreign key and CHECK constraints on updated row
            EnforceForeignKeyUpdate(tableDef, oldRow, row);
            CheckConstraintEvaluator.Enforce(tableDef, row);

            var encoded = EncodeRowWithBlobs(tableId, row, tableDef);
            _store.UpdateRow(tableId, rowId, encoded);

            MaintainIndexesForUpdate(tableDef, tableId, rowId, oldRow, row, indexMetasAll);
            affected++;
        }

        FireTriggers(update.TableName, SqlTriggerEvent.Update, SqlTriggerTiming.After);
        return WalhallaResultSet.Affected(affected);
    }

    private WalhallaResultSet ExecuteDelete(SqlDeleteStatement delete)
    {
        var tableDef = _store.GetTableDefinition(delete.TableName)
            ?? throw new WalhallaException($"Table '{delete.TableName}' not found.");

        FireTriggers(delete.TableName, SqlTriggerEvent.Delete, SqlTriggerTiming.Before);

        var tableId = _store.GetTableId(delete.TableName);

        // PK fast path: WHERE PK = constant
        var pkConst = TryExtractPkLiteral(delete.Where, tableDef);
        if (pkConst != null)
        {
            var rowId = Convert.ToInt64(pkConst);
            var encoded = _store.GetRow(tableId, rowId);
            if (encoded == null)
                return WalhallaResultSet.Affected(0);

            var row = RowCodec.DecodeToArray(encoded, tableDef);

            // Check foreign key constraints before deleting
            EnforceForeignKeyDelete(tableDef, rowId, row);

            var pkIndexMetas = GetOrBuildIndexMetadata(delete.TableName, tableDef);

            var pkIndexEntries = new List<(int IndexId, byte[] SortKey, int TableId, long RowId)>();
            foreach (var meta in pkIndexMetas)
            {
                if (meta.Definition.IndexType == SqlIndexType.Gin)
                {
                    var jsonbColIdx = meta.ColumnIndices[0];
                    var jsonbValue = row[jsonbColIdx];
                    if (jsonbValue != null && jsonbValue != DBNull.Value)
                    {
                        var elements = GinElementExtractor.ExtractElements(jsonbValue);
                        foreach (var element in elements)
                            pkIndexEntries.Add((meta.IndexId, element, tableId, rowId));
                    }
                }
                else
                {
                    var sortKey = IndexKeyCodec.BuildIndexKey(row, meta.ColumnIndices, meta.KeyTypes);
                    pkIndexEntries.Add((meta.IndexId, sortKey, tableId, rowId));
                }
            }

            _store.DeleteIndexEntries(pkIndexEntries);
            _store.DeleteRows(tableId, new[] { rowId });

            FireTriggers(delete.TableName, SqlTriggerEvent.Delete, SqlTriggerTiming.After);
            return WalhallaResultSet.Affected(1);
        }

        Func<object?[], bool>? predicate = null;
        if (delete.Where != null)
        {
            var whereDelegate = WhereCompiler.Compile(delete.Where, tableDef, 0);
            if (whereDelegate != null)
            {
                var emptyParams = Array.Empty<object?>();
                predicate = row => whereDelegate(row, emptyParams);
            }
        }

        var indexMetas = GetOrBuildIndexMetadata(delete.TableName, tableDef);
        bool hasGinIndex = false;
        foreach (var meta in indexMetas)
            if (meta.Definition.IndexType == SqlIndexType.Gin) { hasGinIndex = true; break; }

        var pkRange = QueryPlanner.TryExtractPkRange(delete.Where, tableDef);

        // DELETE Fast-Path: reiner PK-Bereich, kein WHERE-Prädikat auf Nicht-PK-Spalten,
        // keine GIN-Indexe, keine Trigger und keine eingehenden FK-Constraints.
        // In diesem Fall decodieren wir die Rows nicht vollständig, sondern holen nur
        // die Row-IDs, lesen die encoded Rows und decodieren nur die Index-Spalten.
        bool whereOnlyOnPk = delete.Where == null || IsWhereOnlyOnPrimaryKey(delete.Where, tableDef);
        bool canFastDelete = pkRange != null
                             && pkRange.HasLiteralBounds
                             && whereOnlyOnPk
                             && !hasGinIndex
                             && !HasTriggersFor(delete.TableName, SqlTriggerEvent.Delete)
                             && !HasIncomingForeignKeys(tableDef);
        if (canFastDelete)
        {
            long minRowId = pkRange.LiteralMin;
            long maxRowId = pkRange.LiteralMax;
            if (!pkRange.MinInclusive && minRowId != long.MinValue) minRowId++;
            if (!pkRange.MaxInclusive && maxRowId != long.MaxValue) maxRowId--;

            var rowIds = new List<long>();
            _store.ScanRowKeyRangeRowIdsOnly(tableId, minRowId, maxRowId, rowIds);
            if (rowIds.Count == 0)
                return WalhallaResultSet.Affected(0);

            var indexEntries = new List<(int IndexId, byte[] SortKey, int TableId, long RowId)>();
            foreach (var rowId in rowIds)
            {
                var encoded = _store.GetRow(tableId, rowId);
                if (encoded == null) continue;

                foreach (var meta in indexMetas)
                {
                    var projection = RowCodec.DecodeColumns(encoded.AsSpan(), tableDef, meta.ColumnIndices);
                    var sortKey = IndexKeyCodec.BuildIndexKey(projection, meta.KeyTypes);
                    indexEntries.Add((meta.IndexId, sortKey, tableId, rowId));
                }
            }

            _store.DeleteIndexEntries(indexEntries);
            _store.DeleteRows(tableId, rowIds);

            FireTriggers(delete.TableName, SqlTriggerEvent.Delete, SqlTriggerTiming.After);
            return WalhallaResultSet.Affected(rowIds.Count);
        }

        // PK range fast path: WHERE PK BETWEEN literal AND literal (or </<=/>/>= on PK).
        // Avoids full table scan by seeking directly into the row-key range.
        var matchingRows = new List<object?[]>();
        var matchingRowIds = new List<long>();
        RowDecoder decoder = encoded => RowCodec.DecodeToPooledArray(encoded, tableDef);

        if (pkRange != null && pkRange.HasLiteralBounds)
        {
            long minRowId = pkRange.LiteralMin;
            long maxRowId = pkRange.LiteralMax;
            if (!pkRange.MinInclusive && minRowId != long.MinValue) minRowId++;
            if (!pkRange.MaxInclusive && maxRowId != long.MaxValue) maxRowId--;

            _store.ScanRowKeyRange(tableId, minRowId, maxRowId, decoder, predicate, matchingRows, int.MaxValue, matchingRowIds);
        }
        else
        {
            _store.ScanWithPredicateFirst(tableId, tableDef,
                delete.Where?.CollectColumnIndices(tableDef) ?? Array.Empty<int>(),
                decoder, predicate, matchingRows, int.MaxValue, matchingRowIds);
        }

        if (matchingRows.Count == 0)
            return WalhallaResultSet.Affected(0);

        // Check foreign key constraints before deleting
        for (var i = 0; i < matchingRows.Count; i++)
        {
            var delRow = matchingRows[i];
            var delRowId = matchingRowIds[i];
            EnforceForeignKeyDelete(tableDef, delRowId, delRow);
        }

        // Collect all index entries and row IDs, then flush in one WAL batch.
        var rowIds2 = matchingRowIds;
        var indexEntries2 = new List<(int IndexId, byte[] SortKey, int TableId, long RowId)>();

        for (var i = 0; i < matchingRows.Count; i++)
        {
            var row = matchingRows[i];
            var rowId = matchingRowIds[i];

            foreach (var meta in indexMetas)
            {
                if (meta.Definition.IndexType == SqlIndexType.Gin)
                {
                    var jsonbColIdx = meta.ColumnIndices[0];
                    var jsonbValue = row[jsonbColIdx];
                    if (jsonbValue != null && jsonbValue != DBNull.Value)
                    {
                        var elements = GinElementExtractor.ExtractElements(jsonbValue);
                        foreach (var element in elements)
                            indexEntries2.Add((meta.IndexId, element, tableId, rowId));
                    }
                }
                else
                {
                    var sortKey = IndexKeyCodec.BuildIndexKey(row, meta.ColumnIndices, meta.KeyTypes);
                    indexEntries2.Add((meta.IndexId, sortKey, tableId, rowId));
                }
            }
        }

        _store.DeleteIndexEntries(indexEntries2);
        _store.DeleteRows(tableId, rowIds2);

        // Pooled row buffers zurückgeben, um den managed Heap-Druck zu reduzieren.
        foreach (var row in matchingRows)
            RowCodec.ReturnPooledArray(row);

        FireTriggers(delete.TableName, SqlTriggerEvent.Delete, SqlTriggerTiming.After);
        return WalhallaResultSet.Affected(matchingRows.Count);
    }

    private WalhallaResultSet ExecuteDelete(SqlDeleteStatement delete, WalhallaSqlTransaction? transaction)
    {
        if (transaction == null)
            return ExecuteDelete(delete);
        return ExecuteDeleteBuffered(delete, transaction);
    }

    // -- Transaction-buffered write methods ------------------------------------

    private WalhallaResultSet ExecuteInsertBuffered(SqlInsertStatement insert, WalhallaSqlTransaction tx)
    {
        var tableDef = _store.GetTableDefinition(insert.TableName)
            ?? throw new WalhallaException($"Table '{insert.TableName}' not found.");

        var tableId = _store.GetTableId(insert.TableName);

        var colIndexMap = new Dictionary<int, int>();
        for (int i = 0; i < insert.Columns.Count; i++)
        {
            var idx = FindColumnIndex(tableDef, insert.Columns[i]);
            if (idx < 0)
                throw new WalhallaException($"Column '{insert.Columns[i]}' not found in table '{insert.TableName}'.");
            colIndexMap[i] = idx;
        }

        // Build index metadata once for all rows
        var indexMetas = GetOrBuildIndexMetadata(insert.TableName, tableDef);

        // Reserve row IDs (handles both synthetic and alias-PK case)
        bool isRowIdAlias = tableDef.TryGetRowIdAliasPk(out var aliasPkIdx);
        var startRowId = isRowIdAlias ? 0 : _store.GetNextRowId(tableId);
        if (!isRowIdAlias)
        {
            for (int i = 0; i < insert.ValueRows.Count; i++)
                _store.AdvanceNextRowId(tableId);
        }

        // Decode all rows first so FK enforcement can see the full batch
        var decodedRows = new List<object?[]>(insert.ValueRows.Count);
        for (int ri = 0; ri < insert.ValueRows.Count; ri++)
        {
            var valueRow = insert.ValueRows[ri];
            var rowValues = new object?[tableDef.Columns.Count];
            for (int ci = 0; ci < insert.Columns.Count; ci++)
            {
                var type = tableDef.Columns[colIndexMap[ci]].Type;
                rowValues[colIndexMap[ci]] = ParseLiteral(valueRow[ci], type);
            }
            decodedRows.Add(rowValues);
        }

        // FK + CHECK enforcement for buffered inserts (pass full batch so self-referencing FKs resolve)
        foreach (var row in decodedRows)
        {
            EnforceForeignKeyInsert(tableDef, row, decodedRows, tx);
            CheckConstraintEvaluator.Enforce(tableDef, row);
        }

        var affected = 0;
        for (int ri = 0; ri < decodedRows.Count; ri++)
        {
            var rowValues = decodedRows[ri];

            var encoded = EncodeRowWithBlobs(tableId, rowValues, tableDef);
            var rowId = isRowIdAlias
                ? Convert.ToInt64(rowValues[aliasPkIdx]
                    ?? throw new WalhallaException(
                        $"PRIMARY KEY column '{tableDef.PrimaryKeyColumns[0].Name}' in table '{insert.TableName}' cannot be NULL."))
                : startRowId + ri;

            if (!UseMvcc)
                _store.LockManager.AcquireRowExclusive(tx, tableId, rowId);

            // Check unique constraints against committed state + transaction buffer
            foreach (var meta in indexMetas)
                CheckUniqueConstraintBuffered(meta, tableId, rowId, rowValues, tx);

            tx.BufferInsert(tableId, rowId, encoded);

            // Buffer index entries
            foreach (var meta in indexMetas)
            {
                if (meta.Definition.IndexType == SqlIndexType.Gin)
                {
                    var jsonbColIdx = meta.ColumnIndices[0];
                    var jsonbValue = rowValues[jsonbColIdx];
                    if (jsonbValue != null && jsonbValue != DBNull.Value)
                    {
                        var elements = GinElementExtractor.ExtractElements(jsonbValue);
                        foreach (var element in elements)
                            tx.BufferIndexInsert(meta.IndexId, element, tableId, rowId);
                    }
                }
                else
                {
                    var sortKey = IndexKeyCodec.BuildIndexKey(rowValues, meta.ColumnIndices, meta.KeyTypes);
                    tx.BufferIndexInsert(meta.IndexId, sortKey, tableId, rowId);
                }
            }

            affected++;
        }

        return WalhallaResultSet.Affected(affected);
    }

    private WalhallaResultSet ExecuteInsertSelect(SqlInsertSelectStatement insertSelect, WalhallaSqlTransaction? transaction)
    {
        if (transaction == null)
            return ExecuteInsertSelect(insertSelect);
        return ExecuteInsertSelectBuffered(insertSelect, transaction);
    }

    private WalhallaResultSet ExecuteInsertSelectBuffered(SqlInsertSelectStatement insertSelect, WalhallaSqlTransaction tx)
    {
        var tableDef = _store.GetTableDefinition(insertSelect.TableName)
            ?? throw new WalhallaException($"Table '{insertSelect.TableName}' not found.");

        var tableId = _store.GetTableId(insertSelect.TableName);

        var colIndexMap = new Dictionary<int, int>();
        for (int i = 0; i < insertSelect.Columns.Count; i++)
        {
            var idx = FindColumnIndex(tableDef, insertSelect.Columns[i]);
            if (idx < 0)
                throw new WalhallaException($"Column '{insertSelect.Columns[i]}' not found in table '{insertSelect.TableName}'.");
            colIndexMap[i] = idx;
        }

        // Execute the SELECT (transaction-aware, so sees buffered writes)
        var result = ExecuteSelect(insertSelect.SelectStatement, tx);

        if (result.Rows.Count == 0)
            return WalhallaResultSet.Affected(0);

        bool isRowIdAliasIS = tableDef.TryGetRowIdAliasPk(out var aliasPkIdxIS);
        var startRowId = isRowIdAliasIS ? 0 : _store.GetNextRowId(tableId);
        if (!isRowIdAliasIS)
        {
            for (int i = 0; i < result.Rows.Count; i++)
                _store.AdvanceNextRowId(tableId);
        }

        var indexMetas = GetOrBuildIndexMetadata(insertSelect.TableName, tableDef);

        // Decode all rows first so FK enforcement can see the full batch
        var decodedRows = new List<object?[]>(result.Rows.Count);
        for (int ri = 0; ri < result.Rows.Count; ri++)
        {
            var sourceRow = result.Rows[ri];
            var sourceValues = sourceRow.Values.ToArray();
            var rowValues = new object?[tableDef.Columns.Count];
            for (int ci = 0; ci < insertSelect.Columns.Count && ci < sourceValues.Length; ci++)
            {
                var type = tableDef.Columns[colIndexMap[ci]].Type;
                rowValues[colIndexMap[ci]] = ConvertValue(sourceValues[ci], type);
            }
            decodedRows.Add(rowValues);
        }

        // FK + CHECK enforcement for buffered inserts (pass full batch so self-referencing FKs resolve)
        foreach (var row in decodedRows)
        {
            EnforceForeignKeyInsert(tableDef, row, decodedRows, tx);
            CheckConstraintEvaluator.Enforce(tableDef, row);
        }

        var affected = 0;
        for (int ri = 0; ri < decodedRows.Count; ri++)
        {
            var rowValues = decodedRows[ri];

            var encoded = EncodeRowWithBlobs(tableId, rowValues, tableDef);
            var rowId = isRowIdAliasIS
                ? Convert.ToInt64(rowValues[aliasPkIdxIS]
                    ?? throw new WalhallaException(
                        $"PRIMARY KEY column '{tableDef.PrimaryKeyColumns[0].Name}' in table '{insertSelect.TableName}' cannot be NULL."))
                : startRowId + ri;

            if (!UseMvcc)
                _store.LockManager.AcquireRowExclusive(tx, tableId, rowId);

            foreach (var meta in indexMetas)
                CheckUniqueConstraintBuffered(meta, tableId, rowId, rowValues, tx);

            tx.BufferInsert(tableId, rowId, encoded);

            foreach (var meta in indexMetas)
            {
                if (meta.Definition.IndexType == SqlIndexType.Gin)
                {
                    var jsonbColIdx = meta.ColumnIndices[0];
                    var jsonbValue = rowValues[jsonbColIdx];
                    if (jsonbValue != null && jsonbValue != DBNull.Value)
                    {
                        var elements = GinElementExtractor.ExtractElements(jsonbValue);
                        foreach (var element in elements)
                            tx.BufferIndexInsert(meta.IndexId, element, tableId, rowId);
                    }
                }
                else
                {
                    var sortKey = IndexKeyCodec.BuildIndexKey(rowValues, meta.ColumnIndices, meta.KeyTypes);
                    tx.BufferIndexInsert(meta.IndexId, sortKey, tableId, rowId);
                }
            }

            affected++;
        }

        return WalhallaResultSet.Affected(affected);
    }

    private WalhallaResultSet ExecuteUpdate(SqlUpdateStatement update, WalhallaSqlTransaction? transaction)
    {
        if (transaction == null)
            return ExecuteUpdate(update);
        return ExecuteUpdateBuffered(update, transaction);
    }

    private WalhallaResultSet ExecuteUpdateBuffered(SqlUpdateStatement update, WalhallaSqlTransaction tx)
    {
        var tableDef = _store.GetTableDefinition(update.TableName)
            ?? throw new WalhallaException($"Table '{update.TableName}' not found.");

        var tableId = _store.GetTableId(update.TableName);

        // Build index metadata once for the entire update
        var indexMetas = GetOrBuildIndexMetadata(update.TableName, tableDef);

        // PK fast path: WHERE PK = constant
        var pkConst = TryExtractPkLiteral(update.Where, tableDef);
        if (pkConst != null)
        {
            var rowId = Convert.ToInt64(pkConst);

            if (!UseMvcc)
                _store.LockManager.AcquireRowExclusive(tx, tableId, rowId);

            // Check transaction buffer first, then store
            byte[]? encoded;
            if (!tx.TryGetBufferedRow(tableId, rowId, out encoded) || encoded == null)
                encoded = _store.GetRow(tableId, rowId);

            if (encoded == null)
                return WalhallaResultSet.Affected(0);

            var row = RowCodec.DecodeToArray(encoded, tableDef);
            var oldRow = new object?[row.Length];
            Array.Copy(row, oldRow, row.Length);

            foreach (var kv in update.Assignments)
            {
                var colIdx = FindColumnIndex(tableDef, kv.Key);
                if (colIdx < 0) continue;
                row[colIdx] = ParseLiteral(kv.Value, tableDef.Columns[colIdx].Type);
            }

            EnforceForeignKeyUpdate(tableDef, oldRow, row);
            CheckConstraintEvaluator.Enforce(tableDef, row);

            var newEncoded = EncodeRowWithBlobs(tableId, row, tableDef);
            tx.BufferUpdate(tableId, rowId, newEncoded);

            // Buffered index maintenance
            foreach (var meta in indexMetas)
            {
                if (meta.Definition.IndexType == SqlIndexType.Gin)
                {
                    var jsonbColIdx = meta.ColumnIndices[0];
                    var oldValue = oldRow[jsonbColIdx];
                    var newValue = row[jsonbColIdx];
                    if (oldValue != null && oldValue != DBNull.Value)
                    {
                        var oldElements = GinElementExtractor.ExtractElements(oldValue);
                        foreach (var element in oldElements)
                            tx.BufferIndexDelete(meta.IndexId, element, tableId, rowId);
                    }
                    if (newValue != null && newValue != DBNull.Value)
                    {
                        var newElements = GinElementExtractor.ExtractElements(newValue);
                        foreach (var element in newElements)
                            tx.BufferIndexInsert(meta.IndexId, element, tableId, rowId);
                    }
                }
                else
                {
                    var oldSortKey = IndexKeyCodec.BuildIndexKey(oldRow, meta.ColumnIndices, meta.KeyTypes);
                    var newSortKey = IndexKeyCodec.BuildIndexKey(row, meta.ColumnIndices, meta.KeyTypes);
                    if (!ByteArrayComparer.Instance.Equals(oldSortKey, newSortKey))
                    {
                        tx.BufferIndexDelete(meta.IndexId, oldSortKey, tableId, rowId);
                        tx.BufferIndexInsert(meta.IndexId, newSortKey, tableId, rowId);
                    }
                }
            }

            return WalhallaResultSet.Affected(1);
        }

        // Build WHERE delegate and find matching rows
        Func<object?[], bool>? predicate = null;
        if (update.Where != null)
        {
            var whereDelegate = WhereCompiler.Compile(update.Where, tableDef, 0);
            if (whereDelegate != null)
            {
                var emptyParams = Array.Empty<object?>();
                predicate = row => whereDelegate(row, emptyParams);
            }
        }

        var matchingRows = new List<object?[]>();
        var matchingRowIds = new List<long>();
        RowDecoder decoder = encoded => RowCodec.DecodeToArray(encoded, tableDef);
        _store.ScanWithPredicateFirst(tableId, tableDef,
            update.Where?.CollectColumnIndices(tableDef) ?? Array.Empty<int>(),
            decoder, predicate, matchingRows, int.MaxValue, matchingRowIds);

        var affected = 0;
        for (var i = 0; i < matchingRows.Count; i++)
        {
            var row = matchingRows[i];
            var rowId = matchingRowIds[i];

            if (!UseMvcc)
                _store.LockManager.AcquireRowExclusive(tx, tableId, rowId);

            var oldRow = new object?[row.Length];
            Array.Copy(row, oldRow, row.Length);

            foreach (var kv in update.Assignments)
            {
                var colIdx = FindColumnIndex(tableDef, kv.Key);
                if (colIdx < 0) continue;
                row[colIdx] = ParseLiteral(kv.Value, tableDef.Columns[colIdx].Type);
            }

            EnforceForeignKeyUpdate(tableDef, oldRow, row);
            CheckConstraintEvaluator.Enforce(tableDef, row);

            var newEncoded = EncodeRowWithBlobs(tableId, row, tableDef);
            tx.BufferUpdate(tableId, rowId, newEncoded);

            // Buffered index maintenance
            foreach (var meta in indexMetas)
            {
                if (meta.Definition.IndexType == SqlIndexType.Gin)
                {
                    var jsonbColIdx = meta.ColumnIndices[0];
                    var oldValue = oldRow[jsonbColIdx];
                    var newValue = row[jsonbColIdx];
                    if (oldValue != null && oldValue != DBNull.Value)
                    {
                        var oldElements = GinElementExtractor.ExtractElements(oldValue);
                        foreach (var element in oldElements)
                            tx.BufferIndexDelete(meta.IndexId, element, tableId, rowId);
                    }
                    if (newValue != null && newValue != DBNull.Value)
                    {
                        var newElements = GinElementExtractor.ExtractElements(newValue);
                        foreach (var element in newElements)
                            tx.BufferIndexInsert(meta.IndexId, element, tableId, rowId);
                    }
                }
                else
                {
                    var oldSortKey = IndexKeyCodec.BuildIndexKey(oldRow, meta.ColumnIndices, meta.KeyTypes);
                    var newSortKey = IndexKeyCodec.BuildIndexKey(row, meta.ColumnIndices, meta.KeyTypes);
                    if (!ByteArrayComparer.Instance.Equals(oldSortKey, newSortKey))
                    {
                        tx.BufferIndexDelete(meta.IndexId, oldSortKey, tableId, rowId);
                        tx.BufferIndexInsert(meta.IndexId, newSortKey, tableId, rowId);
                    }
                }
            }

            affected++;
        }

        return WalhallaResultSet.Affected(affected);
    }

    private WalhallaResultSet ExecuteDeleteBuffered(SqlDeleteStatement delete, WalhallaSqlTransaction tx)
    {
        var tableDef = _store.GetTableDefinition(delete.TableName)
            ?? throw new WalhallaException($"Table '{delete.TableName}' not found.");

        var tableId = _store.GetTableId(delete.TableName);

        var indexMetas = GetOrBuildIndexMetadata(delete.TableName, tableDef);

        // PK fast path: WHERE PK = constant
        var pkConst = TryExtractPkLiteral(delete.Where, tableDef);
        if (pkConst != null)
        {
            var rowId = Convert.ToInt64(pkConst);

            if (!UseMvcc)
                _store.LockManager.AcquireRowExclusive(tx, tableId, rowId);

            // Read the row to build index sort keys for deletion
            byte[]? encoded;
            if (!tx.TryGetBufferedRow(tableId, rowId, out encoded) || encoded == null)
                encoded = _store.GetRow(tableId, rowId);

            if (encoded == null)
                return WalhallaResultSet.Affected(0);

            var row = RowCodec.DecodeToArray(encoded, tableDef);
            foreach (var meta in indexMetas)
            {
                var sortKey = IndexKeyCodec.BuildIndexKey(row, meta.ColumnIndices, meta.KeyTypes);
                tx.BufferIndexDelete(meta.IndexId, sortKey, tableId, rowId);
            }

            tx.BufferDelete(tableId, rowId);
            return WalhallaResultSet.Affected(1);
        }

        Func<object?[], bool>? predicate = null;
        if (delete.Where != null)
        {
            var whereDelegate = WhereCompiler.Compile(delete.Where, tableDef, 0);
            if (whereDelegate != null)
            {
                var emptyParams = Array.Empty<object?>();
                predicate = row => whereDelegate(row, emptyParams);
            }
        }

        var matchingRows = new List<object?[]>();
        var matchingRowIds = new List<long>();
        RowDecoder decoder = encoded => RowCodec.DecodeToArray(encoded, tableDef);
        _store.ScanWithPredicateFirst(tableId, tableDef,
            delete.Where?.CollectColumnIndices(tableDef) ?? Array.Empty<int>(),
            decoder, predicate, matchingRows, int.MaxValue, matchingRowIds);

        var affected = 0;
        for (var i = 0; i < matchingRows.Count; i++)
        {
            var row = matchingRows[i];
            var rowId = matchingRowIds[i];

            if (!UseMvcc)
                _store.LockManager.AcquireRowExclusive(tx, tableId, rowId);

            foreach (var meta in indexMetas)
            {
                var sortKey = IndexKeyCodec.BuildIndexKey(row, meta.ColumnIndices, meta.KeyTypes);
                tx.BufferIndexDelete(meta.IndexId, sortKey, tableId, rowId);
            }

            tx.BufferDelete(tableId, rowId);
            affected++;
        }

        return WalhallaResultSet.Affected(affected);
    }

    // -- Transaction-aware SELECT ----------------------------------------------

    private WalhallaResultSet ExecuteSelect(SqlSelectStatement select, WalhallaSqlTransaction? transaction, string? planCacheKey = null)
    {
        if (transaction == null)
            return ExecuteSelect(select, planCacheKey);

        bool hasAggregates = select.Columns.Any(c => c.Aggregate != null && c.WindowFunction == null);
        if (hasAggregates && transaction.Writes.Count > 0)
        {
            // Aggregate queries cannot be merged into an already-aggregated result.
            // Re-run on raw merged rows: select all columns with the same WHERE,
            // let the normal transaction path merge committed + transaction rows,
            // then compute the aggregate over the merged raw rows.
            var tableDef = _store.GetTableDefinition(select.TableName);
            if (tableDef != null && select.Joins is not { Count: > 0 })
            {
                var allColumns = tableDef.Columns.Select(c => new SqlSelectColumn(c.Name, null)).ToList();
                var rawSelect = select with
                {
                    Columns = allColumns,
                    GroupByColumns = null,
                    Having = null,
                    OrderBy = null,
                    Limit = null,
                    Offset = null,
                    IsDistinct = false
                };

                var rawResult = ExecuteSelect(rawSelect, transaction, planCacheKey: null);
                var rawRows = rawResult.Rows.Select(r => r.Values is object?[] a ? a : r.Values.ToArray()).ToList();

                var plan = QueryPlanner.Build(select, _store, ResolveSubquery, _statisticsCatalog);
                var aggregated = AggregateExecutor.ExecuteGroupBy(rawRows, select.GroupByColumns, select.Columns, plan.TableDefinition, plan.OutputColumnNames);
                aggregated = AggregateExecutor.ApplyHaving(aggregated, select.Having, plan.OutputColumnNames);

                var schema = new ColumnSchema(plan.OutputColumnNames);
                var resultRows = aggregated.ConvertAll(r => new WalhallaRow(schema, r));
                return new WalhallaResultSet(resultRows, plan.OutputColumnNames);
            }
        }

        // Execute with snapshot-aware point reads
        var result = ExecuteSelect(select, storageTx: transaction.StorageTransaction);

        // If the transaction has no writes affecting this table, return the snapshot result directly
        if (transaction.Writes.Count == 0)
            return result;

        // Post-process: remove rows deleted by this transaction,
        // replace rows updated by this transaction,
        // append rows inserted by this transaction.
        if (result.Rows.Count == 0 && select.Where == null && select.Joins is { Count: 0 })
        {
            // Simple full-table scan with no results — check for buffered inserts
            return MergeTransactionInserts(result, select, transaction);
        }

        return MergeTransactionWrites(result, select, transaction);
    }

    private WalhallaResultSet MergeTransactionWrites(
        WalhallaResultSet committed, SqlSelectStatement select, WalhallaSqlTransaction tx)
    {
        var tableId = -1;
        SqlTableDefinition? tableDef = null;

        // Resolve table (skip views)
        bool isView;
        lock (_metaSync)
            isView = _views.ContainsKey(select.TableName);
        if (!isView)
        {
            tableId = _store.GetTableId(select.TableName);
            tableDef = _store.GetTableDefinition(select.TableName);
        }

        if (tableId < 0 || tableDef == null)
            return committed; // Can't merge � views, CTEs, joins: return as-is

        var pkIdx = GetPrimaryKeyIndex(tableDef);

        // If the result doesn't include the PK column, we can't merge buffered writes
        if (pkIdx < 0 || pkIdx >= committed.ColumnNames.Count)
            return committed;

        // Filter committed rows: remove deleted, replace updated, keep rest
        var filteredRows = new List<object?[]>();
        foreach (var row in committed.Rows)
        {
            if (pkIdx >= row.Count) continue;
            var pkValue = row.GetValue(pkIdx);
            if (!IsNumericLiteralValue(pkValue))
            {
                // PK-Spalte ist nicht numerisch (z. B. Table-Splitting mit String-Spalten)
                // Transaktions-Merge ist hier nicht anwendbar; Zeile unverändert übernehmen.
                var srcArr = row.Values is object?[] a ? a : row.Values.ToArray();
                var pooledCommitted = System.Buffers.ArrayPool<object?>.Shared.Rent(srcArr.Length);
                Array.Copy(srcArr, 0, pooledCommitted, 0, srcArr.Length);
                filteredRows.Add(pooledCommitted);
                continue;
            }

            var rowId = Convert.ToInt64(pkValue);
            if (rowId == 0) continue;

            if (tx.IsDeleted(tableId, rowId))
                continue; // row was deleted in this transaction

            if (tx.TryGetBufferedRow(tableId, rowId, out var buffered) && buffered != null)
            {
                // Row was updated � decode the buffered version and project
                var decoded = RowCodec.DecodeToPooledArray(buffered, tableDef);
                filteredRows.Add(decoded);
            }
            else
            {
                // Use committed row � copy into a pooled array so ApplyPostProcessing can return it
                var srcArr = row.Values is object?[] a ? a : row.Values.ToArray();
                var pooledCommitted = System.Buffers.ArrayPool<object?>.Shared.Rent(srcArr.Length);
                Array.Copy(srcArr, 0, pooledCommitted, 0, srcArr.Length);
                filteredRows.Add(pooledCommitted);
            }
        }

        // Append buffered inserts for this table (if they match WHERE clause)
        var insertedRows = tx.GetInsertedRows(tableId);
        foreach (var (rowId, encoded) in insertedRows)
        {
            var decoded = RowCodec.DecodeToPooledArray(encoded, tableDef);

            // Apply WHERE filter if present
            if (select.Where != null)
            {
                var whereDelegate = WhereCompiler.Compile(select.Where, tableDef, 0);
                if (whereDelegate != null)
                {
                    var emptyParams = Array.Empty<object?>();
                    if (!whereDelegate(decoded, emptyParams))
                        continue;
                }
            }

            filteredRows.Add(decoded);
        }

        // Re-apply ORDER BY, projection, paging
        var plan = QueryPlanner.Build(select, _store, ResolveSubquery, _statisticsCatalog);
        return ApplyPostProcessing(filteredRows, plan, select);
    }

    private WalhallaResultSet MergeTransactionInserts(
        WalhallaResultSet committed, SqlSelectStatement select, WalhallaSqlTransaction tx)
    {
        // Only for simple table scans with no committed results
        var tableId = _store.GetTableId(select.TableName);
        var tableDef = _store.GetTableDefinition(select.TableName);
        if (tableId < 0 || tableDef == null)
            return committed;

        var pkIdx = GetPrimaryKeyIndex(tableDef);
        var insertedRows = tx.GetInsertedRows(tableId);
        if (insertedRows.Count == 0)
            return committed;

        var rows = new List<object?[]>();
        foreach (var (rowId, encoded) in insertedRows)
        {
            var decoded = RowCodec.DecodeToPooledArray(encoded, tableDef);
            if (select.Where != null)
            {
                var whereDelegate = WhereCompiler.Compile(select.Where, tableDef, 0);
                if (whereDelegate != null)
                {
                    var emptyParams = Array.Empty<object?>();
                    if (!whereDelegate(decoded, emptyParams))
                    {
                        RowCodec.ReturnPooledArray(decoded);
                        continue;
                    }
                }
            }
            rows.Add(decoded);
        }

        if (rows.Count == 0)
            return committed;

        var plan = QueryPlanner.Build(select, _store, ResolveSubquery, _statisticsCatalog);
        return ApplyPostProcessing(rows, plan, select);
    }

    private void CheckUniqueConstraintBuffered(IndexMeta meta, int tableId, long rowId, object?[] row, WalhallaSqlTransaction tx)
    {
        if (!meta.Definition.IsUnique || meta.Definition.IndexType == SqlIndexType.Gin) return;
        if (IsKeyAllNulls(row, meta.ColumnIndices)) return;

        var sortKey = IndexKeyCodec.BuildIndexKey(row, meta.ColumnIndices, meta.KeyTypes);
        // Check committed state
        if (_store.IndexEntryExists(meta.IndexId, sortKey, tableId, rowId))
            throw new WalhallaException(
                $"UNIQUE constraint violation on index '{meta.Definition.IndexName}'.");

        // Also check buffered inserts in this transaction
        // (Simplified: we trust the caller to manage this for now)
    }

    // -- Helpers ----------------------------------------------------------------

    private static bool IsWhereOnlyOnPrimaryKey(SqlWhereExpression where, SqlTableDefinition tableDef)
    {
        var pkIndices = new HashSet<int>();
        foreach (var col in tableDef.PrimaryKeyColumns)
        {
            var idx = FindColumnIndex(tableDef, col.Name);
            if (idx >= 0) pkIndices.Add(idx);
        }

        var whereColIndices = where.CollectColumnIndices(tableDef);
        foreach (var idx in whereColIndices)
            if (!pkIndices.Contains(idx))
                return false;

        return true;
    }

    private static object? TryExtractPkLiteral(SqlWhereExpression? where, SqlTableDefinition tableDef)
    {
        // PK==RowId fast path is only valid for SQLite-style INTEGER PRIMARY KEY alias tables.
        if (!tableDef.TryGetRowIdAliasPk(out _)) return null;

        if (where is not SqlWhereComparisonExpression cmp
            || cmp.Operator != SqlWhereComparisonOperator.Equal)
            return null;

        if (cmp.Left is not SqlWhereColumnExpression colExpr)
            return null;

        var pkCols = tableDef.PrimaryKeyColumns;
        if (pkCols.Count != 1) return null;

        if (!string.Equals(colExpr.SimpleName, pkCols[0].Name, StringComparison.OrdinalIgnoreCase))
            return null;

        var value = cmp.Right is SqlWhereLiteralExpression lit ? lit.Value : null;
        if (value == null)
            return null;

        // Der RowId-Alias-Pfad verlangt einen numerischen Literalwert, weil der PK-Wert
        // hier direkt die physische Storage-RowId ist. Nicht-numerische Literale
        // (z. B. Guid-Spalten mit string-Konverter) müssen den Scan-Pfad nutzen.
        if (!IsNumericLiteralValue(value))
            return null;

        return value;
    }

    private static bool IsNumericLiteralValue(object? value)
    {
        return value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;
    }

    private static int FindColumnIndex(SqlTableDefinition table, string name)
    {
        for (int i = 0; i < table.Columns.Count; i++)
        {
            if (string.Equals(table.Columns[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private static string[] ParseKeyList(string jsonArray)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(jsonArray);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var keys = new List<string>();
                foreach (var elem in doc.RootElement.EnumerateArray())
                {
                    var str = elem.GetString();
                    if (str != null) keys.Add(str);
                }
                return keys.ToArray();
            }
        }
        catch { }
        // Fallback: single key
        return new[] { jsonArray.Trim('"', '\'').Trim() };
    }

    private static int GetPrimaryKeyIndex(SqlTableDefinition table)
    {
        // Prefer explicit PK columns, then "Id" column, else first column
        foreach (var col in table.Columns)
        {
            if (col.IsPrimaryKey)
                return FindColumnIndex(table, col.Name);
        }
        var idIdx = FindColumnIndex(table, "Id");
        if (idIdx >= 0) return idIdx;
        return 0;
    }

    // -- Index maintenance helpers ------------------------------------------

    internal struct IndexMeta
    {
        public SqlIndexDefinition Definition;
        public int IndexId;
        public int[] ColumnIndices;
        public SqlScalarType[] KeyTypes;
    }

    private IndexMeta[] GetOrBuildIndexMetadata(string tableName, SqlTableDefinition tableDef)
    {
        var entry = _store.GetEntry(tableName);
        if (entry?.CachedIndexMeta is { } cached)
            return cached;

        var metas = BuildIndexMetadata(tableDef);
        FillIndexIds(tableDef, metas);
        if (entry != null)
            entry.CachedIndexMeta = metas;
        return metas;
    }

    private static IndexMeta[] BuildIndexMetadata(SqlTableDefinition tableDef)
    {
        // This only works after the table has been created through TableStore
        // (which assigns indexIds). For tables that haven't been persisted yet,
        // indexes are empty anyway.
        var indexes = tableDef.Indexes;
        var result = new IndexMeta[indexes.Count];
        for (int i = 0; i < indexes.Count; i++)
        {
            var idx = indexes[i];
            var colIndices = new int[idx.ColumnNames.Count];
            var keyTypes = new SqlScalarType[idx.ColumnNames.Count];
            for (int j = 0; j < idx.ColumnNames.Count; j++)
            {
                colIndices[j] = FindColumnIndex(tableDef, idx.ColumnNames[j]);
                keyTypes[j] = tableDef.Columns[colIndices[j]].Type;
            }
            result[i] = new IndexMeta
            {
                Definition = idx,
                IndexId = 0, // will be filled by caller using TableStore
                ColumnIndices = colIndices,
                KeyTypes = keyTypes
            };
        }
        return result;
    }

    private void FillIndexIds(SqlTableDefinition tableDef, IndexMeta[] metas)
    {
        for (int i = 0; i < metas.Length; i++)
        {
            metas[i].IndexId = _store.GetIndexId(tableDef.CollectionName, metas[i].Definition.IndexName);
        }
    }

    private void MaintainIndexesForUpdate(
        SqlTableDefinition tableDef, int tableId, long rowId,
        object?[] oldRow, object?[] newRow, IndexMeta[] indexMetas)
    {
        foreach (var meta in indexMetas)
        {
            if (meta.Definition.IndexType == SqlIndexType.Gin)
            {
                // GIN: delete old elements, insert new elements.
                var jsonbColIdx = meta.ColumnIndices[0];
                var oldValue = oldRow[jsonbColIdx];
                var newValue = newRow[jsonbColIdx];

                if (oldValue != null && oldValue != DBNull.Value)
                {
                    var oldElements = GinElementExtractor.ExtractElements(oldValue);
                    var oldEntries = new (int IndexId, byte[] SortKey, int TableId, long RowId)[oldElements.Length];
                    for (int i = 0; i < oldElements.Length; i++)
                        oldEntries[i] = (meta.IndexId, oldElements[i], tableId, rowId);
                    _store.DeleteIndexEntries(oldEntries);
                }

                if (newValue != null && newValue != DBNull.Value)
                {
                    var newElements = GinElementExtractor.ExtractElements(newValue);
                    var newEntries = new (byte[] SortKey, int TableId, long RowId)[newElements.Length];
                    for (int i = 0; i < newElements.Length; i++)
                        newEntries[i] = (newElements[i], tableId, rowId);
                    _store.InsertIndexEntries(meta.IndexId, newEntries);
                }
            }
            else
            {
                var oldSortKey = IndexKeyCodec.BuildIndexKey(oldRow, meta.ColumnIndices, meta.KeyTypes);
                var newSortKey = IndexKeyCodec.BuildIndexKey(newRow, meta.ColumnIndices, meta.KeyTypes);

                // Only update if the key changed.
                if (ByteArrayComparer.Instance.Compare(oldSortKey, newSortKey) != 0)
                {
                    CheckUniqueConstraint(meta, tableId, rowId, newRow);
                    _store.UpdateIndexEntry(meta.IndexId, oldSortKey, newSortKey, tableId, rowId);
                }
            }
        }
    }

    private void MaintainIndexesForDelete(
        SqlTableDefinition tableDef, int tableId, long rowId,
        object?[] row, IndexMeta[] indexMetas)
    {
        foreach (var meta in indexMetas)
        {
            if (meta.Definition.IndexType == SqlIndexType.Gin)
            {
                var jsonbColIdx = meta.ColumnIndices[0];
                var value = row[jsonbColIdx];
                if (value != null && value != DBNull.Value)
                {
                    var elements = GinElementExtractor.ExtractElements(value);
                    var entries = new (int IndexId, byte[] SortKey, int TableId, long RowId)[elements.Length];
                    for (int i = 0; i < elements.Length; i++)
                        entries[i] = (meta.IndexId, elements[i], tableId, rowId);
                    _store.DeleteIndexEntries(entries);
                }
            }
            else
            {
                var sortKey = IndexKeyCodec.BuildIndexKey(row, meta.ColumnIndices, meta.KeyTypes);
                _store.DeleteIndexEntry(meta.IndexId, sortKey, tableId, rowId);
            }
        }
    }

    private void CheckUniqueConstraint(IndexMeta meta, int tableId, long rowId, object?[] row)
    {
        if (!meta.Definition.IsUnique || meta.Definition.IndexType == SqlIndexType.Gin) return;
        if (IsKeyAllNulls(row, meta.ColumnIndices)) return;

        var sortKey = IndexKeyCodec.BuildIndexKey(row, meta.ColumnIndices, meta.KeyTypes);
        if (_store.IndexEntryExists(meta.IndexId, sortKey, tableId, rowId))
            throw new WalhallaException(
                $"UNIQUE constraint violation on index '{meta.Definition.IndexName}'.");
    }

    private static bool IsKeyAllNulls(object?[] row, int[] colIndices)
    {
        foreach (var colIndex in colIndices)
        {
            if (row[colIndex] != null && row[colIndex] != DBNull.Value)
                return false;
        }
        return true;
    }

    internal static object? ParseLiteral(string text, SqlScalarType type)
    {
        text = text.Trim();
        if (text.Equals("NULL", StringComparison.OrdinalIgnoreCase))
            return null;

        // Hex binary literal: X'...' or x'...'
        if (type == SqlScalarType.Binary && text.Length >= 3
            && (text[0] == 'X' || text[0] == 'x')
            && text[1] == '\'')
        {
            var hex = text[2..^1];
            if ((hex.Length & 1) != 0)
                throw new FormatException("Hex binary literal must have an even number of digits.");
            return Convert.FromHexString(hex);
        }

        // Strip surrounding single quotes for string-like types
        if (text.Length >= 2 && text[0] == '\'' && text[^1] == '\'')
            text = text[1..^1].Replace("''", "'");

        return type switch
        {
            SqlScalarType.Int32 => int.Parse(text, CultureInfo.InvariantCulture),
            SqlScalarType.Int64 => long.Parse(text, CultureInfo.InvariantCulture),
            SqlScalarType.Double => double.Parse(text, CultureInfo.InvariantCulture),
            SqlScalarType.Decimal => decimal.Parse(text, CultureInfo.InvariantCulture),
            SqlScalarType.Boolean => ParseBooleanLiteral(text),
            SqlScalarType.DateTime => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            SqlScalarType.Guid => Guid.Parse(text),
            SqlScalarType.String => text,
            SqlScalarType.Binary => Encoding.UTF8.GetBytes(text),
            _ => text
        };
    }

    private static bool ParseBooleanLiteral(string text)
    {
        if (text.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        if (text == "1") return true;
        if (text == "0") return false;
        throw new FormatException($"String '{text}' was not recognized as a valid Boolean.");
    }

    // -- Stored Procedure / Trigger Execution ------------------------------------

    private WalhallaResultSet ExecuteCreateProcedure(SqlCreateProcedureStatement stmt)
    {
        lock (_metaSync)
        {
            if (_procedures.ContainsKey(stmt.ProcedureName) && !stmt.OrReplace)
                throw new WalhallaException(
                    $"Procedure '{stmt.ProcedureName}' already exists. Use OR REPLACE to overwrite.");

            var proc = new SqlStoredProcedureDefinition(
                stmt.ProcedureName, stmt.Parameters, stmt.Body, stmt.Language);
            _procedures[stmt.ProcedureName] = proc;
            _compiledProcedures.Remove(stmt.ProcedureName);
        }
        return WalhallaResultSet.Affected(0);
    }

    private WalhallaResultSet ExecuteDropProcedure(SqlDropProcedureStatement stmt)
    {
        lock (_metaSync)
        {
            if (!_procedures.Remove(stmt.ProcedureName) && !stmt.IfExists)
                throw new WalhallaException($"Procedure '{stmt.ProcedureName}' not found.");
            _compiledProcedures.Remove(stmt.ProcedureName);
        }
        return WalhallaResultSet.Affected(0);
    }

    private WalhallaResultSet ExecuteExec(SqlExecStatement exec)
    {
        SqlStoredProcedureDefinition? proc;
        lock (_metaSync)
        {
            if (!_procedures.TryGetValue(exec.ProcedureName, out proc))
                throw new WalhallaException($"Procedure '{exec.ProcedureName}' not found.");
        }

        if (string.Equals(proc.Language, "csharp", StringComparison.OrdinalIgnoreCase))
            return ExecuteCSharpProcedure(proc, exec.Arguments);

        // Bind parameters into body text
        var body = BindProcedureBody(proc, exec.Arguments);
        return ExecuteProcedureBody(body);
    }

    private WalhallaResultSet ExecuteCSharpProcedure(
        SqlStoredProcedureDefinition proc,
        IReadOnlyList<SqlExecArgument> args)
    {
        Func<SqlNativeProcedureContext, WalhallaResultSet>? compiled;
        lock (_metaSync)
            _compiledProcedures.TryGetValue(proc.Name, out compiled);

        if (compiled == null)
        {
            try
            {
                compiled = CSharpProcedureCompiler.Compile(proc);
            }
            catch (Exception ex) when (ex is not WalhallaException)
            {
                throw new WalhallaException(
                    $"C# stored procedure '{proc.Name}': {ex.Message}", ex);
            }
            lock (_metaSync)
                _compiledProcedures[proc.Name] = compiled;
        }

        var bound = BindCSharpArguments(proc, args);
        var ctx = new SqlNativeProcedureContext(this, bound);
        return compiled(ctx);
    }

    private static IReadOnlyList<SqlExecArgument> BindCSharpArguments(
        SqlStoredProcedureDefinition proc,
        IReadOnlyList<SqlExecArgument> args)
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var positional = new List<string>();
        foreach (var a in args)
        {
            if (a.ParameterName != null)
                byName[a.ParameterName.TrimStart('@')] = a.ValueExpression;
            else
                positional.Add(a.ValueExpression);
        }

        var result = new List<SqlExecArgument>(proc.Parameters.Count);
        var positionalIdx = 0;
        foreach (var p in proc.Parameters)
        {
            var key = p.Name.TrimStart('@');
            string value;
            if (byName.TryGetValue(key, out var v))
            {
                value = v;
            }
            else if (positionalIdx < positional.Count)
            {
                value = positional[positionalIdx++];
            }
            else if (p.DefaultValue != null)
            {
                value = FormatLiteral(p.DefaultValue, p.Type);
            }
            else if (p.IsNullable)
            {
                value = "NULL";
            }
            else
            {
                throw new WalhallaException(
                    $"Parameter '{p.Name}' not provided for procedure '{proc.Name}'.");
            }
            result.Add(new SqlExecArgument(p.Name, value));
        }
        return result;
    }

    private static string BindProcedureBody(SqlStoredProcedureDefinition proc,
        IReadOnlyList<SqlExecArgument> args)
    {
        var body = proc.Body;

        // Build argument map: parameter name -> value expression string
        var boundByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var positionalValues = new List<string>();

        foreach (var arg in args)
        {
            if (arg.ParameterName != null)
                boundByName[arg.ParameterName] = arg.ValueExpression;
            else
                positionalValues.Add(arg.ValueExpression);
        }

        // Bind parameters
        for (int i = 0; i < proc.Parameters.Count; i++)
        {
            var param = proc.Parameters[i];
            string valueStr;

            if (boundByName.TryGetValue(param.Name, out var namedVal))
                valueStr = namedVal;
            else if (i < positionalValues.Count)
                valueStr = positionalValues[i];
            else if (param.DefaultValue != null)
                valueStr = FormatLiteral(param.DefaultValue, param.Type);
            else
                throw new WalhallaException(
                    $"Parameter '{param.Name}' not provided for procedure '{proc.Name}'.");

            // Replace @paramname in body (whole-word, case-insensitive)
            body = ReplaceParameterToken(body, param.Name, valueStr);
        }

        return body;
    }

    private static string FormatLiteral(object? value, SqlScalarType type)
    {
        if (value == null) return "NULL";
        return type switch
        {
            SqlScalarType.String => $"'{value.ToString()!.Replace("'", "''")}'",
            SqlScalarType.DateTime => $"'{((DateTime)value).ToString("O")}'",
            SqlScalarType.Boolean => value.ToString()!.ToLowerInvariant(),
            SqlScalarType.Guid => $"'{value}'",
            _ => value.ToString()!
        };
    }

    private static string ReplaceParameterToken(string body, string paramName, string value)
    {
        // Simple whole-word replacement for @paramname tokens
        var searchFor = paramName.StartsWith('@') ? paramName : "@" + paramName;
        var result = body;
        var idx = 0;
        while ((idx = result.IndexOf(searchFor, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            // Check word boundaries
            var beforeOk = idx == 0 || !char.IsLetterOrDigit(result[idx - 1]);
            var afterIdx = idx + searchFor.Length;
            var afterOk = afterIdx >= result.Length || !char.IsLetterOrDigit(result[afterIdx]);

            if (beforeOk && afterOk)
            {
                result = result[..idx] + value + result[afterIdx..];
                idx += value.Length;
            }
            else
            {
                idx += searchFor.Length;
            }
        }
        return result;
    }

    private WalhallaResultSet ExecuteProcedureBody(string body)
    {
        var statements = SplitSqlStatements(body);
        WalhallaResultSet? lastResult = null;

        foreach (var stmtSql in statements)
        {
            if (string.IsNullOrWhiteSpace(stmtSql)) continue;
            lastResult = Execute(stmtSql);
        }

        return lastResult ?? WalhallaResultSet.Affected(0);
    }

    private static List<string> SplitSqlStatements(string body)
    {
        var result = new List<string>();
        var start = 0;
        var inString = false;
        for (int i = 0; i < body.Length; i++)
        {
            switch (body[i])
            {
                case '\'':
                    inString = !inString;
                    break;
                case ';':
                    if (!inString)
                    {
                        result.Add(body[start..i].Trim());
                        start = i + 1;
                    }
                    break;
            }
        }
        if (start < body.Length)
        {
            var last = body[start..].Trim();
            if (last.Length > 0) result.Add(last);
        }
        return result;
    }

    private WalhallaResultSet ExecuteCreateTrigger(SqlCreateTriggerStatement stmt)
    {
        lock (_metaSync)
        {
            if (!_triggersByTable.TryGetValue(stmt.TableName, out var list))
            {
                list = new List<SqlTriggerDefinition>();
                _triggersByTable[stmt.TableName] = list;
            }

            if (!stmt.OrReplace)
            {
                var existing = list.Find(t =>
                    string.Equals(t.Name, stmt.TriggerName, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                    throw new WalhallaException(
                        $"Trigger '{stmt.TriggerName}' already exists on table '{stmt.TableName}'. Use OR REPLACE to overwrite.");
                list.Remove(existing!);
            }
            else
            {
                list.RemoveAll(t =>
                    string.Equals(t.Name, stmt.TriggerName, StringComparison.OrdinalIgnoreCase));
            }

            list.Add(new SqlTriggerDefinition(
                stmt.TriggerName, stmt.TableName, stmt.Event, stmt.Timing, stmt.Body));
        }
        return WalhallaResultSet.Affected(0);
    }

    private WalhallaResultSet ExecuteDropTrigger(SqlDropTriggerStatement stmt)
    {
        lock (_metaSync)
        {
            foreach (var (tableName, list) in _triggersByTable)
            {
                var removed = list.RemoveAll(t =>
                    string.Equals(t.Name, stmt.TriggerName, StringComparison.OrdinalIgnoreCase));
                if (removed > 0)
                    return WalhallaResultSet.Affected(0);
            }

            if (!stmt.IfExists)
                throw new WalhallaException($"Trigger '{stmt.TriggerName}' not found.");
        }
        return WalhallaResultSet.Affected(0);
    }

    private bool HasTriggersFor(string tableName, SqlTriggerEvent evt)
    {
        lock (_metaSync)
        {
            if (!_triggersByTable.TryGetValue(tableName, out var list) || list.Count == 0)
                return false;
            foreach (var trigger in list)
                if (trigger.Event == evt)
                    return true;
            return false;
        }
    }

    private bool HasIncomingForeignKeys(SqlTableDefinition tableDef)
    {
        var collectionName = tableDef.CollectionName;
        foreach (var tableName in _store.GetAllTables().Select(t => t.CollectionName))
        {
            var otherDef = _store.GetTableDefinition(tableName);
            if (otherDef == null) continue;
            foreach (var fk in otherDef.ForeignKeys ?? Array.Empty<SqlForeignKeyDefinition>())
            {
                if (string.Equals(fk.ReferencedCollection, collectionName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private void FireTriggers(string tableName, SqlTriggerEvent evt, SqlTriggerTiming timing)
    {
        SqlTriggerDefinition[] snapshot;
        lock (_metaSync)
        {
            if (!_triggersByTable.TryGetValue(tableName, out var list) || list.Count == 0)
                return;
            snapshot = list.ToArray();
        }

        foreach (var trigger in snapshot)
        {
            if (trigger.Event == evt && trigger.Timing == timing)
                ExecuteProcedureBody(trigger.Body);
        }
    }

    // -- Foreign Key Enforcement --------------------------------------------------

    private void EnforceForeignKeyInsert(SqlTableDefinition tableDef, object?[] row, IReadOnlyList<object?[]>? pendingRows = null, WalhallaSqlTransaction? tx = null)
    {
        foreach (var fk in tableDef.ForeignKeys ?? Array.Empty<SqlForeignKeyDefinition>())
        {
            if (fk.ColumnNames.Count == 0) continue;

            // Determine if any FK column is null ? skip (FK with null = no constraint)
            if (fk.ColumnNames.Any(cn =>
            {
                var idx = FindColumnIndex(tableDef, cn);
                return idx >= 0 && row[idx] == null;
            }))
                continue;

            // Build lookup key for the referenced row
            var refTableDef = _store.GetTableDefinition(fk.ReferencedCollection);
            if (refTableDef == null)
                throw new WalhallaException(
                    $"Foreign key references non-existent table '{fk.ReferencedCollection}'.");

            var exists = RowExistsByKey(refTableDef, fk.ReferencedCollection, fk.ReferencedColumns, tableDef, fk.ColumnNames, row);
            if (!exists && pendingRows != null && pendingRows.Count > 0
                && string.Equals(fk.ReferencedCollection, tableDef.CollectionName, StringComparison.OrdinalIgnoreCase))
            {
                var fkValues = GetColumnValues(tableDef, fk.ColumnNames, row);
                foreach (var pendingRow in pendingRows)
                {
                    var pendingValues = GetColumnValues(refTableDef, fk.ReferencedColumns, pendingRow);
                    bool match = true;
                    for (int i = 0; i < fkValues.Length; i++)
                    {
                        if (!Equals(pendingValues[i], fkValues[i]))
                        {
                            match = false;
                            break;
                        }
                    }
                    if (match)
                    {
                        exists = true;
                        break;
                    }
                }
            }

            // Also check rows buffered in the current transaction from previous statements
            if (!exists && tx != null)
            {
                var refTableId = _store.GetTableId(fk.ReferencedCollection);
                if (refTableId >= 0)
                {
                    var fkValues = GetColumnValues(tableDef, fk.ColumnNames, row);
                    foreach (var (_, encodedRow) in tx.GetInsertedRows(refTableId))
                    {
                        var bufferedRow = RowCodec.DecodeToArray(encodedRow, refTableDef);
                        var bufferedValues = GetColumnValues(refTableDef, fk.ReferencedColumns, bufferedRow);
                        bool match = true;
                        for (int i = 0; i < fkValues.Length; i++)
                        {
                            if (!Equals(bufferedValues[i], fkValues[i]))
                            {
                                match = false;
                                break;
                            }
                        }
                        if (match)
                        {
                            exists = true;
                            break;
                        }
                    }
                }
            }

            if (!exists)
            {
                var fkName = string.IsNullOrEmpty(fk.ConstraintName) ? "FK" : fk.ConstraintName;
                throw new WalhallaException(
                    $"Foreign key constraint '{fkName}' violated: referenced row not found.");
            }
        }
    }

    private void EnforceForeignKeyDelete(SqlTableDefinition tableDef, long rowId, object?[] row)
    {
        // Check all tables that reference this table via foreign keys
        var allTables = _store.GetAllTables().Select(t => t.CollectionName);
        foreach (var childTableName in allTables)
        {
            var childTableDef = _store.GetTableDefinition(childTableName);
            if (childTableDef == null) continue;

            foreach (var fk in childTableDef.ForeignKeys ?? Array.Empty<SqlForeignKeyDefinition>())
            {
                if (!string.Equals(fk.ReferencedCollection, tableDef.CollectionName, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Check if any row in child table references the row being deleted
                var pkValues = GetColumnValues(tableDef, fk.ReferencedColumns, row);
                var referencingRowIds = FindReferencingRowIds(childTableDef, childTableName, fk, pkValues);

                if (referencingRowIds.Count > 0)
                {
                    var fkName = string.IsNullOrEmpty(fk.ConstraintName) ? "FK" : fk.ConstraintName;

                    switch (fk.OnDelete)
                    {
                        case SqlForeignKeyAction.Restrict:
                            throw new WalhallaException(
                                $"Foreign key constraint '{fkName}' violated: row is referenced by table '{childTableName}'.");
                        case SqlForeignKeyAction.Cascade:
                            // Delete all referencing rows recursively
                            foreach (var childRowId in referencingRowIds)
                            {
                                var childEncoded = _store.GetRow(_store.GetTableId(childTableName), childRowId);
                                if (childEncoded != null)
                                {
                                    var childRow = RowCodec.DecodeToArray(childEncoded, childTableDef);
                                    // Recursively check FKs for the child row
                                    EnforceForeignKeyDelete(childTableDef, childRowId, childRow);
                                    // Delete the child row
                                    var childPkIdx = GetPrimaryKeyIndex(childTableDef);
                                    var childRowActualId = Convert.ToInt64(childRow[childPkIdx]);
                                    _store.DeleteRows(_store.GetTableId(childTableName), new[] { childRowActualId });
                                }
                            }
                            break;
                        case SqlForeignKeyAction.SetNull:
                            // Set FK columns to null
                            foreach (var childRowId in referencingRowIds)
                            {
                                var childEncoded = _store.GetRow(_store.GetTableId(childTableName), childRowId);
                                if (childEncoded != null)
                                {
                                    var childRow = RowCodec.DecodeToArray(childEncoded, childTableDef);
                                    foreach (var colName in fk.ColumnNames)
                                    {
                                        var idx = FindColumnIndex(childTableDef, colName);
                                        if (idx >= 0) childRow[idx] = null;
                                    }
                                    var childTableId = _store.GetTableId(childTableName);
                                    var newEncoded = EncodeRowWithBlobs(childTableId, childRow, childTableDef);
                                    _store.UpdateRow(childTableId, childRowId, newEncoded);
                                }
                            }
                            break;
                    }
                }
            }
        }
    }

    private void EnforceForeignKeyUpdate(SqlTableDefinition tableDef, object?[] oldRow, object?[] newRow)
    {
        // 1. Check FK columns on THIS table: if changed, new values must reference existing rows
        foreach (var fk in tableDef.ForeignKeys ?? Array.Empty<SqlForeignKeyDefinition>())
        {
            if (fk.ColumnNames.Count == 0) continue;

            // Check if any FK column changed
            var changed = false;
            foreach (var cn in fk.ColumnNames)
            {
                var idx = FindColumnIndex(tableDef, cn);
                if (idx >= 0 && !Equals(oldRow[idx], newRow[idx]))
                {
                    changed = true;
                    break;
                }
            }
            if (!changed) continue;

            // If new FK value has any null ? skip (FK with null = no constraint)
            if (fk.ColumnNames.Any(cn =>
            {
                var idx = FindColumnIndex(tableDef, cn);
                return idx >= 0 && newRow[idx] == null;
            }))
                continue;

            var refTableDef = _store.GetTableDefinition(fk.ReferencedCollection);
            if (refTableDef == null)
                throw new WalhallaException(
                    $"Foreign key references non-existent table '{fk.ReferencedCollection}'.");

            var exists = RowExistsByKey(refTableDef, fk.ReferencedCollection, fk.ReferencedColumns, tableDef, fk.ColumnNames, newRow);
            if (!exists)
            {
                var fkName = string.IsNullOrEmpty(fk.ConstraintName) ? "FK" : fk.ConstraintName;
                throw new WalhallaException(
                    $"Foreign key constraint '{fkName}' violated: referenced row not found.");
            }
        }

        // 2. If PK columns changed and other tables reference us with CASCADE, propagate
        var pkCols = tableDef.PrimaryKeyColumns.Select(c => c.Name).ToArray();
        var pkChanged = pkCols.Any(pk =>
        {
            var idx = FindColumnIndex(tableDef, pk);
            return idx >= 0 && !Equals(oldRow[idx], newRow[idx]);
        });

        if (pkChanged)
        {
            var allTables = _store.GetAllTables().Select(t => t.CollectionName);
            foreach (var childTableName in allTables)
            {
                var childTableDef = _store.GetTableDefinition(childTableName);
                if (childTableDef == null) continue;

                foreach (var fk in childTableDef.ForeignKeys ?? Array.Empty<SqlForeignKeyDefinition>())
                {
                    if (!string.Equals(fk.ReferencedCollection, tableDef.CollectionName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (fk.OnUpdate == SqlForeignKeyAction.Cascade)
                    {
                        // Update matching FK columns in referencing child rows
                        var oldPkValues = GetColumnValues(tableDef, fk.ReferencedColumns, oldRow);
                        var referencingRowIds = FindReferencingRowIds(childTableDef, childTableName, fk, oldPkValues);

                        foreach (var childRowId in referencingRowIds)
                        {
                            var childEncoded = _store.GetRow(_store.GetTableId(childTableName), childRowId);
                            if (childEncoded != null)
                            {
                                var childRow = RowCodec.DecodeToArray(childEncoded, childTableDef);
                                for (int i = 0; i < fk.ColumnNames.Count && i < fk.ReferencedColumns.Count; i++)
                                {
                                    var childColIdx = FindColumnIndex(childTableDef, fk.ColumnNames[i]);
                                    var refColIdx = FindColumnIndex(tableDef, fk.ReferencedColumns[i]);
                                    if (childColIdx >= 0 && refColIdx >= 0)
                                        childRow[childColIdx] = newRow[refColIdx];
                                }
                                var childTableId = _store.GetTableId(childTableName);
                                var newEncoded = EncodeRowWithBlobs(childTableId, childRow, childTableDef);
                                _store.UpdateRow(childTableId, childRowId, newEncoded);
                            }
                        }
                    }
                }
            }
        }
    }

    private bool RowExistsByKey(SqlTableDefinition refTableDef, string refTableName,
        IReadOnlyList<string> refCols, SqlTableDefinition fkTableDef,
        IReadOnlyList<string> fkCols, object?[] fkRow)
    {
        // Try to find the referenced row by PK or scan index
        var refTableId = _store.GetTableId(refTableName);
        var refPkCols = refTableDef.PrimaryKeyColumns;

        // If referencing columns match the referenced table's PK, try direct lookup
        if (refCols.Count == refPkCols.Count &&
            refCols.Zip(refPkCols, (rc, pc) => string.Equals(rc, pc.Name, StringComparison.OrdinalIgnoreCase)).All(x => x))
        {
            // Direct PK lookup: combine FK values as a single long key.
            // Only return if found; fall through to scan otherwise (rowId may differ from PK value).
            if (refCols.Count == 1)
            {
                var fkIdx = FindColumnIndex(fkTableDef, fkCols[0]);
                if (fkIdx >= 0 && fkRow[fkIdx] is long pkVal && _store.GetRow(refTableId, pkVal) != null)
                    return true;
                if (fkIdx >= 0 && fkRow[fkIdx] is int intVal && _store.GetRow(refTableId, intVal) != null)
                    return true;
            }
        }

        // Fallback: scan the referenced table for matching row
        var values = GetColumnValues(fkTableDef, fkCols, fkRow);
        var found = false;
        _store.ScanWithPredicate(refTableId,
            encoded => RowCodec.DecodeToArray(encoded, refTableDef),
            refRow =>
            {
                for (int i = 0; i < refCols.Count; i++)
                {
                    var refIdx = FindColumnIndex(refTableDef, refCols[i]);
                    if (refIdx < 0 || !Equals(refRow[refIdx], values[i]))
                        return false;
                }
                found = true;
                return true; // stop scanning after match
            },
            new List<object?[]>(),
            1);
        return found;
    }

    private object?[] GetColumnValues(SqlTableDefinition tableDef, IReadOnlyList<string> columnNames, object?[] row)
    {
        var values = new object?[columnNames.Count];
        for (int i = 0; i < columnNames.Count; i++)
        {
            var idx = FindColumnIndex(tableDef, columnNames[i]);
            values[i] = idx >= 0 ? row[idx] : null;
        }
        return values;
    }

    private List<long> FindReferencingRowIds(SqlTableDefinition childTableDef, string childTableName,
        SqlForeignKeyDefinition fk, object?[] parentKeyValues)
    {
        var result = new List<long>();
        var childTableId = _store.GetTableId(childTableName);

        // Try to use an index on the FK columns if available
        _store.ScanWithPredicate(childTableId,
            encoded => RowCodec.DecodeToArray(encoded, childTableDef),
            childRow =>
            {
                for (int i = 0; i < fk.ColumnNames.Count; i++)
                {
                    var idx = FindColumnIndex(childTableDef, fk.ColumnNames[i]);
                    if (idx < 0 || !Equals(childRow[idx], parentKeyValues[i]))
                        return false;
                }
                var pkIdx = GetPrimaryKeyIndex(childTableDef);
                result.Add(Convert.ToInt64(childRow[pkIdx]));
                return false; // continue scanning
            },
            new List<object?[]>(),
            int.MaxValue);
        return result;
    }

    // -- Streaming execution -------------------------------------------------

    public WalhallaStreamResult ExecuteStreaming(string sql)
    {
        var statement = SqlStatementParser.Parse(sql);
        if (statement is not SqlSelectStatement select)
            throw new WalhallaException("Only SELECT statements support streaming execution.");

        SqlCreateViewStatement? viewDef;
        lock (_metaSync)
            _views.TryGetValue(select.TableName, out viewDef);
        if (viewDef != null)
            select = viewDef.SelectStatement;

        if (select.DerivedTable != null)
            throw new WalhallaException("Derived tables are not supported in streaming mode.");

        var plan = QueryPlanner.Build(select, _store, ResolveSubquery, _statisticsCatalog);

        if (!plan.IsStreamable)
            throw new WalhallaException(
                "Query is not streamable. Streaming requires a simple SELECT without ORDER BY, DISTINCT, GROUP BY, or JOIN.");

        // Build predicate delegate (parameterless only)
        Func<object?[], bool>? predicate = null;
        if (plan.WhereDelegate != null && plan.ParameterCount == 0)
        {
            var where = plan.WhereDelegate;
            var emptyParams = Array.Empty<object?>();
            predicate = row => where(row, emptyParams);
        }
        else if (plan.WhereDelegate != null)
        {
            throw new WalhallaException("Parameterized queries are not supported in streaming mode. Use a prepared statement.");
        }

        // Map column types from table metadata
        var columnTypes = new Type[plan.OutputColumnNames.Length];
        for (int i = 0; i < plan.OutputColumnNames.Length; i++)
        {
            var colIdx = plan.ProjectionIndices[i];
            if (colIdx >= 0 && colIdx < plan.TableDefinition.Columns.Count)
                columnTypes[i] = MapScalarTypeToClr(plan.TableDefinition.Columns[colIdx].Type);
            else
                columnTypes[i] = typeof(object);
        }

        // Get lazy enumerator from TableStore
        IEnumerator<object?[]> rowEnumerator;

        if (plan.PkRange != null)
        {
            var range = plan.PkRange;
            long minRowId = range.HasLiteralBounds ? range.LiteralMin : long.MinValue;
            long maxRowId = range.HasLiteralBounds ? range.LiteralMax : long.MaxValue;
            if (!range.MinInclusive) minRowId++;
            if (!range.MaxInclusive) maxRowId--;

            rowEnumerator = _store.ScanRowKeyRangeLazy(plan.TableId, minRowId, maxRowId,
                plan.TableDefinition, predicate).GetEnumerator();
        }
        else
        {
            rowEnumerator = _store.ScanWithPredicateLazy(plan.TableId, plan.TableDefinition, predicate).GetEnumerator();
        }

        var wrapped = new StreamingRowEnumerator(rowEnumerator, plan, select.Limit, select.Offset);
        var schema = new ColumnSchema(plan.OutputColumnNames);
        return new WalhallaStreamResult(plan.OutputColumnNames, columnTypes, schema, wrapped);
    }

    private static Type MapScalarTypeToClr(SqlScalarType type) => type switch
    {
        SqlScalarType.Int32 => typeof(int),
        SqlScalarType.Int64 => typeof(long),
        SqlScalarType.Int16 => typeof(short),
        SqlScalarType.Double => typeof(double),
        SqlScalarType.Decimal => typeof(decimal),
        SqlScalarType.String => typeof(string),
        SqlScalarType.Boolean => typeof(bool),
        SqlScalarType.DateTime => typeof(DateTime),
        SqlScalarType.Date => typeof(DateTime),
        SqlScalarType.Time => typeof(TimeSpan),
        SqlScalarType.Binary => typeof(byte[]),
        SqlScalarType.Json => typeof(string),
        SqlScalarType.Guid => typeof(Guid),
        _ => typeof(object),
    };

    private sealed class StreamingRowEnumerator : IEnumerator<object?[]>
    {
        private readonly IEnumerator<object?[]> _inner;
        private readonly CompiledPlan _plan;
        private readonly int _limit;
        private readonly int _offset;
        private int _yielded;
        private int _skipped;

        public StreamingRowEnumerator(
            IEnumerator<object?[]> inner, CompiledPlan plan, int? limit, int? offset)
        {
            _inner = inner;
            _plan = plan;
            _limit = limit ?? int.MaxValue;
            _offset = offset ?? 0;
        }

        public object?[] Current { get; private set; } = Array.Empty<object?>();

        object? System.Collections.IEnumerator.Current => Current;

        public bool MoveNext()
        {
            while (_inner.MoveNext())
            {
                if (_skipped < _offset)
                {
                    _skipped++;
                    continue;
                }

                if (_yielded >= _limit)
                    return false;

                // Always copy: the inner enumerator reuses its buffer
                var source = _inner.Current;
                var result = new object?[_plan.OutputColumnNames.Length];
                var comp = _plan.ComputedProjections;
                for (int i = 0; i < _plan.ProjectionIndices.Length; i++)
                {
                    if (comp != null && comp[i] != null)
                        result[i] = comp[i]!(source);
                    else
                        result[i] = source[_plan.ProjectionIndices[i]];
                }

                Current = result;
                _yielded++;
                return true;
            }

            return false;
        }

        public void Reset() => throw new NotSupportedException();
        public void Dispose() => _inner.Dispose();
    }
}
