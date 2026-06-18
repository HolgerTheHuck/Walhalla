using WalhallaSql.AdoNet;
using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore;

var pgWireWebSocketEndpoint = ReadArg(args, "--pgwire-ws-endpoint")
    ?? ReadArg(args, "--pgwire-websocket-endpoint");
if (!string.IsNullOrWhiteSpace(pgWireWebSocketEndpoint))
{
    await RunRemoteWebSocketDemoAsync(
        pgWireWebSocketEndpoint!,
        databaseName: ReadArg(args, "--database") ?? "EmbeddedShopApp",
        username: ReadArg(args, "--username") ?? "test",
        password: ReadArg(args, "--password") ?? "test");
    return;
}

var dbPath = Path.Combine(Path.GetTempPath(), "WalhallaSql", "NuGetDemo", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dbPath);

var embeddedConnectionString = $"EmbeddedPath={dbPath};Database=EmbeddedShopApp";
var layeredOptions = new WalhallaSqlEfCoreOptions(embeddedConnectionString);
var options = new DbContextOptionsBuilder<EmbeddedShopContext>()
    .UseWalhallaSql(layeredOptions)
    .Options;

string[] appliedMigrations;
using (var context = new EmbeddedShopContext(options))
{
    context.Database.Migrate();
    SeedShopData(context);
    appliedMigrations = context.Database.GetAppliedMigrations().ToArray();
}

using var verificationConnection = new WalhallaSqlDbConnection(embeddedConnectionString);
verificationConnection.Open();
using var verificationCommand = verificationConnection.CreateCommand();
verificationCommand.CommandText = @"
SELECT u.DisplayName AS DisplayName,
       a.City AS City,
       o.OrderNumber AS OrderNumber,
       o.Status AS Status,
       o.TotalAmountCents AS TotalAmountCents
FROM Users u
LEFT JOIN Addresses a ON a.UserId = u.Id
LEFT JOIN Orders o ON o.UserId = u.Id
ORDER BY u.Id, a.Id, o.Id";

var rows = new List<Dictionary<string, object?>>();
using (var reader = verificationCommand.ExecuteReader())
{
    while (reader.Read())
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
            row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);

        rows.Add(row);
    }
}

Console.WriteLine($"Migrations applied: {string.Join(", ", appliedMigrations)}");
Console.WriteLine($"DatabasePath: {dbPath}");
Console.WriteLine($"Rows: {rows.Count}");
foreach (var row in rows)
    Console.WriteLine($"- {GetValue(row, "DisplayName")}: {GetValue(row, "City")} | {GetValue(row, "OrderNumber")} | {GetValue(row, "Status")} | cents={GetValue(row, "TotalAmountCents")}");

static async Task RunRemoteWebSocketDemoAsync(string endpoint, string databaseName, string username, string password)
{
    await using var tunnel = await WalhallaSqlPgWireWebSocketTunnel.StartAsync(
        endpoint,
        database: databaseName,
        username: username,
        password: password,
        extraConnectionStringSegments: "Pooling=false;Timeout=5;Command Timeout=10");

    var layeredOptions = new WalhallaSqlEfCoreOptions(tunnel.ConnectionString);
    var options = new DbContextOptionsBuilder<EmbeddedShopContext>()
        .UseWalhallaSql(layeredOptions)
        .Options;

    using var context = new EmbeddedShopContext(options);
    context.Database.Migrate();

    SeedShopData(context);

    using var verificationConnection = tunnel.CreateOpenConnection();
    var rows = ReadVerificationRows(verificationConnection);
    var appliedMigrations = context.Database.GetAppliedMigrations().ToArray();

    Console.WriteLine($"RemoteWebSocketEndpoint: {endpoint}");
    Console.WriteLine($"TunnelConnectionString: {tunnel.ConnectionString}");
    Console.WriteLine($"Migrations applied: {string.Join(", ", appliedMigrations)}");
    Console.WriteLine($"Rows: {rows.Count}");
    foreach (var row in rows)
        Console.WriteLine($"- {GetValue(row, "DisplayName")}: {GetValue(row, "City")} | {GetValue(row, "OrderNumber")} | {GetValue(row, "Status")} | cents={GetValue(row, "TotalAmountCents")}");
}

static void SeedShopData(EmbeddedShopContext context)
{
    context.Users.AddRange(
        new User { Id = 1, Email = "ada@example.net", DisplayName = "Ada Lovelace", IsActive = true },
        new User { Id = 2, Email = "alan@example.net", DisplayName = "Alan Turing", IsActive = true });

    context.SaveChanges();

    context.Addresses.AddRange(
        new Address { Id = 10, UserId = 1, Label = "Home", Street = "Analytical Engine 1", PostalCode = "10000", City = "London", Country = "UK" },
        new Address { Id = 11, UserId = 1, Label = "Office", Street = "Babbage Street 2", PostalCode = "10001", City = "London", Country = "UK" },
        new Address { Id = 12, UserId = 2, Label = "Home", Street = "Enigma Road 3", PostalCode = "10115", City = "Berlin", Country = "DE" });

    context.SaveChanges();

    context.Orders.AddRange(
        new Order { Id = 1000, UserId = 1, OrderNumber = "SO-1000", Status = "Open", TotalAmountCents = 129900, IsPaid = false },
        new Order { Id = 1001, UserId = 1, OrderNumber = "SO-1001", Status = "Paid", TotalAmountCents = 45900, IsPaid = true },
        new Order { Id = 1002, UserId = 2, OrderNumber = "SO-1002", Status = "Open", TotalAmountCents = 9900, IsPaid = false });

    context.SaveChanges();
}

static List<Dictionary<string, object?>> ReadVerificationRows(WalhallaSqlDbConnection verificationConnection)
{
    using var verificationCommand = verificationConnection.CreateCommand();
    verificationCommand.CommandText = @"
SELECT u.DisplayName AS DisplayName,
       a.City AS City,
       o.OrderNumber AS OrderNumber,
       o.Status AS Status,
       o.TotalAmountCents AS TotalAmountCents
FROM Users u
LEFT JOIN Addresses a ON a.UserId = u.Id
LEFT JOIN Orders o ON o.UserId = u.Id
ORDER BY u.Id, a.Id, o.Id";

    var rows = new List<Dictionary<string, object?>>();
    using var reader = verificationCommand.ExecuteReader();
    while (reader.Read())
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
            row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);

        rows.Add(row);
    }

    return rows;
}

static string? ReadArg(string[] args, string key)
{
    for (var i = 0; i < args.Length; i++)
    {
        if (!string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
            continue;

        if (i + 1 >= args.Length)
            return null;

        return args[i + 1];
    }

    return null;
}

static object? GetValue(IReadOnlyDictionary<string, object?> row, string columnName)
{
    foreach (var pair in row)
    {
        if (string.Equals(pair.Key, columnName, StringComparison.OrdinalIgnoreCase))
            return pair.Value;
    }

    return $"<{columnName}?>";
}

public sealed class EmbeddedShopContext : DbContext
{
    public EmbeddedShopContext(DbContextOptions<EmbeddedShopContext> options)
        : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Address> Addresses => Set<Address>();
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("Users");
            entity.HasKey(user => user.Id);
            entity.Property(user => user.Id).ValueGeneratedNever();
            entity.Property(user => user.Email).IsRequired();
            entity.Property(user => user.DisplayName).IsRequired();
            entity.HasIndex(user => user.Email).IsUnique();
        });

        modelBuilder.Entity<Address>(entity =>
        {
            entity.ToTable("Addresses");
            entity.HasKey(address => address.Id);
            entity.Property(address => address.Id).ValueGeneratedNever();
            entity.Property(address => address.Label).IsRequired();
            entity.Property(address => address.Street).IsRequired();
            entity.Property(address => address.PostalCode).IsRequired();
            entity.Property(address => address.City).IsRequired();
            entity.Property(address => address.Country).IsRequired();
            entity.HasOne(address => address.User)
                .WithMany(user => user.Addresses)
                .HasForeignKey(address => address.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Order>(entity =>
        {
            entity.ToTable("Orders");
            entity.HasKey(order => order.Id);
            entity.Property(order => order.Id).ValueGeneratedNever();
            entity.Property(order => order.OrderNumber).IsRequired();
            entity.Property(order => order.Status).IsRequired();
            entity.HasIndex(order => order.OrderNumber).IsUnique();
            entity.HasOne(order => order.User)
                .WithMany(user => user.Orders)
                .HasForeignKey(order => order.UserId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}

public sealed class User
{
    public int Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public List<Address> Addresses { get; set; } = new();
    public List<Order> Orders { get; set; } = new();
}

public sealed class Address
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Label { get; set; } = string.Empty;
    public string Street { get; set; } = string.Empty;
    public string PostalCode { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public User? User { get; set; }
}

public sealed class Order
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string OrderNumber { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int TotalAmountCents { get; set; }
    public bool IsPaid { get; set; }
    public User? User { get; set; }
}
