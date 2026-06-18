using System;
using System.Data;
using System.IO;
using WalhallaSql;
using WalhallaSql.AdoNet;
using WalhallaSql.Core;
using Microsoft.EntityFrameworkCore;

namespace WalhallaSql.EfCore.Tests.MinimalSpecs;

/// <summary>
/// Minimalbeispiele für die ADO.NET-Oberfläche:
/// DbCommand.ExecuteScalar, DbDataReader.RecordsAffected, ParameterDirection.
/// </summary>
[Trait("Category", "MinimalEfSpec")]
public sealed class MinimalAdoNetSurfaceTests
{
    [Fact]
    public void ExecuteScalar_select_literal_returns_value()
    {
        using var conn = OpenConnection();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1";
        var result = cmd.ExecuteScalar();

        Assert.Equal(1L, Assert.IsType<long>(result));
    }

    [Fact]
    public void ExecuteScalar_select_string_literal_returns_string()
    {
        using var conn = OpenConnection();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 'Ada' AS Value";
        var result = cmd.ExecuteScalar();

        Assert.Equal("Ada", result);
    }

    [Fact]
    public void RecordsAffected_after_INSERT_returns_one()
    {
        using var conn = OpenConnection();
        Exec(conn, "CREATE TABLE T (Id INT)");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO T (Id) VALUES (99)";
        using var reader = cmd.ExecuteReader();

        Assert.Equal(1, reader.RecordsAffected);
    }

    [Fact]
    public void Input_parameter_direction_does_not_throw()
    {
        using var conn = OpenConnection();
        Exec(conn, "CREATE TABLE Users (Id INT PRIMARY KEY, Name VARCHAR(200) NOT NULL)");
        Exec(conn, "INSERT INTO Users (Id, Name) VALUES (1, 'Ada')");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Name FROM Users WHERE Id = @id";
        var p = cmd.CreateParameter();
        p.ParameterName = "id";
        p.Direction = ParameterDirection.Input;
        p.Value = 1;
        cmd.Parameters.Add(p);

        var result = cmd.ExecuteScalar();
        Assert.Equal("Ada", result);
    }

    [Fact]
    public void Output_parameter_direction_throws_NotSupportedException()
    {
        using var conn = OpenConnection();
        Exec(conn, "CREATE TABLE Users (Id INT PRIMARY KEY, Name VARCHAR(200) NOT NULL)");

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id FROM Users WHERE Id = 1";
        var p = cmd.CreateParameter();
        p.ParameterName = "result";
        p.Direction = ParameterDirection.Output;
        cmd.Parameters.Add(p);

        Assert.Throws<NotSupportedException>(() => cmd.ExecuteNonQuery());
    }

    private static WalhallaSqlDbConnection OpenConnection()
    {
        var conn = new WalhallaSqlDbConnection("Data Source=:memory:");
        conn.Open();
        return conn;
    }

    private static void Exec(WalhallaSqlDbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
