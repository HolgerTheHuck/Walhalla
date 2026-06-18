using Microsoft.EntityFrameworkCore.TestUtilities;
using Xunit;

namespace Microsoft.EntityFrameworkCore.Query;

[Collection(SharedTypeQuerySpecSerialCollection.SharedTypeQueryCollectionName)]
public sealed class WalhallaSqlSharedTypeQueryRelationalSpecTests : SharedTypeQueryRelationalTestBase
{
    protected override ITestStoreFactory TestStoreFactory
        => LayeredSqlTestStoreFactory.Instance;
}
