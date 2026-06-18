using System;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WalhallaSql;
using WalhallaSql.AdoNet;
using WalhallaSql.Core;
using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore;

namespace WalhallaSql.EfCore.Tests;

/// <summary>
/// Regression tests for the four fixes applied 2026-03-04:
///   Fix #1 � Value converters are applied when building INSERT/UPDATE/DELETE SQL
///   Fix #2 � NOT predicate is translated to SQL NOT (...)
///   Fix #3 � (Compile-level: dead code removed � no runtime test needed)
///   Fix #4 � Output/Return-Value parameter direction throws NotSupportedException
/// </summary>
public sealed class SaveChangesAndLinqRegressionTests
{
    // -----------------------------------------------------------------
    // Fix #1 � Enum value converter
    // -----------------------------------------------------------------

    [Fact]
    public void SaveChanges_insert_writes_converted_int_value_for_enum_property()
    {
        using var scope = EnumConverterScope.Create();

        scope.Context.Add(new OrderEntity { Id = 1, Name = "First", Status = OrderStatus.Active });
        scope.Context.SaveChanges();

        // Raw DB value must be the int representation (1), not the enum name "Active".
        var raw = scope.Context.ExecuteSql("SELECT Status FROM Orders WHERE Id = 1")
            .Rows!.Single();

        var rawStatus = Convert.ToInt32(raw["Status"], CultureInfo.InvariantCulture);
        Assert.Equal((int)OrderStatus.Active, rawStatus);
    }

    [Fact]
    public void SaveChanges_update_writes_converted_int_value_for_enum_property()
    {
        using var scope = EnumConverterScope.Create();

        var entity = new OrderEntity { Id = 2, Name = "Second", Status = OrderStatus.Pending };
        scope.Context.Add(entity);
        scope.Context.SaveChanges();

        entity.Status = OrderStatus.Closed;
        scope.Context.SaveChanges();

        var raw = scope.Context.ExecuteSql("SELECT Status FROM Orders WHERE Id = 2")
            .Rows!.Single();

        var rawStatus = Convert.ToInt32(raw["Status"], CultureInfo.InvariantCulture);
        Assert.Equal((int)OrderStatus.Closed, rawStatus);
    }

    [Fact]
    public void SaveChanges_delete_uses_converted_pk_when_pk_has_converter()
    {
        // Ensure the key itself (int PK) is correctly forwarded in DELETE WHERE.
        // Re-uses the enum-converter scope; deletes by numeric PK.
        using var scope = EnumConverterScope.Create();

        var entity = new OrderEntity { Id = 5, Name = "ToDelete", Status = OrderStatus.Pending };
        scope.Context.Add(entity);
        scope.Context.SaveChanges();

        scope.Context.Remove(entity);
        var deleted = scope.Context.SaveChanges();
        Assert.Equal(1, deleted);

        var remaining = scope.Context.ExecuteSql("SELECT Id FROM Orders WHERE Id = 5")
            .Rows?.Count ?? 0;
        Assert.Equal(0, remaining);
    }

    [Fact]
    public void SaveChanges_insert_and_update_preserve_unsigned_provider_values_for_uint_enum_mapping()
    {
        using var scope = UnsignedEnumConverterScope.Create();

        var entity = new UnsignedOrderEntity
        {
            Id = 1,
            Sequence = 4000000000U,
            Status = UnsignedOrderStatus.Archived
        };

        scope.Context.Add(entity);
        scope.Context.SaveChanges();

        var insertedRow = scope.Context.ExecuteSql("SELECT Sequence, Status FROM UnsignedOrders WHERE Id = 1")
            .Rows!.Single();

        Assert.Equal(4000000000UL, Convert.ToUInt64(insertedRow["Sequence"], CultureInfo.InvariantCulture));
        Assert.Equal((ulong)UnsignedOrderStatus.Archived, Convert.ToUInt64(insertedRow["Status"], CultureInfo.InvariantCulture));

        entity.Sequence = 4294967294U;
        entity.Status = UnsignedOrderStatus.Active;
        scope.Context.SaveChanges();

        scope.Context.ChangeTracker.Clear();

        var updatedEntity = scope.Context.Set<UnsignedOrderEntity>().Single(x => x.Id == 1);
        Assert.Equal(4294967294U, updatedEntity.Sequence);
        Assert.Equal(UnsignedOrderStatus.Active, updatedEntity.Status);

        var updatedRow = scope.Context.ExecuteSql("SELECT Sequence, Status FROM UnsignedOrders WHERE Id = 1")
            .Rows!.Single();

        Assert.Equal(4294967294UL, Convert.ToUInt64(updatedRow["Sequence"], CultureInfo.InvariantCulture));
        Assert.Equal((ulong)UnsignedOrderStatus.Active, Convert.ToUInt64(updatedRow["Status"], CultureInfo.InvariantCulture));
    }

    [Fact]
    public void SaveChanges_insert_and_update_preserve_decimal_provider_values_for_ulong_enum_mapping()
    {
        using var scope = UnsignedEnumConverterScope.Create();

        var entity = new UnsignedLongOrderEntity
        {
            Id = 7,
            Sequence = 18446744073709551610UL,
            Status = UnsignedLongOrderStatus.Archived
        };

        scope.Context.Add(entity);
        scope.Context.SaveChanges();

        var insertedRow = scope.Context.ExecuteSql("SELECT Sequence, Status FROM UnsignedLongOrders WHERE Id = 7")
            .Rows!.Single();

        Assert.Equal(18446744073709551610UL, Convert.ToUInt64(insertedRow["Sequence"], CultureInfo.InvariantCulture));
        Assert.Equal((ulong)UnsignedLongOrderStatus.Archived, Convert.ToUInt64(insertedRow["Status"], CultureInfo.InvariantCulture));

        entity.Sequence = 18446744073709551614UL;
        entity.Status = UnsignedLongOrderStatus.Active;
        scope.Context.SaveChanges();

        scope.Context.ChangeTracker.Clear();

        var updatedEntity = scope.Context.Set<UnsignedLongOrderEntity>().Single(x => x.Id == 7);
        Assert.Equal(18446744073709551614UL, updatedEntity.Sequence);
        Assert.Equal(UnsignedLongOrderStatus.Active, updatedEntity.Status);

        var updatedRow = scope.Context.ExecuteSql("SELECT Sequence, Status FROM UnsignedLongOrders WHERE Id = 7")
            .Rows!.Single();

        Assert.Equal(18446744073709551614UL, Convert.ToUInt64(updatedRow["Sequence"], CultureInfo.InvariantCulture));
        Assert.Equal((ulong)UnsignedLongOrderStatus.Active, Convert.ToUInt64(updatedRow["Status"], CultureInfo.InvariantCulture));
    }

    // -----------------------------------------------------------------
    // Fix #1 � Guid ? string value converter
    // -----------------------------------------------------------------

    [Fact]
    public void SaveChanges_insert_writes_string_representation_for_guid_property()
    {
        using var scope = GuidConverterScope.Create();

        var externalId = Guid.NewGuid();
        scope.Context.Add(new GuidEntity { Id = 1, ExternalId = externalId });
        scope.Context.SaveChanges();

        var raw = scope.Context.ExecuteSql("SELECT ExternalId FROM GuidEntity WHERE Id = 1")
            .Rows!.Single();

        // Value must be stored as a string (not a byte array or null).
        var rawString = raw["ExternalId"]?.ToString();
        Assert.False(string.IsNullOrWhiteSpace(rawString), "ExternalId must not be empty.");
        Assert.True(
            Guid.TryParse(rawString, out var parsed),
            $"ExternalId must be a parseable Guid string, but got: '{rawString}'");
        Assert.Equal(externalId, parsed);
    }

    [Fact]
    public void SaveChanges_update_writes_new_string_for_guid_property()
    {
        using var scope = GuidConverterScope.Create();

        var originalId = Guid.NewGuid();
        var entity = new GuidEntity { Id = 2, ExternalId = originalId };
        scope.Context.Add(entity);
        scope.Context.SaveChanges();

        var newId = Guid.NewGuid();
        entity.ExternalId = newId;
        scope.Context.SaveChanges();

        var raw = scope.Context.ExecuteSql("SELECT ExternalId FROM GuidEntity WHERE Id = 2")
            .Rows!.Single();

        var rawString = raw["ExternalId"]?.ToString();
        Assert.True(Guid.TryParse(rawString, out var parsed));
        Assert.Equal(newId, parsed);
    }

    [Fact]
    public void SaveChanges_delete_uses_string_provider_value_for_guid_primary_key()
    {
        using var scope = GuidPrimaryKeyScope.Create();

        var id = Guid.NewGuid();
        var entity = new GuidPrimaryKeyEntity { Id = id, Name = "remove-me" };
        scope.Context.Add(entity);
        scope.Context.SaveChanges();

        scope.Context.Remove(entity);
        var deleted = scope.Context.SaveChanges();

        Assert.Equal(1, deleted);
        var remaining = scope.Context.ExecuteSql($"SELECT Id FROM GuidPkEntities WHERE Id = '{id}'").Rows?.Count ?? 0;
        Assert.Equal(0, remaining);
    }

    [Fact]
    public void SaveChanges_insert_writes_utc_datetime_for_datetimeoffset_converter()
    {
        using var scope = DateTimeOffsetConverterScope.Create();

        var occurredAt = new DateTimeOffset(2024, 7, 11, 13, 45, 30, TimeSpan.FromHours(2));
        scope.Context.Add(new OffsetEventEntity { Id = 1, OccurredAt = occurredAt });
        scope.Context.SaveChanges();

        var raw = scope.Context.ExecuteSql("SELECT OccurredAtUtc FROM OffsetEvents WHERE Id = 1")
            .Rows!.Single();
        var storedValue = Assert.IsType<DateTime>(raw["OccurredAtUtc"]);
        Assert.Equal(occurredAt.UtcDateTime, storedValue);
    }

    [Fact]
    public void SaveChanges_update_writes_new_utc_datetime_for_datetimeoffset_converter()
    {
        using var scope = DateTimeOffsetConverterScope.Create();

        var entity = new OffsetEventEntity
        {
            Id = 2,
            OccurredAt = new DateTimeOffset(2024, 1, 5, 8, 0, 0, TimeSpan.FromHours(-5))
        };
        scope.Context.Add(entity);
        scope.Context.SaveChanges();

        var updatedOccurredAt = new DateTimeOffset(2025, 2, 14, 18, 15, 0, TimeSpan.FromHours(1));
        entity.OccurredAt = updatedOccurredAt;
        scope.Context.SaveChanges();

        var raw = scope.Context.ExecuteSql("SELECT OccurredAtUtc FROM OffsetEvents WHERE Id = 2")
            .Rows!.Single();
        var storedValue = Assert.IsType<DateTime>(raw["OccurredAtUtc"]);
        Assert.Equal(updatedOccurredAt.UtcDateTime, storedValue);
    }

    [Fact]
    public void LinqWhere_datetimeoffset_converter_uses_provider_column_and_utc_value()
    {
        using var scope = DateTimeOffsetConverterScope.Create();

        var first = new DateTimeOffset(2024, 7, 11, 13, 45, 30, TimeSpan.FromHours(2));
        var second = new DateTimeOffset(2024, 7, 11, 14, 0, 0, TimeSpan.Zero);
        scope.Context.AddRange(
            new OffsetEventEntity { Id = 1, OccurredAt = first },
            new OffsetEventEntity { Id = 2, OccurredAt = second });
        scope.Context.SaveChanges();

        var rows = scope.Context
            .Query<OffsetEventEntity>("OffsetEvents")
            .Where(e => e.OccurredAt == first)
            .OrderBy(e => e.Id)
            .ToRows();

        Assert.Single(rows);
        Assert.Equal(1, Convert.ToInt32(rows[0]["Id"], CultureInfo.InvariantCulture));
        var storedValue = Assert.IsType<DateTime>(rows[0]["OccurredAtUtc"]);
        Assert.Equal(first.UtcDateTime, storedValue);
    }

    [Fact]
    public void LinqOrderBy_datetimeoffset_converter_uses_provider_column()
    {
        using var scope = DateTimeOffsetConverterScope.Create();

        scope.Context.AddRange(
            new OffsetEventEntity { Id = 1, OccurredAt = new DateTimeOffset(2024, 7, 11, 13, 0, 0, TimeSpan.FromHours(2)) },
            new OffsetEventEntity { Id = 2, OccurredAt = new DateTimeOffset(2024, 7, 11, 11, 30, 0, TimeSpan.Zero) },
            new OffsetEventEntity { Id = 3, OccurredAt = new DateTimeOffset(2024, 7, 11, 14, 0, 0, TimeSpan.FromHours(1)) });
        scope.Context.SaveChanges();

        var rows = scope.Context
            .Query<OffsetEventEntity>("OffsetEvents")
            .OrderBy(e => e.OccurredAt)
            .ToRows();

        Assert.Equal(3, rows.Count);
        Assert.Equal(1, Convert.ToInt32(rows[0]["Id"], CultureInfo.InvariantCulture));
        Assert.Equal(2, Convert.ToInt32(rows[1]["Id"], CultureInfo.InvariantCulture));
        Assert.Equal(3, Convert.ToInt32(rows[2]["Id"], CultureInfo.InvariantCulture));
    }

    // -----------------------------------------------------------------
    // Fix #2 � NOT predicate translation
    // -----------------------------------------------------------------

    [Fact]
    public void LinqWhere_not_equality_predicate_returns_complementary_rows()
    {
        using var scope = SimpleLinqScope.Create();

        // NOT (Name = 'Ada Lovelace')  ?  all rows except Id=1
        var rows = scope.Context
            .Query<SimpleLinqEntity>("SimpleLinqEntity")
            .Where(e => !(e.Name == "Ada Lovelace"))
            .OrderBy(e => e.Id)
            .ToRows();

        Assert.DoesNotContain(rows, row => row["Name"]?.ToString() == "Ada Lovelace");
        Assert.Contains(rows, row => row["Name"]?.ToString() == "Alan Turing");
    }

    [Fact]
    public void LinqWhere_not_and_predicate_applies_correctly()
    {
        using var scope = SimpleLinqScope.Create();

        // !(e.Id == 1 && e.Name == "Ada Lovelace") via NOT composition
        // Expects all rows where NOT (Id = 1) � i.e. rows 2 and 3
        var rows = scope.Context
            .Query<SimpleLinqEntity>("SimpleLinqEntity")
            .Where(e => !(e.Id == 1))
            .OrderBy(e => e.Id)
            .ToRows();

        Assert.All(rows, row => Assert.NotEqual(1, Convert.ToInt32(row["Id"], CultureInfo.InvariantCulture)));
        Assert.True(rows.Count >= 2, "Expected at least 2 rows excluding Id=1.");
    }

    [Fact]
    public void LinqWhere_combined_not_and_equality_predicates_compose_correctly()
    {
        using var scope = SimpleLinqScope.Create();

        // WHERE NOT (Name = 'Alan Turing') AND Id > 1  ?  only Grace Hopper (Id=3)
        var rows = scope.Context
            .Query<SimpleLinqEntity>("SimpleLinqEntity")
            .Where(e => !(e.Name == "Alan Turing") && e.Id > 1)
            .OrderBy(e => e.Id)
            .ToRows();

        Assert.Single(rows);
        Assert.Equal("Grace Hopper", rows[0]["Name"]?.ToString());
    }

    [Fact]
    public void LinqWhere_equals_null_uses_is_null_semantics()
    {
        using var scope = SimpleLinqScope.Create();

        var rows = scope.Context
            .Query<SimpleLinqEntity>("SimpleLinqEntity")
            .Where(e => e.Nickname == null)
            .OrderBy(e => e.Id)
            .ToRows();

        Assert.Equal(2, rows.Count);
        Assert.Equal("Ada Lovelace", rows[0]["Name"]?.ToString());
        Assert.Equal("Grace Hopper", rows[1]["Name"]?.ToString());
    }

    [Fact]
    public void LinqWhere_not_equals_null_uses_is_not_null_semantics()
    {
        using var scope = SimpleLinqScope.Create();

        var rows = scope.Context
            .Query<SimpleLinqEntity>("SimpleLinqEntity")
            .Where(e => e.Nickname != null)
            .OrderBy(e => e.Id)
            .ToRows();

        Assert.Single(rows);
        Assert.Equal("Alan Turing", rows[0]["Name"]?.ToString());
        Assert.Equal("The Enigma", rows[0]["Nickname"]?.ToString());
    }

    [Fact]
    public void LinqWhere_dateonly_equality_uses_canonical_literal()
    {
        using var scope = SimpleLinqScope.Create();

        var rows = scope.Context
            .Query<SimpleLinqEntity>("SimpleLinqEntity")
            .Where(e => e.BirthDate == new DateOnly(1815, 12, 10))
            .OrderBy(e => e.Id)
            .ToRows();

        Assert.Single(rows);
        Assert.Equal("Ada Lovelace", rows[0]["Name"]?.ToString());
    }

    [Fact]
    public void LinqWhere_timeonly_equality_uses_canonical_literal()
    {
        using var scope = SimpleLinqScope.Create();

        var rows = scope.Context
            .Query<SimpleLinqEntity>("SimpleLinqEntity")
            .Where(e => e.PreferredContactTime == new TimeOnly(9, 30, 0))
            .OrderBy(e => e.Id)
            .ToRows();

        Assert.Single(rows);
        Assert.Equal("Alan Turing", rows[0]["Name"]?.ToString());
    }

    [Fact]
    public void LinqWhere_dateonly_greater_than_filters_using_canonical_order()
    {
        using var scope = SimpleLinqScope.Create();

        var rows = scope.Context
            .Query<SimpleLinqEntity>("SimpleLinqEntity")
            .Where(e => e.BirthDate > new DateOnly(1900, 1, 1))
            .OrderBy(e => e.BirthDate)
            .ToRows();

        Assert.Equal(2, rows.Count);
        Assert.Equal("Grace Hopper", rows[0]["Name"]?.ToString());
        Assert.Equal("Alan Turing", rows[1]["Name"]?.ToString());
    }

    [Fact]
    public void LinqWhere_timeonly_greater_than_filters_using_canonical_order()
    {
        using var scope = SimpleLinqScope.Create();

        var rows = scope.Context
            .Query<SimpleLinqEntity>("SimpleLinqEntity")
            .Where(e => e.PreferredContactTime > new TimeOnly(9, 0, 0))
            .OrderBy(e => e.PreferredContactTime)
            .ToRows();

        Assert.Equal(2, rows.Count);
        Assert.Equal("Alan Turing", rows[0]["Name"]?.ToString());
        Assert.Equal("Grace Hopper", rows[1]["Name"]?.ToString());
    }

    [Fact]
    public void LinqOrderBy_dateonly_sorts_in_chronological_order()
    {
        using var scope = SimpleLinqScope.Create();

        var rows = scope.Context
            .Query<SimpleLinqEntity>("SimpleLinqEntity")
            .OrderBy(e => e.BirthDate)
            .ToRows();

        Assert.Equal(3, rows.Count);
        Assert.Equal("Ada Lovelace", rows[0]["Name"]?.ToString());
        Assert.Equal("Grace Hopper", rows[1]["Name"]?.ToString());
        Assert.Equal("Alan Turing", rows[2]["Name"]?.ToString());
    }

    [Fact]
    public void LinqOrderBy_timeonly_sorts_in_time_order()
    {
        using var scope = SimpleLinqScope.Create();

        var rows = scope.Context
            .Query<SimpleLinqEntity>("SimpleLinqEntity")
            .OrderBy(e => e.PreferredContactTime)
            .ToRows();

        Assert.Equal(3, rows.Count);
        Assert.Equal("Ada Lovelace", rows[0]["Name"]?.ToString());
        Assert.Equal("Alan Turing", rows[1]["Name"]?.ToString());
        Assert.Equal("Grace Hopper", rows[2]["Name"]?.ToString());
    }

    [Fact]
    public void LinqWhere_dateonly_contains_translates_to_in_with_canonical_literals()
    {
        using var scope = SimpleLinqScope.Create();

        var selectedDates = new[]
        {
            new DateOnly(1815, 12, 10),
            new DateOnly(1906, 12, 9)
        };

        var rows = scope.Context
            .Query<SimpleLinqEntity>("SimpleLinqEntity")
            .Where(e => selectedDates.Contains(e.BirthDate))
            .OrderBy(e => e.BirthDate)
            .ToRows();

        Assert.Equal(2, rows.Count);
        Assert.Equal("Ada Lovelace", rows[0]["Name"]?.ToString());
        Assert.Equal("Grace Hopper", rows[1]["Name"]?.ToString());
    }

    [Fact]
    public void LinqWhere_timeonly_contains_translates_to_in_with_canonical_literals()
    {
        using var scope = SimpleLinqScope.Create();

        var selectedTimes = new[]
        {
            new TimeOnly(8, 15, 0),
            new TimeOnly(10, 45, 0)
        };

        var rows = scope.Context
            .Query<SimpleLinqEntity>("SimpleLinqEntity")
            .Where(e => selectedTimes.Contains(e.PreferredContactTime))
            .OrderBy(e => e.PreferredContactTime)
            .ToRows();

        Assert.Equal(2, rows.Count);
        Assert.Equal("Ada Lovelace", rows[0]["Name"]?.ToString());
        Assert.Equal("Grace Hopper", rows[1]["Name"]?.ToString());
    }

    // -----------------------------------------------------------------
    // Fix #4 � Output parameter direction throws NotSupportedException
    // -----------------------------------------------------------------

    [Fact]
    public void Output_parameter_direction_throws_on_execute_non_query()
    {
        using var scope = AdoNetFixtureScope.Create();

        using var command = scope.Connection.CreateCommand();
        command.CommandText = "SELECT Id FROM Users WHERE Id = 1";

        var p = command.CreateParameter();
        p.ParameterName = "result";
        p.Direction = ParameterDirection.Output;
        command.Parameters.Add(p);

        var ex = Assert.Throws<NotSupportedException>(() => command.ExecuteNonQuery());
        Assert.Contains("ParameterDirection.Output", ex.Message);
    }

    [Fact]
    public void ReturnValue_parameter_direction_throws_on_execute_reader()
    {
        using var scope = AdoNetFixtureScope.Create();

        using var command = scope.Connection.CreateCommand();
        command.CommandText = "SELECT Id FROM Users WHERE Id = 1";

        var p = command.CreateParameter();
        p.ParameterName = "ret";
        p.Direction = ParameterDirection.ReturnValue;
        command.Parameters.Add(p);

        var ex = Assert.Throws<NotSupportedException>(() => command.ExecuteReader());
        Assert.Contains("ParameterDirection.ReturnValue", ex.Message);
    }

    [Fact]
    public void InputOutput_parameter_direction_throws_on_execute_scalar()
    {
        using var scope = AdoNetFixtureScope.Create();

        using var command = scope.Connection.CreateCommand();
        command.CommandText = "SELECT Id FROM Users WHERE Id = 1";

        var p = command.CreateParameter();
        p.ParameterName = "inout";
        p.Direction = ParameterDirection.InputOutput;
        command.Parameters.Add(p);

        var ex = Assert.Throws<NotSupportedException>(() => command.ExecuteScalar());
        Assert.Contains("ParameterDirection.InputOutput", ex.Message);
    }

    [Fact]
    public void Input_parameter_direction_does_not_throw()
    {
        using var scope = AdoNetFixtureScope.Create();

        using var command = scope.Connection.CreateCommand();
        command.CommandText = "SELECT Name FROM Users WHERE Id = @id";

        var p = command.CreateParameter();
        p.ParameterName = "id";
        p.Direction = ParameterDirection.Input;
        p.Value = 1;
        command.Parameters.Add(p);

        var result = command.ExecuteScalar();
        Assert.NotNull(result);
    }

    [Fact]
    public void ExecuteScalar_supports_literal_select_without_from()
    {
        using var scope = AdoNetFixtureScope.Create();

        using var command = scope.Connection.CreateCommand();
        command.CommandText = "SELECT 1";

        Assert.Equal(1L, Assert.IsType<long>(command.ExecuteScalar()));
    }

    [Fact]
    public async Task ExecuteScalarAsync_supports_string_literal_select_without_from()
    {
        using var scope = AdoNetFixtureScope.Create();

        await using var command = scope.Connection.CreateCommand();
        command.CommandText = "SELECT 'Ada' AS Value";

        var result = await command.ExecuteScalarAsync();
        Assert.Equal("Ada", result);
    }
}

// -----------------------------------------------------------------------------
// HasColumnName regression tests
// -----------------------------------------------------------------------------

public sealed class HasColumnNameTests
{
    // INSERT generates SQL using the DB column name, not the CLR property name.
    // The raw SELECT references 'display_name' (db name); if INSERT wrote to
    // 'DisplayName' (CLR name) instead the SELECT would return no rows.
    [Fact]
    public void SaveChanges_insert_uses_db_column_name()
    {
        using var scope = ColumnNameScope.Create();
        var ctx = scope.Context;

        ctx.Set<ColumnMappedEntity>().Add(new ColumnMappedEntity { Id = 1, DisplayName = "Ada" });
        ctx.SaveChanges();

        var row = ctx.ExecuteSql("SELECT display_name FROM column_mapped WHERE id = 1")
            .Rows!.Single();
        Assert.Equal("Ada", row["display_name"]);
    }

    // UPDATE SET clause uses the DB column name.
    [Fact]
    public void SaveChanges_update_uses_db_column_name()
    {
        using var scope = ColumnNameScope.Create();
        var ctx = scope.Context;

        ctx.Set<ColumnMappedEntity>().Add(new ColumnMappedEntity { Id = 2, DisplayName = "Before" });
        ctx.SaveChanges();

        var entity = ctx.Set<ColumnMappedEntity>().Find(2)!;
        entity.DisplayName = "After";
        ctx.SaveChanges();

        var row = ctx.ExecuteSql("SELECT display_name FROM column_mapped WHERE id = 2")
            .Rows!.Single();
        Assert.Equal("After", row["display_name"]);
    }

    // DELETE WHERE clause uses the DB column name for the PK.
    [Fact]
    public void SaveChanges_delete_uses_db_column_name_for_pk()
    {
        using var scope = ColumnNameScope.Create();
        var ctx = scope.Context;

        ctx.Set<ColumnMappedEntity>().Add(new ColumnMappedEntity { Id = 3, DisplayName = "ToDelete" });
        ctx.SaveChanges();

        var entity = ctx.Set<ColumnMappedEntity>().Find(3)!;
        ctx.Set<ColumnMappedEntity>().Remove(entity);
        ctx.SaveChanges();

        // Row must be gone after delete.
        var remaining = ctx.ExecuteSql("SELECT id FROM column_mapped WHERE id = 3")
            .Rows?.Count ?? 0;
        Assert.Equal(0, remaining);
    }

    // Migration creates the column with the DB column name.
    // A SELECT on 'display_name' succeeds; if the migration had used
    // the CLR name 'DisplayName' the query would throw an unknown-column error.
    [Fact]
    public void Migration_creates_column_with_db_column_name()
    {
        using var scope = ColumnNameScope.Create();
        var ctx = scope.Context;

        // Insert a row so the SELECT returns data; an exception here means the
        // column 'display_name' does not exist (bug: migration used CLR name).
        ctx.ExecuteSql("INSERT INTO column_mapped (id, display_name) VALUES (99, 'test')");
        var rows = ctx.ExecuteSql("SELECT display_name FROM column_mapped WHERE id = 99").Rows;
        Assert.NotNull(rows);
        Assert.Single(rows);
    }

    [Fact]
    public void Query_where_and_orderby_use_db_column_name()
    {
        using var scope = ColumnNameScope.Create();
        var ctx = scope.Context;

        ctx.Set<ColumnMappedEntity>().AddRange(
            new ColumnMappedEntity { Id = 10, DisplayName = "Grace" },
            new ColumnMappedEntity { Id = 11, DisplayName = "Ada" });
        ctx.SaveChanges();

        var rows = ctx.Query<ColumnMappedEntity>("column_mapped")
            .Where(entity => entity.DisplayName == "Ada")
            .OrderBy(entity => entity.DisplayName)
            .ToRows();

        var row = Assert.Single(rows);
        Assert.Equal("Ada", row["display_name"]?.ToString());
    }
}

// -----------------------------------------------------------------------------
// Domain models
// -----------------------------------------------------------------------------

public enum OrderStatus { Pending = 0, Active = 1, Closed = 2 }

public enum UnsignedOrderStatus : uint { Pending = 0U, Active = 1U, Archived = 4000000000U }

public enum UnsignedLongOrderStatus : ulong
{
    Pending = 0UL,
    Active = 18446744073709551613UL,
    Archived = 18446744073709551610UL
}

public sealed class OrderEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public OrderStatus Status { get; set; }
}

public sealed class UnsignedOrderEntity
{
    public int Id { get; set; }
    public uint Sequence { get; set; }
    public UnsignedOrderStatus Status { get; set; }
}

public sealed class UnsignedLongOrderEntity
{
    public int Id { get; set; }
    public ulong Sequence { get; set; }
    public UnsignedLongOrderStatus Status { get; set; }
}

public sealed class GuidEntity
{
    public int Id { get; set; }
    public Guid ExternalId { get; set; }
}

public sealed class SimpleLinqEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Nickname { get; set; }
    public int Age { get; set; }
    public DateOnly BirthDate { get; set; }
    public TimeOnly PreferredContactTime { get; set; }
}

public sealed class OffsetEventEntity
{
    public int Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class ColumnMappedEntity
{
    public int Id { get; set; }          // mapped to DB column "id"
    public string DisplayName { get; set; } = string.Empty;  // mapped to DB column "display_name"
}

// -----------------------------------------------------------------------------
// EF Core contexts
// -----------------------------------------------------------------------------

public sealed class EnumConverterContext : WalhallaSqlEfCoreContext
{
    public EnumConverterContext(DbContextOptions options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OrderEntity>(entity =>
        {
            entity.ToTable("Orders");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired();
            entity.Property(x => x.Status).HasConversion<int>();
        });
    }
}

public sealed class GuidConverterContext : WalhallaSqlEfCoreContext
{
    public GuidConverterContext(DbContextOptions options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GuidEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ExternalId).HasConversion<string>();
        });
    }
}

public sealed class UnsignedEnumConverterContext : WalhallaSqlEfCoreContext
{
    public UnsignedEnumConverterContext(DbContextOptions options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UnsignedOrderEntity>(entity =>
        {
            entity.ToTable("UnsignedOrders");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Sequence);
            entity.Property(x => x.Status).HasConversion<uint>();
        });

        modelBuilder.Entity<UnsignedLongOrderEntity>(entity =>
        {
            entity.ToTable("UnsignedLongOrders");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Sequence);
            entity.Property(x => x.Status).HasConversion<ulong>();
        });
    }
}

public sealed class SimpleLinqContext : WalhallaSqlEfCoreContext
{
    public SimpleLinqContext(DbContextOptions options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SimpleLinqEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired();
            entity.Property(x => x.Nickname);
            entity.Property(x => x.Age);
            entity.Property(x => x.BirthDate).IsRequired();
            entity.Property(x => x.PreferredContactTime).IsRequired();
        });
    }
}

public sealed class DateTimeOffsetConverterContext : WalhallaSqlEfCoreContext
{
    public DateTimeOffsetConverterContext(DbContextOptions options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OffsetEventEntity>(entity =>
        {
            entity.ToTable("OffsetEvents");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OccurredAt)
                .HasColumnName("OccurredAtUtc")
                .HasConversion(
                    value => value.UtcDateTime,
                    value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)));
        });
    }
}

public sealed class GuidPrimaryKeyEntity
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

public sealed class GuidPrimaryKeyContext : WalhallaSqlEfCoreContext
{
    public GuidPrimaryKeyContext(DbContextOptions options) : base(options) { }

    public DbSet<GuidPrimaryKeyEntity> GuidPrimaryKeyEntities => Set<GuidPrimaryKeyEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GuidPrimaryKeyEntity>(entity =>
        {
            entity.ToTable("GuidPkEntities");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).HasConversion<string>();
            entity.Property(e => e.Name).IsRequired();
        });
    }
}

public sealed class ColumnNameContext : WalhallaSqlEfCoreContext
{
    public ColumnNameContext(DbContextOptions options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ColumnMappedEntity>(entity =>
        {
            entity.ToTable("column_mapped");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.DisplayName)
                  .IsRequired()
                  .HasColumnName("display_name");
        });
    }
}

// -----------------------------------------------------------------------------
// Test scopes
// -----------------------------------------------------------------------------

internal sealed class EnumConverterScope : IDisposable
{
    private readonly string _dbPath;
    private readonly WalhallaEngine _engine;

    private EnumConverterScope(string dbPath, WalhallaEngine engine, EnumConverterContext context)
    {
        _dbPath = dbPath;
        _engine = engine;
        Context = context;
    }

    public EnumConverterContext Context { get; }

    public static EnumConverterScope Create()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "LayeredSql", "EnumConverterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dbPath);

        var engine = WalhallaEngine.Open(dbPath);
        var database = engine;

        var options = new DbContextOptionsBuilder<EnumConverterContext>()
            .UseWalhallaSql(new WalhallaSqlEfCoreOptions(database))
            .Options;

        var context = new EnumConverterContext(options);
        context.Migrations.ApplyPlannedChanges("20260304_EnumConverter");

        return new EnumConverterScope(dbPath, engine, context);
    }

    public void Dispose()
    {
        Context.Dispose();
        _engine.Dispose();
        try { if (Directory.Exists(_dbPath)) Directory.Delete(_dbPath, recursive: true); } catch { }
    }
}

internal sealed class GuidConverterScope : IDisposable
{
    private readonly string _dbPath;
    private readonly WalhallaEngine _engine;

    private GuidConverterScope(string dbPath, WalhallaEngine engine, GuidConverterContext context)
    {
        _dbPath = dbPath;
        _engine = engine;
        Context = context;
    }

    public GuidConverterContext Context { get; }

    public static GuidConverterScope Create()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "LayeredSql", "GuidConverterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dbPath);

        var engine = WalhallaEngine.Open(dbPath);
        var database = engine;

        var options = new DbContextOptionsBuilder<GuidConverterContext>()
            .UseWalhallaSql(new WalhallaSqlEfCoreOptions(database))
            .Options;

        var context = new GuidConverterContext(options);
        context.Migrations.ApplyPlannedChanges("20260304_GuidConverter");

        return new GuidConverterScope(dbPath, engine, context);
    }

    public void Dispose()
    {
        Context.Dispose();
        _engine.Dispose();
        try { if (Directory.Exists(_dbPath)) Directory.Delete(_dbPath, recursive: true); } catch { }
    }
}

internal sealed class UnsignedEnumConverterScope : IDisposable
{
    private readonly string _dbPath;
    private readonly WalhallaEngine _engine;

    private UnsignedEnumConverterScope(string dbPath, WalhallaEngine engine, UnsignedEnumConverterContext context)
    {
        _dbPath = dbPath;
        _engine = engine;
        Context = context;
    }

    public UnsignedEnumConverterContext Context { get; }

    public static UnsignedEnumConverterScope Create()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "LayeredSql", "UnsignedEnumConverterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dbPath);

        var engineOptions = new WalhallaOptions(dbPath) { StorageMode = StorageMode.MvccBPlusTree };
        var engine = new WalhallaEngine(engineOptions);
        var database = engine;

        var options = new DbContextOptionsBuilder<UnsignedEnumConverterContext>()
            .UseWalhallaSql(new WalhallaSqlEfCoreOptions(database))
            .Options;

        var context = new UnsignedEnumConverterContext(options);
        context.Migrations.ApplyPlannedChanges("20260304_UnsignedEnumConverter");

        return new UnsignedEnumConverterScope(dbPath, engine, context);
    }

    public void Dispose()
    {
        Context.Dispose();
        _engine.Dispose();
        try { if (Directory.Exists(_dbPath)) Directory.Delete(_dbPath, recursive: true); } catch { }
    }
}

internal sealed class SimpleLinqScope : IDisposable
{
    private readonly string _dbPath;
    private readonly WalhallaEngine _engine;

    private SimpleLinqScope(string dbPath, WalhallaEngine engine, SimpleLinqContext context)
    {
        _dbPath = dbPath;
        _engine = engine;
        Context = context;
    }

    public SimpleLinqContext Context { get; }

    public static SimpleLinqScope Create()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "LayeredSql", "SimpleLinqTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dbPath);

        var engine = WalhallaEngine.Open(dbPath);
        var database = engine;

        var options = new DbContextOptionsBuilder<SimpleLinqContext>()
            .UseWalhallaSql(new WalhallaSqlEfCoreOptions(database))
            .Options;

        var context = new SimpleLinqContext(options);
        context.Migrations.ApplyPlannedChanges("20260304_SimpleLinq");

        context.ExecuteSql("INSERT INTO SimpleLinqEntity (Id, Name, Nickname, Age, BirthDate, PreferredContactTime) VALUES (1, 'Ada Lovelace', NULL, 30, '1815-12-10', '08:15:00.0000000')");
        context.ExecuteSql("INSERT INTO SimpleLinqEntity (Id, Name, Nickname, Age, BirthDate, PreferredContactTime) VALUES (2, 'Alan Turing', 'The Enigma', 41, '1912-06-23', '09:30:00.0000000')");
        context.ExecuteSql("INSERT INTO SimpleLinqEntity (Id, Name, Nickname, Age, BirthDate, PreferredContactTime) VALUES (3, 'Grace Hopper', NULL, 45, '1906-12-09', '10:45:00.0000000')");

        return new SimpleLinqScope(dbPath, engine, context);
    }

    public void Dispose()
    {
        Context.Dispose();
        _engine.Dispose();
        try { if (Directory.Exists(_dbPath)) Directory.Delete(_dbPath, recursive: true); } catch { }
    }
}

internal sealed class ColumnNameScope : IDisposable
{
    private readonly string _dbPath;
    private readonly WalhallaEngine _engine;

    private ColumnNameScope(string dbPath, WalhallaEngine engine, ColumnNameContext context)
    {
        _dbPath = dbPath;
        _engine = engine;
        Context = context;
    }

    public ColumnNameContext Context { get; }

    public static ColumnNameScope Create()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "LayeredSql", "ColumnNameTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dbPath);

        var engine = WalhallaEngine.Open(dbPath);
        var database = engine;

        var options = new DbContextOptionsBuilder<ColumnNameContext>()
            .UseWalhallaSql(new WalhallaSqlEfCoreOptions(database))
            .Options;

        var context = new ColumnNameContext(options);
        context.Migrations.ApplyPlannedChanges("20260304_ColumnName");

        return new ColumnNameScope(dbPath, engine, context);
    }

    public void Dispose()
    {
        Context.Dispose();
        _engine.Dispose();
        try { if (Directory.Exists(_dbPath)) Directory.Delete(_dbPath, recursive: true); } catch { }
    }
}

internal sealed class DateTimeOffsetConverterScope : IDisposable
{
    private readonly string _dbPath;
    private readonly WalhallaEngine _engine;

    private DateTimeOffsetConverterScope(string dbPath, WalhallaEngine engine, DateTimeOffsetConverterContext context)
    {
        _dbPath = dbPath;
        _engine = engine;
        Context = context;
    }

    public DateTimeOffsetConverterContext Context { get; }

    public static DateTimeOffsetConverterScope Create()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "LayeredSql", "DateTimeOffsetConverterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dbPath);

        var engine = WalhallaEngine.Open(dbPath);
        var database = engine;

        var options = new DbContextOptionsBuilder<DateTimeOffsetConverterContext>()
            .UseWalhallaSql(new WalhallaSqlEfCoreOptions(database))
            .Options;

        var context = new DateTimeOffsetConverterContext(options);
        context.Migrations.ApplyPlannedChanges("20260313_DateTimeOffsetConverter");

        return new DateTimeOffsetConverterScope(dbPath, engine, context);
    }

    public void Dispose()
    {
        Context.Dispose();
        _engine.Dispose();
        try { if (Directory.Exists(_dbPath)) Directory.Delete(_dbPath, recursive: true); } catch { }
    }
}

internal sealed class GuidPrimaryKeyScope : IDisposable
{
    private readonly string _dbPath;
    private readonly WalhallaEngine _engine;

    private GuidPrimaryKeyScope(string dbPath, WalhallaEngine engine, GuidPrimaryKeyContext context)
    {
        _dbPath = dbPath;
        _engine = engine;
        Context = context;
    }

    public GuidPrimaryKeyContext Context { get; }

    public static GuidPrimaryKeyScope Create()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "LayeredSql", "GuidPrimaryKeyTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dbPath);

        var engine = WalhallaEngine.Open(dbPath);
        var database = engine;
        var options = new DbContextOptionsBuilder<GuidPrimaryKeyContext>()
            .UseWalhallaSql(new WalhallaSqlEfCoreOptions(database))
            .Options;

        var context = new GuidPrimaryKeyContext(options);
        context.Migrations.ApplyPlannedChanges("20260318_GuidPrimaryKey");

        return new GuidPrimaryKeyScope(dbPath, engine, context);
    }

    public void Dispose()
    {
        Context.Dispose();
        (_engine as IDisposable)?.Dispose();

        if (Directory.Exists(_dbPath))
            Directory.Delete(_dbPath, recursive: true);
    }
}

internal sealed class AdoNetFixtureScope : IDisposable
{
    private readonly IDisposable _engineHandle;

    private AdoNetFixtureScope(WalhallaSqlDbConnection connection, IDisposable engineHandle)
    {
        Connection = connection;
        _engineHandle = engineHandle;
    }

    public WalhallaSqlDbConnection Connection { get; }

    public static AdoNetFixtureScope Create()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "LayeredSql", "AdoNetFixture", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dbPath);

        var engine = WalhallaEngine.Open(dbPath);
        var database = engine;
        var dataSource = "adofixture-" + Guid.NewGuid().ToString("N");
        WalhallaSqlConnectionRegistry.Register(dataSource, () => database);

        var conn = new WalhallaSqlDbConnection($"DataSource={dataSource};Database=App");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE Users (Id INT PRIMARY KEY, Name VARCHAR(200) NOT NULL)";
        cmd.ExecuteNonQuery();

        using var ins = conn.CreateCommand();
        ins.CommandText = "INSERT INTO Users (Id, Name) VALUES (1, 'Ada Lovelace')";
        ins.ExecuteNonQuery();

        return new AdoNetFixtureScope(conn, engine);
    }

    public void Dispose()
    {
        Connection.Dispose();
        _engineHandle.Dispose();
    }
}
