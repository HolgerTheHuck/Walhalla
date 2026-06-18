using System.Reflection;
using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore.Diagnostics.Internal;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.TestUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.EntityFrameworkCore;

public sealed class WalhallaSqlLoggingRelationalSpecTests
    : LoggingRelationalTestBase<WalhallaSqlDbContextOptionsBuilder, WalhallaSqlDbContextOptionsExtension>
{
    protected override DbContextOptionsBuilder CreateOptionsBuilder(
        IServiceCollection services,
        Action<RelationalDbContextOptionsBuilder<WalhallaSqlDbContextOptionsBuilder, WalhallaSqlDbContextOptionsExtension>>? relationalAction)
        => new DbContextOptionsBuilder()
            .UseInternalServiceProvider(services.AddEntityFrameworkWalhallaSql().BuildServiceProvider(validateScopes: true))
            .UseWalhallaSql(new WalhallaSqlEfCoreOptions("DataSource=WalhallaSqlLoggingRelationalSpecTests;Database=App"));

    protected override TestLogger CreateTestLogger()
        => new TestLogger<TestRelationalLoggingDefinitions>();

    protected override string ProviderName
        => typeof(WalhallaSqlEfCoreOptions).Assembly.GetName().Name!;

    protected override string ProviderVersion
        => typeof(WalhallaSqlEfCoreOptions).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion!;

    protected override string DefaultOptions
        => "using LayeredSql ";
}
