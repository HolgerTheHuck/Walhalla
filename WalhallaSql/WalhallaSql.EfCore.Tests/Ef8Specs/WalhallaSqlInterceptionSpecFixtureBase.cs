using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.TestUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.EntityFrameworkCore;

public abstract class WalhallaSqlInterceptionSpecFixtureBase : InterceptionTestBase.InterceptionFixtureBase
{
    protected override IServiceCollection AddServices(IServiceCollection serviceCollection)
        => base.AddServices(serviceCollection)
            .AddEntityFrameworkWalhallaSql();

    protected override IServiceCollection InjectInterceptors(IServiceCollection serviceCollection, IEnumerable<IInterceptor> injectedInterceptors)
        => base.InjectInterceptors(
            serviceCollection.AddEntityFrameworkWalhallaSql(),
            injectedInterceptors);

    protected override bool ShouldSubscribeToDiagnosticListener
        => true;

    protected override ITestStoreFactory TestStoreFactory
        => LayeredSqlTestStoreFactory.Instance;
}
