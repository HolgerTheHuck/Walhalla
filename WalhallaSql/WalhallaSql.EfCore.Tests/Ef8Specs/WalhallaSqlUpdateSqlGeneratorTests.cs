using System.Text;
using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.TestUtilities;

namespace Microsoft.EntityFrameworkCore.Update;

public sealed class LayeredSqlUpdateSqlGeneratorTests : UpdateSqlGeneratorTestBase
{
    protected override string Identity
        => "last_insert_rowid()";

    protected override string RowsAffected
        => "changes()";

    protected override TestHelpers TestHelpers
        => LayeredSqlRelationalTestHelpers.Instance;

    protected override IUpdateSqlGenerator CreateSqlGenerator()
    {
        var options = new DbContextOptionsBuilder()
            .UseWalhallaSql(new WalhallaSqlEfCoreOptions("DataSource=ef8spec-update-sql-generator;Database=App"))
            .Options;

        var context = new UpdateSqlGeneratorContext(options);
        return context.GetService<IUpdateSqlGenerator>();
    }

    public override void AppendInsertOperation_appends_insert_and_select_rowcount_if_no_store_generated_columns_exist_or_conditions_exist()
    {
        var stringBuilder = new StringBuilder();
        var command = CreateInsertCommand(false, false);

        CreateSqlGenerator().AppendInsertOperation(stringBuilder, command, 0);

        AssertBaseline(
            """
INSERT INTO "dbo"."Ducks" ("Id", "Name", "Quacks", "ConcurrencyToken")
VALUES (@p0, @p1, @p2, @p3);
SELECT changes();

""",
            stringBuilder.ToString());
    }

    protected override void AppendDeleteOperation_creates_full_delete_command_text_verification(StringBuilder stringBuilder)
        => AssertBaseline(
            """
DELETE FROM "dbo"."Ducks"
WHERE "Id" = @p0;
SELECT changes();

""",
            stringBuilder.ToString());

    protected override void AppendDeleteOperation_creates_full_delete_command_text_with_concurrency_check_verification(StringBuilder stringBuilder)
        => AssertBaseline(
            """
DELETE FROM "dbo"."Ducks"
WHERE "Id" = @p0 AND "ConcurrencyToken" IS NULL;
SELECT changes();

""",
            stringBuilder.ToString());

    protected override void AppendInsertOperation_for_all_store_generated_columns_verification(StringBuilder stringBuilder)
        => AssertBaseline(
            """
INSERT INTO "dbo"."Ducks"
DEFAULT VALUES;
SELECT "Id", "Computed"
FROM "dbo"."Ducks"
WHERE changes() = 1 AND "Id" = last_insert_rowid();

""",
            stringBuilder.ToString());

    protected override void AppendInsertOperation_for_only_identity_verification(StringBuilder stringBuilder)
        => AssertBaseline(
            """
INSERT INTO "dbo"."Ducks" ("Name", "Quacks", "ConcurrencyToken")
VALUES (@p0, @p1, @p2);
SELECT "Id"
FROM "dbo"."Ducks"
WHERE changes() = 1 AND "Id" = last_insert_rowid();

""",
            stringBuilder.ToString());

    protected override void AppendInsertOperation_for_only_single_identity_columns_verification(StringBuilder stringBuilder)
        => AssertBaseline(
            """
INSERT INTO "dbo"."Ducks"
DEFAULT VALUES;
SELECT "Id"
FROM "dbo"."Ducks"
WHERE changes() = 1 AND "Id" = last_insert_rowid();

""",
            stringBuilder.ToString());

    protected override void AppendInsertOperation_for_store_generated_columns_but_no_identity_verification(StringBuilder stringBuilder)
        => AssertBaseline(
            """
INSERT INTO "dbo"."Ducks" ("Id", "Name", "Quacks", "ConcurrencyToken")
VALUES (@p0, @p1, @p2, @p3);
SELECT "Computed"
FROM "dbo"."Ducks"
WHERE changes() = 1 AND "Id" = @p0;

""",
            stringBuilder.ToString());

    protected override void AppendInsertOperation_insert_if_store_generated_columns_exist_verification(StringBuilder stringBuilder)
        => AssertBaseline(
            """
INSERT INTO "dbo"."Ducks" ("Name", "Quacks", "ConcurrencyToken")
VALUES (@p0, @p1, @p2);
SELECT "Id", "Computed"
FROM "dbo"."Ducks"
WHERE changes() = 1 AND "Id" = last_insert_rowid();

""",
            stringBuilder.ToString());

    protected override void AppendUpdateOperation_appends_where_for_concurrency_token_verification(StringBuilder stringBuilder)
        => AssertBaseline(
            """
UPDATE "dbo"."Ducks" SET "Name" = @p0, "Quacks" = @p1, "ConcurrencyToken" = @p2
WHERE "Id" = @p3 AND "ConcurrencyToken" IS NULL;
SELECT changes();

""",
            stringBuilder.ToString());

    protected override void AppendUpdateOperation_for_computed_property_verification(StringBuilder stringBuilder)
        => AssertBaseline(
            """
UPDATE "dbo"."Ducks" SET "Name" = @p0, "Quacks" = @p1, "ConcurrencyToken" = @p2
WHERE "Id" = @p3;
SELECT "Computed"
FROM "dbo"."Ducks"
WHERE changes() = 1 AND "Id" = @p3;

""",
            stringBuilder.ToString());

    protected override void AppendUpdateOperation_if_store_generated_columns_dont_exist_verification(StringBuilder stringBuilder)
        => AssertBaseline(
            """
UPDATE "dbo"."Ducks" SET "Name" = @p0, "Quacks" = @p1, "ConcurrencyToken" = @p2
WHERE "Id" = @p3;
SELECT changes();

""",
            stringBuilder.ToString());

    protected override void AppendUpdateOperation_if_store_generated_columns_exist_verification(StringBuilder stringBuilder)
        => AssertBaseline(
            """
UPDATE "dbo"."Ducks" SET "Name" = @p0, "Quacks" = @p1, "ConcurrencyToken" = @p2
WHERE "Id" = @p3 AND "ConcurrencyToken" IS NULL;
SELECT "Computed"
FROM "dbo"."Ducks"
WHERE changes() = 1 AND "Id" = @p3;

""",
            stringBuilder.ToString());

    private sealed class UpdateSqlGeneratorContext(DbContextOptions options) : DbContext(options);

    private static void AssertBaseline(string expected, string actual)
        => Assert.Equal(expected.TrimEnd(), actual.TrimEnd(), ignoreLineEndingDifferences: true);
}
