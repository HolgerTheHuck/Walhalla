using System;
using System.IO;
using WalhallaSql;
using WalhallaSql.Core;
using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore;

namespace WalhallaSql.EfCore.Tests.MinimalSpecs;

/// <summary>
/// Leichtgewichtige Test-Infrastruktur für EF-Core-Minimalbeispiele.
/// Jeder Test bekommt eine eigene temporäre MvccBPlusTree-Engine,
/// damit die Tests parallelisierbar sind und nicht gegen den Legacy-
/// BPlusTree-Cross-Session-Bug laufen.
/// </summary>
public sealed class MinimalSpecScope<TContext> : IDisposable
    where TContext : WalhallaSqlEfCoreContext
{
    private readonly string _dbPath;
    private readonly WalhallaEngine _engine;

    private MinimalSpecScope(string dbPath, WalhallaEngine engine, TContext context)
    {
        _dbPath = dbPath;
        _engine = engine;
        Context = context;
    }

    public TContext Context { get; }

    /// <summary>
    /// Die zugrunde liegende Engine. Für Tests, die mehrere Kontexte
    /// auf derselben Datenbank benötigen (z. B. Transaktions-Tests).
    /// </summary>
    public WalhallaEngine Engine => _engine;

    /// <summary>
    /// Erzeugt einen frischen Scope mit einer neuen Datenbank.
    /// </summary>
    /// <param name="migrationName">Eindeutiger Name für ApplyPlannedChanges.</param>
    /// <param name="factory">Kontext-Factory, üblicherweise "options => new MyContext(options)".</param>
    /// <param name="seed">Optionaler Seed, der nach der Migration ausgeführt wird.</param>
    public static MinimalSpecScope<TContext> Create(
        string migrationName,
        Func<DbContextOptions, TContext> factory,
        Action<TContext>? seed = null)
    {
        var dbPath = Path.Combine(
            Path.GetTempPath(),
            "LayeredSql",
            "MinimalSpecs",
            typeof(TContext).Name,
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(dbPath);

        var engineOptions = new WalhallaOptions(dbPath) { StorageMode = StorageMode.MvccBPlusTree };
        var engine = new WalhallaEngine(engineOptions);

        var options = new DbContextOptionsBuilder<TContext>()
            .UseWalhallaSql(new WalhallaSqlEfCoreOptions(engine))
            .Options;

        var context = factory(options);
        context.Migrations.ApplyPlannedChanges(migrationName);

        seed?.Invoke(context);

        return new MinimalSpecScope<TContext>(dbPath, engine, context);
    }

    public void Dispose()
    {
        Context.Dispose();
        _engine.Dispose();

        try
        {
            if (Directory.Exists(_dbPath))
                Directory.Delete(_dbPath, recursive: true);
        }
        catch
        {
            // Best-Effort-Aufräumen; beim Testlauf auf dem lokalen Rechner
            // ist das meist unkritisch.
        }
    }
}
