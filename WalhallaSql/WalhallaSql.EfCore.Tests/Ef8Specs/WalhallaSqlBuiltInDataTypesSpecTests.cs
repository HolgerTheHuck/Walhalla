using Microsoft.EntityFrameworkCore.TestUtilities;

namespace Microsoft.EntityFrameworkCore;

public sealed class WalhallaSqlBuiltInDataTypesSpecTests(
    WalhallaSqlBuiltInDataTypesSpecTests.BuiltInDataTypesFixture fixture)
    : BuiltInDataTypesTestBase<WalhallaSqlBuiltInDataTypesSpecTests.BuiltInDataTypesFixture>(fixture)
{
    public sealed class BuiltInDataTypesFixture : BuiltInDataTypesFixtureBase
    {
        protected override string StoreName
            => nameof(WalhallaSqlBuiltInDataTypesSpecTests);

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
