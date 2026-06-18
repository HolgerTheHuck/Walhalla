using Microsoft.EntityFrameworkCore.TestUtilities;

namespace Microsoft.EntityFrameworkCore;

public sealed class WalhallaSqlNotificationEntitiesSpecTests(
    WalhallaSqlNotificationEntitiesSpecTests.NotificationEntitiesFixture fixture)
    : NotificationEntitiesTestBase<WalhallaSqlNotificationEntitiesSpecTests.NotificationEntitiesFixture>(fixture)
{
    public sealed class NotificationEntitiesFixture : NotificationEntitiesFixtureBase
    {
        protected override ITestStoreFactory TestStoreFactory
            => LayeredSqlTestStoreFactory.Instance;
    }
}
