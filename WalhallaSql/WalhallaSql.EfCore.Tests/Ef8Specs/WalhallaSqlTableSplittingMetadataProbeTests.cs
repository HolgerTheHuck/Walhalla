using System.Text;
using WalhallaSql.AdoNet;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.TestModels.TransportationModel;
using Microsoft.EntityFrameworkCore.TestUtilities;
using Xunit.Abstractions;

namespace Microsoft.EntityFrameworkCore;

[Trait("Category", "EF8MetadataProbe")]
[Collection(TableSplittingSpecSerialCollection.Name)]
public sealed class WalhallaSqlTableSplittingMetadataProbeTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task Diag_can_update_just_dependents_raw_sql()
    {
        await using var harness = new TableSplittingMetadataProbeHarness(testOutputHelper);
        await harness.InitializeAsync(harness.OnModelCreatingPublic, seed: true);

        // Raw SQL: Run the Engine query and see what comes back
        using (var ctx = harness.CreateContextPublic())
        {
            var conn = ctx.Database.GetDbConnection() as WalhallaSqlDbConnection;
            Assert.NotNull(conn);

            // This is the exact SQL EF Core generates for context.Set<Engine>().OrderBy(o => o.VehicleName).First()
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT v.Name, v.Computed, v.Description, v.Engine_Discriminator, t.Name, t.Capacity, t.FuelTank_Discriminator, t.FuelType, t.GrainGeometry, t0.Name, t0.Capacity, t0.FuelTank_Discriminator, t0.FuelType, t0.GrainGeometry
FROM Vehicles AS v
LEFT JOIN (
    SELECT v0.Name, v0.Capacity, v0.FuelTank_Discriminator, v0.FuelType, v0.GrainGeometry
    FROM Vehicles AS v0
    WHERE v0.Capacity IS NOT NULL AND v0.FuelTank_Discriminator IS NOT NULL
) AS t ON v.Name = t.Name
LEFT JOIN (
    SELECT v1.Name, v1.Capacity, v1.FuelTank_Discriminator, v1.FuelType, v1.GrainGeometry
    FROM Vehicles AS v1
    WHERE v1.FuelTank_Discriminator = 'SolidFuelTank'
) AS t0 ON v.Name = t0.Name
WHERE v.Computed IS NOT NULL AND v.Engine_Discriminator IS NOT NULL
ORDER BY v.Name
FETCH FIRST 1 ROWS ONLY";
            using var rdr = cmd.ExecuteReader();
            var sb = new StringBuilder();
            sb.AppendLine($"FieldCount={rdr.FieldCount}");
            for (int i = 0; i < rdr.FieldCount; i++)
                sb.AppendLine($"  Col[{i}] = {rdr.GetName(i)}");
            while (rdr.Read())
            {
                sb.AppendLine("Row:");
                for (int i = 0; i < rdr.FieldCount; i++)
                    sb.AppendLine($"  [{i}] {rdr.GetName(i)} = {(rdr.IsDBNull(i) ? "NULL" : rdr.GetValue(i)?.ToString())}");
            }
            testOutputHelper.WriteLine(sb.ToString());
        }

        Assert.True(true, "Output above - check test output");
    }

    [Fact]
    public async Task Transportation_model_reports_vehicle_table_splitting_metadata()
    {
        await using var harness = new TableSplittingMetadataProbeHarness(testOutputHelper);
        var report = await harness.InspectVehiclesAsync();

        testOutputHelper.WriteLine(report);
        Assert.Contains("Operator_Discriminator", report, StringComparison.Ordinal);
    }

    private sealed class TableSplittingMetadataProbeHarness(ITestOutputHelper testOutputHelper)
        : TableSplittingTestBase(testOutputHelper)
    {
        protected override ITestStoreFactory TestStoreFactory
            => LayeredSqlTestStoreFactory.Instance;

        // Public wrappers for protected members
        public new Task InitializeAsync(Action<ModelBuilder> onModelCreating, bool seed = true)
            => base.InitializeAsync(onModelCreating, seed);

        public DbContext CreateContextPublic() => base.CreateContext();
        public Action<ModelBuilder> OnModelCreatingPublic => OnModelCreating;

        public async Task<string> InspectVehiclesAsync()
        {
            await InitializeAsync(OnModelCreating, seed: false);

            await using var context = CreateContext();

            var vehicleEntities = context.Model.GetEntityTypes()
                .Where(entity => string.Equals(entity.GetTableName(), "Vehicles", StringComparison.OrdinalIgnoreCase))
                .OrderBy(static entity => entity.Name, StringComparer.Ordinal)
                .ToArray();

            Assert.NotEmpty(vehicleEntities);

            var report = new StringBuilder();
            report.AppendLine("TableSplitting InitializeAsync metadata for Vehicles:");

            foreach (var entity in vehicleEntities)
            {
                var storeObject = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());

                report.AppendLine($"Entity={entity.Name}");
                report.AppendLine($"  BaseType={entity.BaseType?.Name ?? "<none>"}");
                report.AppendLine($"  Table={entity.GetSchema() ?? "<default>"}.{entity.GetTableName()}");
                report.AppendLine($"  DiscriminatorProperty={entity.FindDiscriminatorProperty()?.Name ?? "<none>"}");
                report.AppendLine($"  DiscriminatorValue={entity.GetDiscriminatorValue() ?? "<null>"}");

                foreach (var foreignKey in entity.GetForeignKeys().OrderBy(static fk => fk.PrincipalEntityType.Name, StringComparer.Ordinal))
                {
                    report.AppendLine(
                        $"  FK={foreignKey.PrincipalEntityType.Name} Unique={foreignKey.IsUnique} Ownership={foreignKey.IsOwnership} RequiredDependent={foreignKey.IsRequiredDependent} Props=[{string.Join(", ", foreignKey.Properties.Select(static property => property.Name))}]");
                }

                foreach (var property in entity.GetProperties()
                             .Where(property =>
                                 string.Equals(property.GetColumnName(storeObject), "Discriminator", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(property.GetColumnName(storeObject), "Operator_Discriminator", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(property.GetColumnName(storeObject), "Name", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(property.GetColumnName(storeObject), "SeatingCapacity", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(property.GetColumnName(storeObject), "Operator_Name", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(property.GetColumnName(storeObject), "Operator_DisplayName", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(static property => property.Name, StringComparer.Ordinal))
                {
                    report.AppendLine(
                        $"  Property={property.Name} Column={property.GetColumnName(storeObject)} Nullable={property.IsColumnNullable(storeObject)} Shadow={property.IsShadowProperty()} PK={property.IsPrimaryKey()} Discriminator={ReferenceEquals(entity.FindDiscriminatorProperty(), property)}");
                }
            }

            return report.ToString();
        }
    }
}
