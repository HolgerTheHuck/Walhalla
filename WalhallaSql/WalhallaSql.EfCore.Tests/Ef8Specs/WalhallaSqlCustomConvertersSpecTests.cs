using Microsoft.EntityFrameworkCore.TestUtilities;

namespace Microsoft.EntityFrameworkCore;

public sealed class WalhallaSqlCustomConvertersSpecTests(
    WalhallaSqlCustomConvertersSpecTests.CustomConvertersFixture fixture)
    : CustomConvertersTestBase<WalhallaSqlCustomConvertersSpecTests.CustomConvertersFixture>(fixture)
{
    public override void Collection_enum_as_string_Contains()
    {
        using var context = CreateContext();
        var sameRole = Roles.Seller;
        var query = context.Set<CollectionEnum>().Where(e => e.Roles.Contains(sameRole)).ToList();

        Assert.Single(query);
    }

    public sealed class CustomConvertersFixture : CustomConvertersFixtureBase
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
