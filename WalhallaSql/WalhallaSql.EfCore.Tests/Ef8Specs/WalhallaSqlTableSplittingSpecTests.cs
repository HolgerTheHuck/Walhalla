using Microsoft.EntityFrameworkCore.TestUtilities;
using Xunit.Abstractions;

namespace Microsoft.EntityFrameworkCore;

[Collection(TableSplittingSpecSerialCollection.Name)]
public sealed class WalhallaSqlTableSplittingSpecTests(ITestOutputHelper testOutputHelper)
	: TableSplittingTestBase(testOutputHelper)
{
	protected override ITestStoreFactory TestStoreFactory
		=> LayeredSqlTestStoreFactory.Instance;
}
