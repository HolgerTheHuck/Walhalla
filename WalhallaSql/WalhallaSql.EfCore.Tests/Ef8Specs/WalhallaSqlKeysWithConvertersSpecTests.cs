using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.TestUtilities;

namespace Microsoft.EntityFrameworkCore;

public sealed class WalhallaSqlKeysWithConvertersSpecTests(
    WalhallaSqlKeysWithConvertersSpecTests.KeysWithConvertersFixture fixture)
    : KeysWithConvertersTestBase<WalhallaSqlKeysWithConvertersSpecTests.KeysWithConvertersFixture>(fixture)
{
    public sealed class KeysWithConvertersFixture : KeysWithConvertersFixtureBase
    {
        protected override ITestStoreFactory TestStoreFactory
            => LayeredSqlTestStoreFactory.Instance;

        public override DbContextOptionsBuilder AddOptions(DbContextOptionsBuilder builder)
            => base.AddOptions(builder)
                .ConfigureWarnings(warnings => warnings.Ignore(CoreEventId.CollectionWithoutComparer));
    }
}
