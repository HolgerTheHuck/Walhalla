using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using WalhallaSql;
using WalhallaSql.Sql;
using Xunit;

namespace WalhallaSql.Tests;

public sealed class RowCodecUnsignedTests
{
    [Fact]
    public void Int64_column_roundtrips_uint_max_minus_one()
    {
        var columns = new List<SqlColumnDefinition>
        {
            new("Id", SqlScalarType.Int32, IsNullable: false, IsPrimaryKey: true, IsUnique: true),
            new("Seq", SqlScalarType.Int64, IsNullable: false, IsPrimaryKey: false, IsUnique: false)
        };
        var tableDef = new SqlTableDefinition("T", columns, new List<SqlIndexDefinition>(), new List<SqlForeignKeyDefinition>(), null);
        var row = new object?[] { 1, 4294967294L };

        var encoded = RowCodec.Encode(row, tableDef);
        var decoded = RowCodec.DecodeToArray(encoded, tableDef);

        Assert.Equal(4294967294L, decoded[1]);
    }

    [Fact]
    public void Engine_in_memory_int64_column_roundtrips_large_literal()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Seq BIGINT)");
        engine.Execute("INSERT INTO T (Id, Seq) VALUES (1, 4294967294)");

        var tableDef = engine.GetTableDefinition("T");
        Assert.NotNull(tableDef);
        var seqCol = tableDef.Columns.First(c => c.Name.Equals("Seq", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(SqlScalarType.Int64, seqCol.Type);

        var result = engine.Execute("SELECT Seq FROM T WHERE Id = 1");
        var value = result.Rows![0]["Seq"];
        Assert.Equal(4294967294L, Convert.ToInt64(value, CultureInfo.InvariantCulture));
    }
}
