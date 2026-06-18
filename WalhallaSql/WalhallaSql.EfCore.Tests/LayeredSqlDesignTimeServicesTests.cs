using System.Reflection;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace WalhallaSql.EfCore.Tests;

public sealed class WalhallaSqlDesignTimeServicesTests
{
    [Fact]
    public void Provider_exports_design_time_services_for_ef_tools_discovery()
    {
        var attribute = typeof(WalhallaSqlEfCoreContext).Assembly
            .GetCustomAttribute<DesignTimeProviderServicesAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal("WalhallaSql.EfCore.WalhallaSqlDesignTimeServices", attribute!.TypeName);
    }

    [Fact]
    public void Provider_design_time_services_register_annotation_code_generator()
    {
        var services = new ServiceCollection();
        services.AddEntityFrameworkWalhallaSql();

        var designTimeServices = new WalhallaSqlDesignTimeServices();
        designTimeServices.ConfigureDesignTimeServices(services);

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IRelationalTypeMappingSource>());
        Assert.NotNull(provider.GetRequiredService<IAnnotationCodeGenerator>());
        Assert.NotNull(provider.GetRequiredService<ITypeMappingSource>());
    }
}
