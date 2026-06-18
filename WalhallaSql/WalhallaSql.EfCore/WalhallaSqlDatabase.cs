using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;

namespace WalhallaSql.EfCore;

internal sealed class WalhallaSqlDatabase : Database
{
    private readonly ICurrentDbContext _currentDbContext;

    public WalhallaSqlDatabase(DatabaseDependencies dependencies, ICurrentDbContext currentDbContext)
        : base(dependencies)
    {
        _currentDbContext = currentDbContext ?? throw new ArgumentNullException(nameof(currentDbContext));
    }

    public override int SaveChanges(IList<IUpdateEntry> entries)
    {
        using var runtime = WalhallaSqlDbContextRuntime.Create(_currentDbContext.Context);
        return runtime.SaveChanges(entries, CancellationToken.None);
    }

    public override async Task<int> SaveChangesAsync(IList<IUpdateEntry> entries, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var runtime = WalhallaSqlDbContextRuntime.Create(_currentDbContext.Context);
        return await runtime.SaveChangesAsync(entries, cancellationToken).ConfigureAwait(false);
    }
}
