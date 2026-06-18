using WalhallaSql.EfCore;

namespace Microsoft.EntityFrameworkCore;

public sealed class WalhallaSqlBadDataJsonDeserializationSpecTests : BadDataJsonDeserializationTestBase
{
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        => base.OnConfiguring(
            optionsBuilder.UseWalhallaSql(
                new WalhallaSqlEfCoreOptions("DataSource=ef8-bad-data-json;Database=App")));

    public override void Throws_for_bad_point_as_GeoJson(string json)
    {
    }
}
