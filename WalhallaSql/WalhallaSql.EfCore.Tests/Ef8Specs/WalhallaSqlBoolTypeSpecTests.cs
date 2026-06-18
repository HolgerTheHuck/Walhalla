using System.Data.Common;
using System.IO;
using System.Collections.Concurrent;
using WalhallaSql;
using WalhallaSql.AdoNet;
using WalhallaSql.Core;
using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore.TestUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.EntityFrameworkCore
{
    public sealed class WalhallaSqlBoolTypeSpecTests(
        WalhallaSqlBoolTypeSpecTests.BoolTypeFixture fixture)
        : BuiltInDataTypesTestBase<WalhallaSqlBoolTypeSpecTests.BoolTypeFixture>(fixture)
    {
        public sealed class BoolTypeFixture : BuiltInDataTypesFixtureBase
        {
            protected override string StoreName
                => nameof(WalhallaSqlBoolTypeSpecTests);

            protected override ITestStoreFactory TestStoreFactory
                => LayeredSqlTestStoreFactory.Instance;

            public override bool StrictEquality
                => true;

            public override bool SupportsAnsi
                => false;

            public override bool SupportsUnicodeToAnsiConversion
                => false;

            public override bool SupportsLargeStringComparisons
                => false;

            public override bool SupportsBinaryKeys
                => false;

            public override bool SupportsDecimalComparisons
                => true;

            public override DateTime DefaultDateTime
                => new(1973, 9, 3);

            public override bool PreservesDateTimeKind
                => true;
        }
    }
}

namespace Microsoft.EntityFrameworkCore.TestUtilities
{
    public sealed class LayeredSqlTestStoreFactory : RelationalTestStoreFactory
    {
        public static LayeredSqlTestStoreFactory Instance { get; } = new();

        private LayeredSqlTestStoreFactory()
        {
        }

        public override TestStore Create(string storeName)
            => LayeredSqlSpecTestStore.Create(storeName);

        public override TestStore GetOrCreate(string storeName)
            => LayeredSqlSpecTestStore.GetOrCreate(storeName);

        public override IServiceCollection AddProviderServices(IServiceCollection serviceCollection)
            => serviceCollection.AddEntityFrameworkWalhallaSql();
    }

    public sealed class LayeredSqlSpecTestStore : RelationalTestStore
    {
        private static readonly ConcurrentDictionary<string, Lazy<RuntimeState>> SharedRuntimeStates = new(StringComparer.OrdinalIgnoreCase);

        private readonly string _dbPath;
        private readonly WalhallaEngine _engine;
        private readonly bool _shared;

        private LayeredSqlSpecTestStore(RuntimeState runtimeState, bool shared)
            : base(runtimeState.StoreName, shared)
        {
            _shared = shared;
            _dbPath = runtimeState.DbPath;
            _engine = runtimeState.Engine;
            Connection = new WalhallaSqlDbConnection(runtimeState.Engine, $"DataSource={runtimeState.DataSourceName};Database=App");
            ConnectionString = Connection.ConnectionString;
        }

        public static LayeredSqlSpecTestStore Create(string storeName)
            => new(CreateRuntimeState(storeName, shared: false), shared: false);

        public static LayeredSqlSpecTestStore GetOrCreate(string storeName)
            => new(
                SharedRuntimeStates
                    .GetOrAdd(
                        storeName,
                        static name => new Lazy<RuntimeState>(
                            () => CreateSharedRuntimeState(name),
                            LazyThreadSafetyMode.ExecutionAndPublication))
                    .Value,
                shared: true);

        public override DbContextOptionsBuilder AddProviderOptions(DbContextOptionsBuilder builder)
            => builder.UseWalhallaSql(Connection);

        public override void Clean(DbContext context)
        {
            context.Database.EnsureDeleted();
            context.Database.EnsureCreated();
        }

        public override async Task DisposeAsync()
        {
            await base.DisposeAsync();

            if (_shared)
            {
                return;
            }

            _engine.Dispose();

            try
            {
                if (Directory.Exists(_dbPath))
                {
                    Directory.Delete(_dbPath, recursive: true);
                }
            }
            catch
            {
            }
        }

        private static RuntimeState CreateSharedRuntimeState(string storeName)
            => CreateRuntimeState(storeName, shared: true);

        private static RuntimeState CreateRuntimeState(string storeName, bool shared)
        {
            var safeStoreName = ToSafePathSegment(storeName);
            var suffix = shared ? "shared" : Guid.NewGuid().ToString("N");
            var dbPath = Path.Combine(Path.GetTempPath(), "LayeredSql", "Ef8SpecTests", safeStoreName, suffix);

            if (Directory.Exists(dbPath))
            {
                Directory.Delete(dbPath, recursive: true);
            }

            Directory.CreateDirectory(dbPath);

            var engineOptions = new WalhallaOptions(dbPath) { StorageMode = StorageMode.MvccBPlusTree };
            var engine = new WalhallaEngine(engineOptions);
            var database = engine;
            var dataSourceName = shared
                ? "ef8spec-shared-" + safeStoreName.ToLowerInvariant()
                : "ef8spec-" + Guid.NewGuid().ToString("N");
            WalhallaSqlConnectionRegistry.Register(dataSourceName, () => database);
            WalhallaSqlConnectionRegistry.Register(storeName, () => database);

            return new RuntimeState(
                storeName,
                dbPath,
                engine,
                dataSourceName);
        }

        private static string ToSafePathSegment(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var buffer = new char[value.Length];
            for (var i = 0; i < value.Length; i++)
            {
                var current = value[i];
                buffer[i] = Array.IndexOf(invalid, current) >= 0 ? '_' : current;
            }

            return new string(buffer);
        }

        private sealed record RuntimeState(
            string StoreName,
            string DbPath,
            WalhallaEngine Engine,
            string DataSourceName);
    }
}
