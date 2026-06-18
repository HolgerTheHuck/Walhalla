using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Scaffolding;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

[assembly: DesignTimeProviderServices("WalhallaSql.EfCore.WalhallaSqlDesignTimeServices")]

namespace WalhallaSql.EfCore;

public sealed class WalhallaSqlDesignTimeServices : IDesignTimeServices
{
    public void ConfigureDesignTimeServices(IServiceCollection services)
    {
        services.AddEntityFrameworkWalhallaSql();
        DesignTimeAnnotationGeneratorRegistration.Register(services);
    }

    private static class DesignTimeAnnotationGeneratorRegistration
    {
#pragma warning disable EF1001
        public static void Register(IServiceCollection services)
        {
            services.TryAddScoped<IDatabaseModelFactory, WalhallaSqlDatabaseModelFactory>();
            services.AddSingleton<ITypeMappingSource>(provider => provider.GetRequiredService<IRelationalTypeMappingSource>());
            services.AddSingleton<AnnotationCodeGeneratorDependencies>(provider =>
                new AnnotationCodeGeneratorDependencies(provider.GetRequiredService<IRelationalTypeMappingSource>()));
            services.AddSingleton<IAnnotationCodeGenerator, AnnotationCodeGenerator>();
        }
#pragma warning restore EF1001
    }
}
