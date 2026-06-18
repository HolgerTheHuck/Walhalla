using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace WalhallaSql.EfCore.Tests;

public sealed class WarningConfigurationRegressionTests
{
    [Fact]
    public void UseLayeredSql_sets_multiple_collection_include_warning_to_log()
    {
        var optionsBuilder = new DbContextOptionsBuilder()
            .UseWalhallaSql(new WalhallaSqlEfCoreOptions("DataSource=warning-config;Database=App"));

        var warningsConfiguration = optionsBuilder.Options.FindExtension<CoreOptionsExtension>()?.WarningsConfiguration;

        Assert.NotNull(warningsConfiguration);
        Assert.Equal(
            WarningBehavior.Log,
            warningsConfiguration!.GetBehavior(RelationalEventId.MultipleCollectionIncludeWarning));
    }
}
