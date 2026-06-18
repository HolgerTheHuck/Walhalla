using Microsoft.EntityFrameworkCore.TestUtilities;

namespace Microsoft.EntityFrameworkCore;

public sealed class WalhallaSqlSeedingSpecTests : SeedingTestBase
{
    protected override TestStore TestStore
        => LayeredSqlSpecTestStore.GetOrCreate("SeedingTest");

    protected override SeedingContext CreateContextWithEmptyDatabase(string testId)
        => new LayeredSqlSeedingContext(testId);

    private sealed class LayeredSqlSeedingContext(string testId) : SeedingContext(testId)
    {
        private readonly LayeredSqlSpecTestStore _testStore = LayeredSqlSpecTestStore.GetOrCreate($"Seeds{testId}");

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => _testStore.AddProviderOptions(optionsBuilder);
    }
}
