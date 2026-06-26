using System;
using System.Linq;
using WalhallaSql.Core;
using Xunit;

namespace WalhallaSql.Tests;

/// <summary>
/// End-to-End-Tests fuer PLW-Prozeduren (LANGUAGE plw) gegen WalhallaSql.
/// </summary>
public sealed class PlwExecutionTests
{
    [Fact]
    public void Plw_Procedure_OutputParameter_ReturnsSum()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE AddNumbers(IN @a INT, IN @b INT, OUT @sum INT)
            LANGUAGE plw AS $$
            BEGIN
                sum := a + b;
            END;
            $$;
            """);

        var result = engine.Execute("EXEC AddNumbers @a = 2, @b = 3, @sum = NULL OUTPUT");

        Assert.Single(result.OutputParameters);
        Assert.Equal(5, Convert.ToInt32(result.OutputParameters["sum"]));
    }

    [Fact]
    public void Plw_Procedure_InsertsRows()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Log (Id INT PRIMARY KEY, Message STRING)");

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE WriteLog(IN @p_id INT, IN @p_msg STRING)
            LANGUAGE plw AS $$
            BEGIN
                INSERT INTO Log (Id, Message) VALUES (p_id, p_msg);
            END;
            $$;
            """);

        engine.Execute("EXEC WriteLog @p_id = 99, @p_msg = 'hello from plw'");

        var rows = engine.Execute("SELECT Message FROM Log WHERE Id = 99").Rows;
        Assert.Single(rows);
        Assert.Equal("hello from plw", rows[0]["Message"]);
    }

    [Fact]
    public void Plw_Procedure_If_Else_SetsOutput()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE Sign(IN @x INT, OUT @sign STRING)
            LANGUAGE plw AS $$
            BEGIN
                IF x > 0 THEN
                    sign := 'positive';
                ELSIF x = 0 THEN
                    sign := 'zero';
                ELSE
                    sign := 'negative';
                END IF;
            END;
            $$;
            """);

        var result = engine.Execute("EXEC Sign @x = -5, @sign = NULL OUTPUT");
        Assert.Equal("negative", result.OutputParameters["sign"]);

        result = engine.Execute("EXEC Sign @x = 0, @sign = NULL OUTPUT");
        Assert.Equal("zero", result.OutputParameters["sign"]);

        result = engine.Execute("EXEC Sign @x = 7, @sign = NULL OUTPUT");
        Assert.Equal("positive", result.OutputParameters["sign"]);
    }

    [Fact]
    public void Plw_Procedure_WhileLoop_ComputesFactorial()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE Factorial(IN @n INT, OUT @result INT)
            LANGUAGE plw AS $$
            DECLARE
                v_counter INT := 1;
            BEGIN
                result := 1;
                WHILE v_counter <= n LOOP
                    result := result * v_counter;
                    v_counter := v_counter + 1;
                END LOOP;
            END;
            $$;
            """);

        var result = engine.Execute("EXEC Factorial @n = 5, @result = 0 OUTPUT");
        Assert.Equal(120, Convert.ToInt32(result.OutputParameters["result"]));
    }

    [Fact]
    public void Plw_Procedure_SelectInto_AssignsValue()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Customers (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO Customers (Id, Name) VALUES (1, 'Dyn')");

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE GetCustomerName(IN @p_id INT, OUT @o_name STRING)
            LANGUAGE plw AS $$
            DECLARE
                v_name STRING;
            BEGIN
                SELECT Name INTO v_name FROM Customers WHERE Id = p_id;
                o_name := v_name;
            END;
            $$;
            """);

        var result = engine.Execute("EXEC GetCustomerName @p_id = 1, @o_name = NULL OUTPUT");
        Assert.Equal("Dyn", result.OutputParameters["o_name"]);
    }

    [Fact]
    public void Plw_Procedure_ForQueryLoop_CountsRows()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Items (Id INT PRIMARY KEY)");
        engine.Execute("INSERT INTO Items (Id) VALUES (1)");
        engine.Execute("INSERT INTO Items (Id) VALUES (2)");
        engine.Execute("INSERT INTO Items (Id) VALUES (3)");

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE CountItems(OUT @count INT)
            LANGUAGE plw AS $$
            DECLARE
                v_count INT := 0;
            BEGIN
                FOR rec IN SELECT Id FROM Items LOOP
                    v_count := v_count + 1;
                END LOOP;
                count := v_count;
            END;
            $$;
            """);

        var result = engine.Execute("EXEC CountItems @count = 0 OUTPUT");
        Assert.Equal(3, Convert.ToInt32(result.OutputParameters["count"]));
    }

    [Fact]
    public void Plw_Procedure_ReturnQuery_ReturnsResultSet()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Items (Id INT PRIMARY KEY, Name STRING, Value INT)");
        engine.Execute("INSERT INTO Items (Id, Name, Value) VALUES (1, 'Alpha', 10)");
        engine.Execute("INSERT INTO Items (Id, Name, Value) VALUES (2, 'Beta', 20)");
        engine.Execute("INSERT INTO Items (Id, Name, Value) VALUES (3, 'Gamma', 5)");

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE GetItemsAbove(IN @minValue INT)
            LANGUAGE plw AS $$
            BEGIN
                RETURN QUERY SELECT Id, Name FROM Items WHERE Value >= minValue ORDER BY Id;
            END;
            $$;
            """);

        var result = engine.Execute("EXEC GetItemsAbove @minValue = 10");

        Assert.Equal(2, result.Rows.Count);
        Assert.Equal("Alpha", result.Rows[0]["Name"]);
        Assert.Equal("Beta", result.Rows[1]["Name"]);
    }

    [Fact]
    public void Plw_Procedure_DynamicSql_ExecuteUsing()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Numbers (Id INT PRIMARY KEY)");

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE InsertNumber(IN @p_id INT)
            LANGUAGE plw AS $$
            BEGIN
                EXECUTE 'INSERT INTO Numbers (Id) VALUES ($1)' USING p_id;
            END;
            $$;
            """);

        engine.Execute("EXEC InsertNumber @p_id = 42");

        var rows = engine.Execute("SELECT Id FROM Numbers WHERE Id = 42").Rows;
        Assert.Single(rows);
        Assert.Equal(42, Convert.ToInt32(rows[0]["Id"]));
    }

    [Fact]
    public void Plw_Procedure_InstructionLimit_AbortsInfiniteLoop()
    {
        var options = new WalhallaOptions(":memory:")
        {
            StorageMode = StorageMode.InMemory,
            WalSyncMode = WalSyncMode.None,
            AutoCheckpointWalThresholdBytes = 0,
            PlwMaxInstructions = 500
        };

        using var engine = new WalhallaEngine(options);

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE InfiniteLoop()
            LANGUAGE plw AS $$
            DECLARE
                v_dummy INT := 0;
            BEGIN
                LOOP
                    v_dummy := v_dummy + 1;
                END LOOP;
            END;
            $$;
            """);

        var ex = Assert.Throws<WalhallaException>(() => engine.Execute("EXEC InfiniteLoop"));
        Assert.Contains("Instruktionslimit", ex.Message);
    }

    [Fact]
    public void Plw_Procedure_Timeout_AbortsLongRunningLoop()
    {
        var options = new WalhallaOptions(":memory:")
        {
            StorageMode = StorageMode.InMemory,
            WalSyncMode = WalSyncMode.None,
            AutoCheckpointWalThresholdBytes = 0,
            PlwTimeout = TimeSpan.FromMilliseconds(20),
            PlwMaxInstructions = 10_000_000
        };

        using var engine = new WalhallaEngine(options);

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE SlowLoop()
            LANGUAGE plw AS $$
            DECLARE
                v_dummy INT := 0;
            BEGIN
                LOOP
                    v_dummy := v_dummy + 1;
                END LOOP;
            END;
            $$;
            """);

        var ex = Assert.Throws<WalhallaException>(() => engine.Execute("EXEC SlowLoop"));
        Assert.Contains("Timeout", ex.Message);
    }

    [Fact]
    public void Plw_Procedure_MemoryLimit_AbortsStringGrowth()
    {
        var options = new WalhallaOptions(":memory:")
        {
            StorageMode = StorageMode.InMemory,
            WalSyncMode = WalSyncMode.None,
            AutoCheckpointWalThresholdBytes = 0,
            PlwMaxAllocatedBytesPerCall = 2L * 1024 * 1024,
            PlwMaxInstructions = 10_000_000
        };

        using var engine = new WalhallaEngine(options);

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE GrowString()
            LANGUAGE plw AS $$
            DECLARE
                v_big STRING := '';
            BEGIN
                FOR i IN 1 .. 2000 LOOP
                    v_big := v_big || '12345678901234567890123456789012345678901234567890';
                END LOOP;
            END;
            $$;
            """);

        var ex = Assert.Throws<WalhallaException>(() => engine.Execute("EXEC GrowString"));
        Assert.Contains("Speicherlimit", ex.Message);
    }

    [Fact]
    public void Plw_Procedure_Limits_AcceptableDefaults_RunNormally()
    {
        var options = new WalhallaOptions(":memory:")
        {
            StorageMode = StorageMode.InMemory,
            WalSyncMode = WalSyncMode.None,
            AutoCheckpointWalThresholdBytes = 0,
            PlwTimeout = TimeSpan.FromSeconds(5)
        };

        using var engine = new WalhallaEngine(options);

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE AddNumbers(IN @a INT, IN @b INT, OUT @sum INT)
            LANGUAGE plw AS $$
            BEGIN
                sum := a + b;
            END;
            $$;
            """);

        var result = engine.Execute("EXEC AddNumbers @a = 2, @b = 3, @sum = NULL OUTPUT");
        Assert.Equal(5, Convert.ToInt32(result.OutputParameters["sum"]));
    }

    [Fact]
    public void Plw_Procedure_DuplicateVariableDeclaration_Throws()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE DuplicateDecl()
            LANGUAGE plw AS $$
            DECLARE
                v_x INT := 1;
                v_x INT := 2;
            BEGIN
                v_x := v_x + 1;
            END;
            $$;
            """);

        var ex = Assert.Throws<WalhallaException>(() => engine.Execute("EXEC DuplicateDecl"));
        Assert.Contains("bereits deklariert", ex.Message);
    }

    [Fact]
    public void Plw_Procedure_SelectInto_MultipleRows_Throws()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Items (Id INT PRIMARY KEY)");
        engine.Execute("INSERT INTO Items (Id) VALUES (1)");
        engine.Execute("INSERT INTO Items (Id) VALUES (2)");

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE SelectIntoMany(OUT @o_id INT)
            LANGUAGE plw AS $$
            DECLARE
                v_id INT;
            BEGIN
                SELECT Id INTO v_id FROM Items;
                o_id := v_id;
            END;
            $$;
            """);

        var ex = Assert.Throws<WalhallaException>(() => engine.Execute("EXEC SelectIntoMany @o_id = 0 OUTPUT"));
        Assert.Contains("SELECT INTO", ex.Message);
        Assert.Equal("P0003", ex.SqlState);
    }

    [Fact]
    public void Plw_Procedure_ExecuteInto_SingleRow_Works()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Items (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO Items (Id, Name) VALUES (1, 'Alpha')");

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE ExecuteInto(OUT @o_name STRING)
            LANGUAGE plw AS $$
            DECLARE
                v_name STRING;
            BEGIN
                EXECUTE 'SELECT Name FROM Items WHERE Id = $1' INTO v_name USING 1;
                o_name := v_name;
            END;
            $$;
            """);

        var result = engine.Execute("EXEC ExecuteInto @o_name = NULL OUTPUT");
        Assert.Equal("Alpha", result.OutputParameters["o_name"]);
    }

    [Fact]
    public void Plw_Procedure_ExecuteInto_MultipleRows_Throws()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Items (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO Items (Id, Name) VALUES (1, 'Alpha')");
        engine.Execute("INSERT INTO Items (Id, Name) VALUES (2, 'Beta')");

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE ExecuteIntoMany(OUT @o_name STRING)
            LANGUAGE plw AS $$
            DECLARE
                v_name STRING;
            BEGIN
                EXECUTE 'SELECT Name FROM Items' INTO v_name;
                o_name := v_name;
            END;
            $$;
            """);

        var ex = Assert.Throws<WalhallaException>(() => engine.Execute("EXEC ExecuteIntoMany @o_name = NULL OUTPUT"));
        Assert.Contains("EXECUTE INTO", ex.Message);
        Assert.Equal("P0003", ex.SqlState);
    }

    [Fact]
    public void Plw_Procedure_ReturnWithValue_Throws()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE ReturnValue()
            LANGUAGE plw AS $$
            BEGIN
                RETURN 42;
            END;
            $$;
            """);

        var ex = Assert.Throws<WalhallaException>(() => engine.Execute("EXEC ReturnValue"));
        Assert.Contains("RETURN mit Ausdruck", ex.Message);
    }

    [Fact]
    public void Plw_Procedure_IntegerOverflow_PromotesToLong()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE BigMultiply(IN @a INT, IN @b INT, OUT @o_result STRING)
            LANGUAGE plw AS $$
            BEGIN
                o_result := a * b;
            END;
            $$;
            """);

        var result = engine.Execute(
            "EXEC BigMultiply @a = 2000000, @b = 2000000, @o_result = NULL OUTPUT");
        // 2_000_000 * 2_000_000 = 4_000_000_000_000 (passt nicht in INT, sollte als long erhalten bleiben)
        Assert.Equal("4000000000000", Convert.ToString(result.OutputParameters["o_result"]));
    }

    [Fact]
    public void Plw_Procedure_UnclosedDollarQuote_ThrowsClearMessage()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE BadQuote()
            LANGUAGE plw AS $tag$
            BEGIN
                v_x := 1;
            END;
            $$;
            """);

        var ex = Assert.Throws<WalhallaException>(() => engine.Execute("EXEC BadQuote"));
        Assert.Contains("Schliessendes Dollar-Quote", ex.Message);
    }

    [Fact]
    public void Plw_Procedure_ForLoopVariable_OverwritesBlockVariable()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE LoopShadow(OUT @o_result INT)
            LANGUAGE plw AS $$
            DECLARE
                v_i INT := 99;
            BEGIN
                FOR i IN 1 .. 3 LOOP
                    v_i := i;
                END LOOP;
                o_result := v_i;
            END;
            $$;
            """);

        var result = engine.Execute("EXEC LoopShadow @o_result = 0 OUTPUT");
        Assert.Equal(3, Convert.ToInt32(result.OutputParameters["o_result"]));
    }

    [Fact]
    public void Plw_Procedure_Found_Insert_And_SelectInto_True()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Customers (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO Customers (Id, Name) VALUES (1, 'Dyn')");

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE FoundDemo(OUT @o_found BOOLEAN)
            LANGUAGE plw AS $$
            DECLARE
                v_name STRING;
            BEGIN
                SELECT Name INTO v_name FROM Customers WHERE Id = 1;
                o_found := FOUND;
            END;
            $$;
            """);

        var result = engine.Execute("EXEC FoundDemo @o_found = false OUTPUT");
        Assert.Equal(true, Convert.ToBoolean(result.OutputParameters["o_found"]));
    }

    [Fact]
    public void Plw_Procedure_Found_SelectInto_NoRow_False()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Customers (Id INT PRIMARY KEY, Name STRING)");

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE FoundDemo(OUT @o_found BOOLEAN)
            LANGUAGE plw AS $$
            DECLARE
                v_name STRING;
            BEGIN
                SELECT Name INTO v_name FROM Customers WHERE Id = 1;
                o_found := FOUND;
            END;
            $$;
            """);

        var result = engine.Execute("EXEC FoundDemo @o_found = true OUTPUT");
        Assert.Equal(false, Convert.ToBoolean(result.OutputParameters["o_found"]));
    }

    [Fact]
    public void Plw_Procedure_Found_Update_AffectedRows()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Items (Id INT PRIMARY KEY, Amount INT)");
        engine.Execute("INSERT INTO Items (Id, Amount) VALUES (1, 10)");
        engine.Execute("INSERT INTO Items (Id, Amount) VALUES (2, 20)");

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE FoundUpdate(IN @p_threshold INT, OUT @o_found BOOLEAN)
            LANGUAGE plw AS $$
            BEGIN
                UPDATE Items SET Amount = 99 WHERE Amount >= p_threshold;
                o_found := FOUND;
            END;
            $$;
            """);

        var result = engine.Execute("EXEC FoundUpdate @p_threshold = 15, @o_found = false OUTPUT");
        Assert.Equal(true, Convert.ToBoolean(result.OutputParameters["o_found"]));
    }

    [Fact]
    public void Plw_Procedure_Found_Delete_NoMatch_False()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Items (Id INT PRIMARY KEY)");
        engine.Execute("INSERT INTO Items (Id) VALUES (1)");

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE FoundDelete(OUT @o_found BOOLEAN)
            LANGUAGE plw AS $$
            BEGIN
                DELETE FROM Items WHERE Id = 99;
                o_found := FOUND;
            END;
            $$;
            """);

        var result = engine.Execute("EXEC FoundDelete @o_found = true OUTPUT");
        Assert.Equal(false, Convert.ToBoolean(result.OutputParameters["o_found"]));
    }

    [Fact]
    public void Plw_Procedure_Found_ExecuteInto_True()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Items (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO Items (Id, Name) VALUES (1, 'Alpha')");

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE FoundExecuteInto(OUT @o_found BOOLEAN)
            LANGUAGE plw AS $$
            DECLARE
                v_name STRING;
            BEGIN
                EXECUTE 'SELECT Name FROM Items WHERE Id = $1' INTO v_name USING 1;
                o_found := FOUND;
            END;
            $$;
            """);

        var result = engine.Execute("EXEC FoundExecuteInto @o_found = false OUTPUT");
        Assert.Equal(true, Convert.ToBoolean(result.OutputParameters["o_found"]));
    }

    [Fact]
    public void Plw_Procedure_Cursor_OpenFetchClose_Record()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Customers (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO Customers (Id, Name) VALUES (1, 'Alice')");
        engine.Execute("INSERT INTO Customers (Id, Name) VALUES (2, 'Bob')");
        engine.Execute("INSERT INTO Customers (Id, Name) VALUES (3, 'Carol')");

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE CursorSumNames(OUT @o_count INT)
            LANGUAGE plw AS $$
            DECLARE
                cur CURSOR FOR SELECT Id, Name FROM Customers ORDER BY Id;
                rec RECORD;
            BEGIN
                o_count := 0;
                OPEN cur;
                LOOP
                    FETCH cur INTO rec;
                    EXIT WHEN NOT FOUND;
                    o_count := o_count + 1;
                END LOOP;
                CLOSE cur;
            END;
            $$;
            """);

        var result = engine.Execute("EXEC CursorSumNames @o_count = 0 OUTPUT");
        Assert.Equal(3, Convert.ToInt32(result.OutputParameters["o_count"]));
    }

    [Fact]
    public void Plw_Procedure_Cursor_FetchScalarVariables()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Customers (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO Customers (Id, Name) VALUES (7, 'Dyn')");

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE CursorFetchScalars(OUT @o_id INT, OUT @o_name STRING)
            LANGUAGE plw AS $$
            DECLARE
                cur CURSOR FOR SELECT Id, Name FROM Customers WHERE Id = 7;
            BEGIN
                OPEN cur;
                FETCH cur INTO o_id, o_name;
                CLOSE cur;
            END;
            $$;
            """);

        var result = engine.Execute("EXEC CursorFetchScalars @o_id = 0 OUTPUT, @o_name = '' OUTPUT");
        Assert.Equal(7, Convert.ToInt32(result.OutputParameters["o_id"]));
        Assert.Equal("Dyn", result.OutputParameters["o_name"]);
    }

    [Fact]
    public void Plw_Procedure_Cursor_Empty_FoundFalse()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Customers (Id INT PRIMARY KEY, Name STRING)");

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE CursorEmpty(OUT @o_found BOOLEAN)
            LANGUAGE plw AS $$
            DECLARE
                cur CURSOR FOR SELECT Id FROM Customers;
                v_id INT;
            BEGIN
                OPEN cur;
                FETCH cur INTO v_id;
                o_found := FOUND;
                CLOSE cur;
            END;
            $$;
            """);

        var result = engine.Execute("EXEC CursorEmpty @o_found = true OUTPUT");
        Assert.Equal(false, Convert.ToBoolean(result.OutputParameters["o_found"]));
    }

    [Fact]
    public void Plw_Procedure_Cursor_DoubleOpen_Throws()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Customers (Id INT PRIMARY KEY)");
        engine.Execute("INSERT INTO Customers (Id) VALUES (1)");

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE CursorDoubleOpen()
            LANGUAGE plw AS $$
            DECLARE
                cur CURSOR FOR SELECT Id FROM Customers;
            BEGIN
                OPEN cur;
                OPEN cur;
            END;
            $$;
            """);

        var ex = Assert.Throws<WalhallaException>(() => engine.Execute("EXEC CursorDoubleOpen"));
        Assert.Contains("bereits geoeffnet", ex.Message);
    }

    [Fact]
    public void Plw_Procedure_ExceptionHandler_Others_Catches_RaiseException()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE CatchAll(OUT @o_handled BOOLEAN)
            LANGUAGE plw AS $$
            BEGIN
                BEGIN
                    RAISE EXCEPTION 'something went wrong';
                    o_handled := false;
                EXCEPTION
                    WHEN OTHERS THEN
                        o_handled := true;
                END;
            END;
            $$;
            """);

        var result = engine.Execute("EXEC CatchAll @o_handled = false OUTPUT");
        Assert.Equal(true, Convert.ToBoolean(result.OutputParameters["o_handled"]));
    }

    [Fact]
    public void Plw_Procedure_ExceptionHandler_Exposes_SqlState_And_SqlErrm()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE ExposeError(OUT @o_state STRING, OUT @o_message STRING)
            LANGUAGE plw AS $$
            BEGIN
                BEGIN
                    RAISE EXCEPTION 'boom' USING SQLSTATE = 'P9999';
                EXCEPTION
                    WHEN OTHERS THEN
                        o_state := SQLSTATE;
                        o_message := SQLERRM;
                END;
            END;
            $$;
            """);

        var result = engine.Execute("EXEC ExposeError @o_state = '' OUTPUT, @o_message = '' OUTPUT");
        Assert.Equal("P9999", result.OutputParameters["o_state"]);
        Assert.Equal("boom", result.OutputParameters["o_message"]);
    }

    [Fact]
    public void Plw_Procedure_ExceptionHandler_Matches_Named_Exception()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE MatchNamed(OUT @o_handled BOOLEAN)
            LANGUAGE plw AS $$
            BEGIN
                BEGIN
                    RAISE EXCEPTION 'division' USING SQLSTATE = '22012';
                EXCEPTION
                    WHEN division_by_zero THEN
                        o_handled := true;
                END;
            END;
            $$;
            """);

        var result = engine.Execute("EXEC MatchNamed @o_handled = false OUTPUT");
        Assert.Equal(true, Convert.ToBoolean(result.OutputParameters["o_handled"]));
    }

    [Fact]
    public void Plw_Procedure_ExceptionHandler_Matches_SqlState_String()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE MatchSqlState(OUT @o_handled BOOLEAN)
            LANGUAGE plw AS $$
            BEGIN
                BEGIN
                    RAISE EXCEPTION 'custom' USING SQLSTATE = '12345';
                EXCEPTION
                    WHEN '12345' THEN
                        o_handled := true;
                END;
            END;
            $$;
            """);

        var result = engine.Execute("EXEC MatchSqlState @o_handled = false OUTPUT");
        Assert.Equal(true, Convert.ToBoolean(result.OutputParameters["o_handled"]));
    }

    [Fact]
    public void Plw_Procedure_ExceptionHandler_Unhandled_Rethrows()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE Unhandled()
            LANGUAGE plw AS $$
            DECLARE
                v_state STRING;
            BEGIN
                BEGIN
                    RAISE EXCEPTION 'no handler' USING SQLSTATE = '99999';
                EXCEPTION
                    WHEN '11111' THEN
                        v_state := SQLSTATE;
                END;
            END;
            $$;
            """);

        var ex = Assert.Throws<WalhallaException>(() => engine.Execute("EXEC Unhandled"));
        Assert.Equal("99999", ex.SqlState);
    }

    [Fact]
    public void Plw_Trigger_Insert_Uses_New_And_TgVariables()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Accounts (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("CREATE TABLE Audit (Id INT PRIMARY KEY, Op STRING, TableName STRING)");

        engine.Execute("""
            CREATE OR REPLACE TRIGGER trg_Accounts_Insert
            ON Accounts AFTER INSERT LANGUAGE plw
            AS
            BEGIN
                INSERT INTO Audit (Id, Op, TableName)
                VALUES (NEW.Id, TG_OP, TG_TABLE_NAME);
            END;
            """);

        engine.Execute("INSERT INTO Accounts (Id, Name) VALUES (1, 'Alice')");

        var rows = engine.Execute("SELECT Op, TableName FROM Audit").Rows;
        Assert.Single(rows);
        Assert.Equal("INSERT", rows[0]["Op"]);
        Assert.Equal("Accounts", rows[0]["TableName"]);
    }

    [Fact]
    public void Plw_Trigger_Update_Uses_New_And_Old()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Accounts (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("CREATE TABLE Audit (Id INT PRIMARY KEY, OldName STRING, NewName STRING)");
        engine.Execute("INSERT INTO Accounts (Id, Name) VALUES (1, 'Alice')");

        engine.Execute("""
            CREATE OR REPLACE TRIGGER trg_Accounts_Update
            ON Accounts AFTER UPDATE LANGUAGE plw
            AS
            BEGIN
                INSERT INTO Audit (Id, OldName, NewName)
                VALUES (NEW.Id, OLD.Name, NEW.Name);
            END;
            """);

        engine.Execute("UPDATE Accounts SET Name = 'Bob' WHERE Id = 1");

        var rows = engine.Execute("SELECT OldName, NewName FROM Audit").Rows;
        Assert.Single(rows);
        Assert.Equal("Alice", rows[0]["OldName"]);
        Assert.Equal("Bob", rows[0]["NewName"]);
    }

    [Fact]
    public void Plw_Trigger_Delete_Uses_Old()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE Accounts (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("CREATE TABLE Audit (Id INT PRIMARY KEY, Message STRING)");
        engine.Execute("INSERT INTO Accounts (Id, Name) VALUES (1, 'Alice')");

        engine.Execute("""
            CREATE OR REPLACE TRIGGER trg_Accounts_Delete
            ON Accounts AFTER DELETE LANGUAGE plw
            AS
            BEGIN
                INSERT INTO Audit (Id, Message)
                VALUES (OLD.Id, OLD.Name);
            END;
            """);

        engine.Execute("DELETE FROM Accounts WHERE Id = 1");

        var rows = engine.Execute("SELECT Message FROM Audit").Rows;
        Assert.Single(rows);
        Assert.Equal("Alice", rows[0]["Message"]);
    }

    [Fact]
    public void Plw_Raise_Formats_Percent_Placeholders()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE FormatRaise(OUT @o_msg STRING)
            LANGUAGE plw AS $$
            BEGIN
                o_msg := 'placeholder';
                RAISE NOTICE 'Count: %, Name: %', 42, 'Alice';
            END;
            $$;
            """);

        var result = engine.Execute("EXEC FormatRaise @o_msg = NULL OUTPUT");
        Assert.Equal("placeholder", result.OutputParameters["o_msg"]);
    }

    [Fact]
    public void Plw_Raise_Escapes_Double_Percent()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE RaiseEscape()
            LANGUAGE plw AS $$
            BEGIN
                RAISE EXCEPTION 'Value is %% %', 7 USING SQLSTATE = '77777';
            END;
            $$;
            """);

        var ex = Assert.Throws<WalhallaException>(() => engine.Execute("EXEC RaiseEscape"));
        Assert.Equal("Value is % 7", ex.Message);
        Assert.Equal("77777", ex.SqlState);
    }

    [Fact]
    public void Plw_Raise_Too_Few_Arguments_Throws()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE RaiseTooFew()
            LANGUAGE plw AS $$
            BEGIN
                RAISE EXCEPTION 'A % B % C %', 1, 2;
            END;
            $$;
            """);

        var ex = Assert.Throws<WalhallaException>(() => engine.Execute("EXEC RaiseTooFew"));
        Assert.Contains("Zu wenige Argumente", ex.Message);
    }

    [Fact]
    public void Plw_Raise_Exception_With_Hint_And_Detail()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE RaiseHintDetail()
            LANGUAGE plw AS $$
            BEGIN
                RAISE EXCEPTION 'validation failed'
                    USING SQLSTATE = '12345', HINT = 'check input', DETAIL = 'id must be positive';
            END;
            $$;
            """);

        var ex = Assert.Throws<WalhallaException>(() => engine.Execute("EXEC RaiseHintDetail"));
        Assert.Equal("validation failed", ex.Message);
        Assert.Equal("12345", ex.SqlState);
        Assert.Equal("check input", ex.Hint);
        Assert.Equal("id must be positive", ex.Detail);
    }

    [Fact]
    public void Plw_Functions_Substring_And_Current_Timestamp()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE FuncDemo(OUT @o_sub STRING, OUT @o_len INT, OUT @o_coalesce STRING)
            LANGUAGE plw AS $$
            BEGIN
                o_sub := SUBSTRING('Hello World', 7, 5);
                o_len := LENGTH('Hello World');
                o_coalesce := COALESCE(NULL, 'fallback', 'ignored');
            END;
            $$;
            """);

        var result = engine.Execute("EXEC FuncDemo @o_sub = NULL OUTPUT, @o_len = 0 OUTPUT, @o_coalesce = NULL OUTPUT");
        Assert.Equal("World", result.OutputParameters["o_sub"]);
        Assert.Equal(11, Convert.ToInt32(result.OutputParameters["o_len"]));
        Assert.Equal("fallback", result.OutputParameters["o_coalesce"]);
    }

    [Fact]
    public void Plw_Functions_Trim_And_Abs()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE FuncDemo2(OUT @o_trim STRING, OUT @o_abs DOUBLE)
            LANGUAGE plw AS $$
            BEGIN
                o_trim := TRIM('  x  ');
                o_abs := ABS(-3.14);
            END;
            $$;
            """);

        var result = engine.Execute("EXEC FuncDemo2 @o_trim = NULL OUTPUT, @o_abs = 0 OUTPUT");
        Assert.Equal("x", result.OutputParameters["o_trim"]);
        Assert.Equal(3.14, Convert.ToDouble(result.OutputParameters["o_abs"]), 2);
    }

    [Fact]
    public void Plw_Labeled_Exit_From_Nested_Loop()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE LabelDemo(OUT @o_result INT)
            LANGUAGE plw AS $$
            BEGIN
                o_result := 0;
                <<outer>>
                FOR i IN 1..3 LOOP
                    <<inner>>
                    FOR j IN 1..3 LOOP
                        o_result := o_result + 1;
                        IF j = 2 THEN
                            EXIT outer;
                        END IF;
                    END LOOP inner;
                END LOOP outer;
            END;
            $$;
            """);

        var result = engine.Execute("EXEC LabelDemo @o_result = 0 OUTPUT");
        Assert.Equal(2, Convert.ToInt32(result.OutputParameters["o_result"]));
    }

    [Fact]
    public void Plw_Concat_Operator_In_Expression()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("""
            CREATE OR REPLACE PROCEDURE ConcatDemo(OUT @o_result STRING)
            LANGUAGE plw AS $$
            BEGIN
                o_result := 'Hello' || ' ' || 'World';
            END;
            $$;
            """);

        var result = engine.Execute("EXEC ConcatDemo @o_result = NULL OUTPUT");
        Assert.Equal("Hello World", result.OutputParameters["o_result"]);
    }

    [Fact]
    public void Plw_Before_Insert_Modifies_New()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE audit_items (id INT PRIMARY KEY, name STRING, created_at DATETIME)");
        engine.Execute("""
            CREATE OR REPLACE TRIGGER trg_set_created_at
            ON audit_items BEFORE INSERT LANGUAGE plw
            AS
            BEGIN
                NEW.created_at := NOW();
            END;
            """);

        engine.Execute("INSERT INTO audit_items (id, name) VALUES (1, 'test')");
        var result = engine.Execute("SELECT id, name, created_at FROM audit_items WHERE id = 1");
        Assert.Single(result.Rows);
        Assert.NotNull(result.Rows[0]["created_at"]);
    }

    [Fact]
    public void Plw_Truncate_Trigger_Fires()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE trunc_log (id INT PRIMARY KEY, msg STRING)");
        engine.Execute("CREATE TABLE trunc_test (id INT PRIMARY KEY, name STRING)");
        engine.Execute("""
            CREATE OR REPLACE TRIGGER trg_trunc_audit
            ON trunc_test BEFORE TRUNCATE LANGUAGE plw
            AS
            BEGIN
                INSERT INTO trunc_log (id, msg) VALUES (1, 'truncated');
            END;
            """);

        engine.Execute("TRUNCATE TABLE trunc_test");

        var result = engine.Execute("SELECT msg FROM trunc_log");
        Assert.Single(result.Rows);
        Assert.Equal("truncated", result.Rows[0]["msg"]);
    }

    [Fact]
    public void Sql_Concat_Operator_In_Select()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE Users (Id INT PRIMARY KEY, FirstName STRING, LastName STRING)");
        engine.Execute("INSERT INTO Users (Id, FirstName, LastName) VALUES (1, 'Ada', 'Lovelace')");

        var result = engine.Execute("SELECT FirstName || ' ' || LastName AS FullName FROM Users WHERE Id = 1");
        Assert.Single(result.Rows);
        Assert.Equal("Ada Lovelace", result.Rows[0]["FullName"]);
    }

    [Fact]
    public void Plw_Scalar_Function_In_Select()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE Dummies (Id INT PRIMARY KEY)");
        engine.Execute("INSERT INTO Dummies (Id) VALUES (1)");

        engine.Execute("""
            CREATE OR REPLACE FUNCTION double_it(IN @x INT)
            RETURNS INT
            LANGUAGE plw
            AS $$
            BEGIN
                RETURN x * 2;
            END;
            $$;
            """);

        var result = engine.Execute("SELECT double_it(5) AS doubled FROM Dummies");
        Assert.Single(result.Rows);
        Assert.Equal(10, Convert.ToInt32(result.Rows[0]["doubled"]));
    }

    [Fact]
    public void Plw_Scalar_Function_With_Column_Argument()
    {
        using var engine = WalhallaEngine.InMemory();

        engine.Execute("CREATE TABLE Items (Id INT PRIMARY KEY, Value INT)");
        engine.Execute("INSERT INTO Items (Id, Value) VALUES (1, 7)");

        engine.Execute("""
            CREATE OR REPLACE FUNCTION add_ten(IN @v INT)
            RETURNS INT
            LANGUAGE plw
            AS $$
            BEGIN
                RETURN v + 10;
            END;
            $$;
            """);

        var result = engine.Execute("SELECT add_ten(Value) AS result FROM Items WHERE Id = 1");
        Assert.Single(result.Rows);
        Assert.Equal(17, Convert.ToInt32(result.Rows[0]["result"]));
    }
}
