using Microsoft.EntityFrameworkCore.TestUtilities;

namespace Microsoft.EntityFrameworkCore;

public sealed class WalhallaSqlDataBindingSpecTests(
    WalhallaSqlDataBindingSpecTests.DataBindingFixture fixture)
    : DataBindingTestBase<WalhallaSqlDataBindingSpecTests.DataBindingFixture>(fixture)
{
    public sealed class DataBindingFixture : F1FixtureBase<byte[]>
    {
        public override TestHelpers TestHelpers
            => LayeredSqlRelationalTestHelpers.Instance;

        protected override ITestStoreFactory TestStoreFactory
            => LayeredSqlTestStoreFactory.Instance;
    }
}
