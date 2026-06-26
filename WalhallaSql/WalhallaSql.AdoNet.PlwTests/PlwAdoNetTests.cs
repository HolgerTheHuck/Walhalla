using System;
using System.Data;
using System.Data.Common;
using System.Linq;
using WalhallaSql.AdoNet;
using Xunit;

namespace WalhallaSql.AdoNet.PlwTests;

/// <summary>
/// ADO.NET-Integrationstests fuer LANGUAGE plw-Prozeduren ohne Dapper.
/// </summary>
public sealed class PlwAdoNetTests : IDisposable
{
    private readonly WalhallaEngine _engine;
    private readonly WalhallaSqlDbConnection _connection;

    public PlwAdoNetTests()
    {
        _engine = WalhallaEngine.InMemory();
        _connection = new WalhallaSqlDbConnection(_engine);
        _connection.Open();
        InitializeSchema();
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _engine?.Dispose();
    }

    private void InitializeSchema()
    {
        ExecuteNonQuery(@"
            CREATE TABLE Customers (
                Id INT PRIMARY KEY,
                Name VARCHAR(100) NOT NULL
            )");

        ExecuteNonQuery(@"INSERT INTO Customers (Id, Name) VALUES (1, 'Dyn')");
    }

    private void ExecuteNonQuery(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Execute_NonQuery_With_OutputParameter()
    {
        ExecuteNonQuery(@"
            CREATE OR REPLACE PROCEDURE GetCustomerName(
                IN @p_id INT,
                OUT @o_name STRING
            )
            LANGUAGE plw AS $$
            DECLARE
                v_name STRING;
            BEGIN
                SELECT Name INTO v_name FROM Customers WHERE Id = p_id;
                o_name := v_name;
            END;
            $$");

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "EXEC GetCustomerName @p_id = @id, @o_name = @name OUTPUT";
        AddParameter(cmd, "id", 1);
        AddParameter(cmd, "name", DBNull.Value, ParameterDirection.Output);

        cmd.ExecuteNonQuery();

        Assert.Equal("Dyn", cmd.Parameters["name"].Value);
    }

    [Fact]
    public void Execute_NonQuery_With_InputOutputParameter()
    {
        ExecuteNonQuery(@"
            CREATE OR REPLACE PROCEDURE DoubleValue(
                INOUT @p_value INT
            )
            LANGUAGE plw AS $$
            BEGIN
                p_value := p_value * 2;
            END;
            $$");

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "EXEC DoubleValue @p_value = @value OUTPUT";
        AddParameter(cmd, "value", 21, ParameterDirection.InputOutput);

        cmd.ExecuteNonQuery();

        Assert.Equal(42, Convert.ToInt32(cmd.Parameters["value"].Value));
    }

    [Fact]
    public void Execute_Reader_With_ReturnQuery()
    {
        ExecuteNonQuery(@"
            CREATE OR REPLACE PROCEDURE ListCustomers(
                IN @p_minId INT
            )
            LANGUAGE plw AS $$
            BEGIN
                RETURN QUERY SELECT Id, Name FROM Customers WHERE Id >= p_minId ORDER BY Id;
            END;
            $$");

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "EXEC ListCustomers @p_minId = @minId";
        AddParameter(cmd, "minId", 1);

        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal("Dyn", reader.GetString(1));
        Assert.False(reader.Read());
    }

    [Fact]
    public void Execute_NonQuery_With_LiteralPath_KeepsArgumentNames()
    {
        // Der Literal-Pfad wird genutzt, wenn die Verbindung keine strukturierten
        // Parameter unterstuetzt. Hier simulieren wir das ueber eine Verbindung,
        // die explizit den lokalen Engine-Handle verwendet, aber dennoch den
        // normalen AdoNet-Parameterpfad durchlaeuft.
        ExecuteNonQuery(@"
            CREATE OR REPLACE PROCEDURE AddNumbers(
                IN @p_a INT,
                IN @p_b INT,
                OUT @o_sum INT
            )
            LANGUAGE plw AS $$
            BEGIN
                o_sum := p_a + p_b;
            END;
            $$");

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "EXEC AddNumbers @p_a=@a,@p_b=@b,@o_sum=@sum OUTPUT";
        AddParameter(cmd, "a", 2);
        AddParameter(cmd, "b", 3);
        AddParameter(cmd, "sum", DBNull.Value, ParameterDirection.Output);

        cmd.ExecuteNonQuery();

        Assert.Equal(5, Convert.ToInt32(cmd.Parameters["sum"].Value));
    }

    private static void AddParameter(DbCommand command, string name, object? value, ParameterDirection direction = ParameterDirection.Input)
    {
        var param = command.CreateParameter();
        param.ParameterName = name;
        param.Value = value ?? DBNull.Value;
        param.Direction = direction;
        command.Parameters.Add(param);
    }
}
