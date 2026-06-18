using Microsoft.EntityFrameworkCore.TestUtilities;

namespace Microsoft.EntityFrameworkCore;

public sealed class WalhallaSqlConvertToProviderTypesSpecTests(
    WalhallaSqlConvertToProviderTypesSpecTests.ConvertToProviderTypesFixture fixture)
    : ConvertToProviderTypesTestBase<WalhallaSqlConvertToProviderTypesSpecTests.ConvertToProviderTypesFixture>(fixture)
{
    public sealed class ConvertToProviderTypesFixture : ConvertToProviderTypesFixtureBase
    {
        protected override ITestStoreFactory TestStoreFactory
            => LayeredSqlTestStoreFactory.Instance;

        public override bool StrictEquality
            => true;

        public override bool SupportsAnsi
            => false;

        public override bool SupportsUnicodeToAnsiConversion
            => false;

        public override bool SupportsLargeStringComparisons
            => false;

        public override bool SupportsBinaryKeys
            => false;

        public override bool SupportsDecimalComparisons
            => true;

        public override DateTime DefaultDateTime
            => new(1973, 9, 3);

        public override bool PreservesDateTimeKind
            => true;
    }
}
