using Microsoft.EntityFrameworkCore.TestUtilities;

namespace Microsoft.EntityFrameworkCore;

public sealed class WalhallaSqlCommandInterceptionSpecTests(
    WalhallaSqlCommandInterceptionSpecTests.CommandInterceptionFixture fixture)
    : CommandInterceptionTestBase(fixture), IClassFixture<WalhallaSqlCommandInterceptionSpecTests.CommandInterceptionFixture>
{
    public sealed class CommandInterceptionFixture : WalhallaSqlInterceptionSpecFixtureBase
    {
        protected override string StoreName
            => nameof(WalhallaSqlCommandInterceptionSpecTests);
    }
}
