using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace WalhallaSql.EfCore;

internal sealed class WalhallaSqlExecutionStrategyFactory : IExecutionStrategyFactory
{
    private readonly ICurrentDbContext _currentDbContext;

    public WalhallaSqlExecutionStrategyFactory(ICurrentDbContext currentDbContext)
    {
        _currentDbContext = currentDbContext ?? throw new ArgumentNullException(nameof(currentDbContext));
    }

    public IExecutionStrategy Create()
    {
        return new NonRetryingExecutionStrategy(_currentDbContext.Context);
    }
}
