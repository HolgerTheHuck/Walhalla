using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.TestUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.EntityFrameworkCore
{
    public sealed class WalhallaSqlServiceCollectionExtensionsSpecTests()
        : EntityFrameworkServiceCollectionExtensionsTestBase(LayeredSqlRelationalTestHelpers.Instance);
}

namespace Microsoft.EntityFrameworkCore.TestUtilities
{
    public sealed class LayeredSqlRelationalTestHelpers : RelationalTestHelpers
    {
        public static LayeredSqlRelationalTestHelpers Instance { get; } = new();

        private LayeredSqlRelationalTestHelpers()
        {
        }

        public override IServiceCollection AddProviderServices(IServiceCollection services)
            => services.AddEntityFrameworkWalhallaSql();

        public override DbContextOptionsBuilder UseProviderOptions(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder.UseWalhallaSql(new WalhallaSqlEfCoreOptions("DataSource=ef8spec-helpers;Database=App"));

        public override LoggingDefinitions LoggingDefinitions { get; } = new LayeredSqlSpecLoggingDefinitions();

        private sealed class LayeredSqlSpecLoggingDefinitions : RelationalLoggingDefinitions
        {
        }
    }
}
