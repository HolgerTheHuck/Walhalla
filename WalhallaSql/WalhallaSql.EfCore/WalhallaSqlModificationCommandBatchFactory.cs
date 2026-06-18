using Microsoft.EntityFrameworkCore.Update;
using Microsoft.Extensions.DependencyInjection;

namespace WalhallaSql.EfCore;

internal sealed class WalhallaSqlModificationCommandBatchFactory : IModificationCommandBatchFactory
{
    private readonly ModificationCommandBatchFactoryDependencies _dependencies;

    public WalhallaSqlModificationCommandBatchFactory(ModificationCommandBatchFactoryDependencies dependencies)
    {
        _dependencies = dependencies;
    }

    public ModificationCommandBatch Create()
    {
        return new SingularModificationCommandBatch(_dependencies);
    }
}
