using Microsoft.EntityFrameworkCore.TestUtilities;
using Xunit;

namespace Microsoft.EntityFrameworkCore;

[Collection(SharedTypeQuerySpecSerialCollection.SharedTypeQueryCollectionName)]
public sealed class WalhallaSqlSharedTypeQuerySpecTests : SharedTypeQueryTestBase
{
    protected override ITestStoreFactory TestStoreFactory
        => LayeredSqlTestStoreFactory.Instance;
}
