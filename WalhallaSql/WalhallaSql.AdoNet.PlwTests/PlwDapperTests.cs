using System;
using System.Data;
using System.Linq;
using Dapper;
using WalhallaSql.AdoNet;
using Xunit;

namespace WalhallaSql.AdoNet.PlwTests;

/// <summary>
/// Dapper-Integrationstests fuer LANGUAGE plw-Prozeduren.
/// </summary>
public sealed class PlwDapperTests : IDisposable
{
    private readonly WalhallaEngine _engine;
    private readonly WalhallaSqlDbConnection _connection;

    public PlwDapperTests()
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
        _connection.Execute(@"
            CREATE TABLE Customers (
                Id INT PRIMARY KEY,
                Name STRING NOT NULL,
                Region STRING NOT NULL
            )");

        _connection.Execute(@"INSERT INTO Customers (Id, Name, Region) VALUES (1, 'Dyn', 'EU')");
        _connection.Execute(@"INSERT INTO Customers (Id, Name, Region) VALUES (2, 'Alice', 'US')");
    }

    [Fact]
    public void DynamicParameters_OutputFromPlwProcedure()
    {
        _connection.Execute(@"
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

        var parameters = new DynamicParameters();
        parameters.Add("id", 1);
        parameters.Add("name", dbType: DbType.String, direction: ParameterDirection.Output, size: 100);

        _connection.Execute(
            "EXEC GetCustomerName @p_id = @id, @o_name = @name OUTPUT",
            parameters);

        Assert.Equal("Dyn", parameters.Get<string>("name"));
    }

    [Fact]
    public void DynamicParameters_InputOutputFromPlwProcedure()
    {
        _connection.Execute(@"
            CREATE OR REPLACE PROCEDURE DoubleValue(
                INOUT @p_value INT
            )
            LANGUAGE plw AS $$
            BEGIN
                p_value := p_value * 2;
            END;
            $$");

        var parameters = new DynamicParameters();
        parameters.Add("value", 21, DbType.Int32, direction: ParameterDirection.InputOutput);

        _connection.Execute(
            "EXEC DoubleValue @p_value = @value OUTPUT",
            parameters);

        Assert.Equal(42, parameters.Get<int>("value"));
    }

    [Fact]
    public void Query_ReturnQueryResultSet()
    {
        _connection.Execute(@"
            CREATE OR REPLACE PROCEDURE GetCustomersByRegion(
                IN @p_region STRING
            )
            LANGUAGE plw AS $$
            BEGIN
                RETURN QUERY SELECT Id, Name FROM Customers WHERE Region = p_region ORDER BY Id;
            END;
            $$");

        var customers = _connection.Query<Customer>(
            "EXEC GetCustomersByRegion @p_region = @region",
            new { region = "EU" }).ToList();

        Assert.Single(customers);
        Assert.Equal(1, customers[0].Id);
        Assert.Equal("Dyn", customers[0].Name);
    }

    [Fact]
    public void DynamicParameters_OutputParameter_ReturnsNull_WhenNoRowFound()
    {
        _connection.Execute(@"
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

        var parameters = new DynamicParameters();
        parameters.Add("id", 999);
        parameters.Add("name", dbType: DbType.String, direction: ParameterDirection.Output, size: 100);

        _connection.Execute(
            "EXEC GetCustomerName @p_id = @id, @o_name = @name OUTPUT",
            parameters);

        Assert.Null(parameters.Get<string>("name"));
    }

    [Fact]
    public void Execute_Procedure_WithoutParameters()
    {
        _connection.Execute(@"
            CREATE OR REPLACE PROCEDURE CountCustomers(
                OUT @o_count INT
            )
            LANGUAGE plw AS $$
            DECLARE
                v_count INT := 0;
            BEGIN
                FOR rec IN SELECT Id FROM Customers LOOP
                    v_count := v_count + 1;
                END LOOP;
                o_count := v_count;
            END;
            $$");

        var parameters = new DynamicParameters();
        parameters.Add("count", dbType: DbType.Int32, direction: ParameterDirection.Output);

        _connection.Execute("EXEC CountCustomers @o_count = @count OUTPUT", parameters);

        Assert.Equal(2, parameters.Get<int>("count"));
    }

    private sealed class Customer
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }
}
