using System.IO;
using Xunit;

namespace WalhallaSql.Tests;

public class SecurityTests
{
    [Fact]
    public void CreateRole_LoginAndSuperuser()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE ROLE app_user LOGIN PASSWORD 'secret'");
        engine.Execute("CREATE ROLE admin LOGIN SUPERUSER PASSWORD 'secret'");

        Assert.True(engine.AuthIdCatalog.TryGetRole("app_user", out var app));
        Assert.True(app.CanLogin);
        Assert.False(app.IsSuperuser);

        Assert.True(engine.AuthIdCatalog.TryGetRole("admin", out var adm));
        Assert.True(adm.CanLogin);
        Assert.True(adm.IsSuperuser);
    }

    [Fact]
    public void AlterRole_PasswordAndSuperuser()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE ROLE u LOGIN PASSWORD 'old'");

        engine.Execute("ALTER ROLE u PASSWORD 'new'");
        engine.Execute("ALTER ROLE u SUPERUSER");

        Assert.True(engine.AuthIdCatalog.TryGetRole("u", out var role));
        Assert.True(role.IsSuperuser);
    }

    [Fact]
    public void DropRole_IfExists()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE ROLE u LOGIN PASSWORD 'pw'");
        engine.Execute("DROP ROLE u");
        Assert.False(engine.AuthIdCatalog.TryGetRole("u", out _));

        engine.Execute("DROP ROLE IF EXISTS u"); // sollte nicht werfen
    }

    [Fact]
    public void GrantAndRevoke_SelectDeniedThenAllowed()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");
        engine.Execute("CREATE ROLE reader LOGIN PASSWORD 'pw'");

        // Ohne Recht darf reader nicht lesen
        engine.CurrentRole = "reader";
        Assert.Throws<WalhallaException>(() => engine.Execute("SELECT * FROM T"));

        // Mit Recht geht es
        engine.CurrentRole = "postgres";
        engine.Execute("GRANT SELECT ON TABLE T TO reader");

        engine.CurrentRole = "reader";
        var result = engine.Execute("SELECT * FROM T");
        Assert.Single(result.Rows);

        // Recht entziehen
        engine.CurrentRole = "postgres";
        engine.Execute("REVOKE SELECT ON TABLE T FROM reader");

        engine.CurrentRole = "reader";
        Assert.Throws<WalhallaException>(() => engine.Execute("SELECT * FROM T"));
    }

    [Fact]
    public void AllPrivileges_OnTable()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("CREATE ROLE writer LOGIN PASSWORD 'pw'");
        engine.Execute("GRANT ALL PRIVILEGES ON TABLE T TO writer");

        engine.CurrentRole = "writer";
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");
        engine.Execute("UPDATE T SET Name = 'Bob' WHERE Id = 1");
        var result = engine.Execute("SELECT * FROM T");
        Assert.Single(result.Rows);
        Assert.Equal("Bob", result.Rows[0]["Name"]);
        engine.Execute("DELETE FROM T WHERE Id = 1");
    }

    [Fact]
    public void InsertUpdateDelete_RequireRespectivePrivilege()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY, Name STRING)");
        engine.Execute("CREATE ROLE u LOGIN PASSWORD 'pw'");
        engine.Execute("GRANT INSERT ON TABLE T TO u");

        engine.CurrentRole = "u";
        engine.Execute("INSERT INTO T (Id, Name) VALUES (1, 'Alice')");
        Assert.Throws<WalhallaException>(() => engine.Execute("UPDATE T SET Name = 'Bob' WHERE Id = 1"));
        Assert.Throws<WalhallaException>(() => engine.Execute("SELECT * FROM T"));
    }

    [Fact]
    public void Ddl_RequiresSuperuser()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY)");
        engine.Execute("CREATE ROLE u LOGIN PASSWORD 'pw'");
        engine.Execute("GRANT ALL PRIVILEGES ON TABLE T TO u");

        engine.CurrentRole = "u";
        Assert.Throws<WalhallaException>(() => engine.Execute("CREATE TABLE X (Id INT PRIMARY KEY)"));
        Assert.Throws<WalhallaException>(() => engine.Execute("DROP TABLE T"));
        Assert.Throws<WalhallaException>(() => engine.Execute("CREATE INDEX IX ON T (Id)"));
    }

    [Fact]
    public void Procedure_ExecuteRequiresGrant()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE TABLE T (Id INT PRIMARY KEY)");
        engine.Execute("CREATE PROCEDURE AddRow(@id INT) AS INSERT INTO T (Id) VALUES (@id)");
        engine.Execute("CREATE ROLE u LOGIN PASSWORD 'pw'");
        engine.Execute("GRANT INSERT, SELECT ON TABLE T TO u");

        engine.CurrentRole = "u";
        Assert.Throws<WalhallaException>(() => engine.Execute("EXEC AddRow 1"));

        engine.CurrentRole = "postgres";
        engine.Execute("GRANT EXECUTE ON PROCEDURE AddRow TO u");

        engine.CurrentRole = "u";
        engine.Execute("EXEC AddRow 1");
        var result = engine.Execute("SELECT * FROM T");
        Assert.Single(result.Rows);
    }

    [Fact]
    public void DefaultAdmin_Postgres_IsCreatedOnDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"walhalla_security_test_{Path.GetRandomFileName()}");
        try
        {
            using (var engine = WalhallaEngine.Open(path))
            {
                Assert.True(engine.AuthIdCatalog.TryGetRole("postgres", out var postgres));
                Assert.True(postgres.IsSuperuser);
                Assert.True(postgres.CanLogin);
            }

            // Wieder oeffnen: Admin existiert noch
            using (var engine = WalhallaEngine.Open(path))
            {
                Assert.True(engine.AuthIdCatalog.TryGetRole("postgres", out _));
            }
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public void NonLoginRole_CannotAuthenticate()
    {
        using var engine = WalhallaEngine.InMemory();
        engine.Execute("CREATE ROLE service NOSUPERUSER PASSWORD 'pw'");
        Assert.False(engine.AuthIdCatalog.TryGetRole("service", out var role) && role.CanLogin);
    }
}
