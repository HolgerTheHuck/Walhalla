using Microsoft.EntityFrameworkCore.TestUtilities;

namespace Microsoft.EntityFrameworkCore;

public sealed class WalhallaSqlSerializationSpecTests(
    WalhallaSqlSerializationSpecTests.SerializationFixture fixture)
    : SerializationTestBase<WalhallaSqlSerializationSpecTests.SerializationFixture>(fixture)
{
    public sealed class SerializationFixture : F1FixtureBase<byte[]>
    {
        public override TestHelpers TestHelpers
            => LayeredSqlRelationalTestHelpers.Instance;

        protected override ITestStoreFactory TestStoreFactory
            => LayeredSqlTestStoreFactory.Instance;
    }
}
