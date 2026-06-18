using Xunit;

namespace Microsoft.EntityFrameworkCore;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TableSplittingSpecSerialCollection
{
    public const string Name = "EF8 TableSplitting Serial";
}
