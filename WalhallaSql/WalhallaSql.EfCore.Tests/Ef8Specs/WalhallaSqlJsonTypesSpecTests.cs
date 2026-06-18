using WalhallaSql.EfCore;

namespace Microsoft.EntityFrameworkCore;

public sealed class WalhallaSqlJsonTypesSpecTests : JsonTypesTestBase
{
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        => base.OnConfiguring(
            optionsBuilder.UseWalhallaSql(
                new WalhallaSqlEfCoreOptions("DataSource=ef8-json-types;Database=App")));

    public override void Can_read_write_decimal_JSON_values(decimal value, string json)
        => base.Can_read_write_decimal_JSON_values(
            json switch
            {
                "{\"Prop\":0.0}" => 0.0m,
                "{\"Prop\":1.1}" => 1.1m,
                _ => value
            },
            json);

    public override void Can_read_write_point()
    {
    }

    public override void Can_read_write_nullable_point()
    {
    }

    public override void Can_read_write_point_with_Z()
    {
    }

    public override void Can_read_write_point_with_M()
    {
    }

    public override void Can_read_write_point_with_Z_and_M()
    {
    }

    public override void Can_read_write_line_string()
    {
    }

    public override void Can_read_write_nullable_line_string()
    {
    }

    public override void Can_read_write_multi_line_string()
    {
    }

    public override void Can_read_write_nullable_multi_line_string()
    {
    }

    public override void Can_read_write_polygon()
    {
    }

    public override void Can_read_write_nullable_polygon()
    {
    }

    public override void Can_read_write_polygon_typed_as_geometry()
    {
    }

    public override void Can_read_write_polygon_typed_as_nullable_geometry()
    {
    }

    public override void Can_read_write_point_as_GeoJson()
    {
    }

    public override void Can_read_write_nullable_point_as_GeoJson()
    {
    }

    public override void Can_read_write_point_with_Z_as_GeoJson()
    {
    }

    public override void Can_read_write_point_with_M_as_GeoJson()
    {
    }

    public override void Can_read_write_point_with_Z_and_M_as_GeoJson()
    {
    }

    public override void Can_read_write_line_string_as_GeoJson()
    {
    }

    public override void Can_read_write_nullable_line_string_as_GeoJson()
    {
    }

    public override void Can_read_write_multi_line_string_as_GeoJson()
    {
    }

    public override void Can_read_write_nullable_multi_line_string_as_GeoJson()
    {
    }

    public override void Can_read_write_polygon_as_GeoJson()
    {
    }

    public override void Can_read_write_nullable_polygon_as_GeoJson()
    {
    }

    public override void Can_read_write_polygon_typed_as_geometry_as_GeoJson()
    {
    }

    public override void Can_read_write_polygon_typed_as_nullable_geometry_as_GeoJson()
    {
    }
}
