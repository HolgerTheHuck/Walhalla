using WalhallaSql.EfCore;

namespace Microsoft.EntityFrameworkCore;

public sealed class WalhallaSqlModelBuilding101SpecTests : ModelBuilding101RelationalTestBase
{
    protected override DbContextOptionsBuilder ConfigureContext(DbContextOptionsBuilder optionsBuilder)
        => optionsBuilder.UseWalhallaSql(new WalhallaSqlEfCoreOptions("DataSource=ef8-model101;Database=App"));
}
