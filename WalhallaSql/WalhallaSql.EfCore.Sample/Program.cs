using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WalhallaSql;
using WalhallaSql.EfCore;
using WalhallaSql.EfCore.Migrations;
using WalhallaSql.EfCore.Linq;
using WalhallaSql.Sql;
using Microsoft.EntityFrameworkCore;

var dbPath = Path.Combine(Path.GetTempPath(), "WalhallaSql", "EfSample", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dbPath);

using var engine = WalhallaEngine.Open(dbPath);

var layeredOptions = new WalhallaSqlEfCoreOptions(engine)
{
	CollectionNameResolver = entity => entity.ClrType?.Name == nameof(UserProjection) ? "Users" : entity.ClrType?.Name ?? entity.Name
};

var options = new DbContextOptionsBuilder<AppEfContext>()
	.UseWalhallaSql(layeredOptions)
	.Options;

using var context = new AppEfContext(options);

var migration = context.Migrations.ApplyPlannedChanges("20260220_InitialModel");
Console.WriteLine($"Model migration applied: {migration.AppliedOperations} operations ({migration.MigrationId})");

foreach (var entry in context.Migrations.GetHistory())
{
	Console.WriteLine($"Migration history => {entry.MigrationId} at {entry.AppliedAtUtc:O} ({entry.OperationCount} ops)");
}

context.ExecuteSql("CREATE INDEX IX_Users_Age ON Users (Age)");

context.ExecuteSql("INSERT INTO Users (Id, Name, Age) VALUES (1, 'Ada Lovelace', 30)");
context.ExecuteSql("INSERT INTO Users (Id, Name, Age) VALUES (2, 'Alan Turing', 41)");
context.ExecuteSql("INSERT INTO UserPost (Id, UserId, ReviewerId, Title) VALUES (100, 1, 2, 'Hello FK')");
context.ExecuteSql("INSERT INTO UserPost (Id, UserId, ReviewerId, Title) VALUES (102, 1, 2, 'Second FK')");

try
{
	context.ExecuteSql("INSERT INTO UserPost (Id, UserId, Title) VALUES (101, 999, 'Broken FK')");
	Console.WriteLine("FK check: unexpected success");
}
catch (NotSupportedException)
{
	Console.WriteLine("FK check: rejected as expected");
}

var result = context.ExecuteSql("SELECT Id, Name FROM Users WHERE Age >= 30");

Console.WriteLine($"Rows: {result.AffectedRows}");
if (result.Rows != null)
{
	Console.WriteLine("SQL Users (Age >= 30):");
	foreach (var row in result.Rows)
	{
		var id = row.TryGetValue("Id", out var idValue) ? idValue : null;
		var name = row.TryGetValue("Name", out var nameValue) ? nameValue : null;
		Console.WriteLine($"{id} | {name}");
	}
}

var linqRows = context.Query<UserProjection>("Users")
	.Where(user => user.Age >= 30)
	.Select(user => new { user.Id, user.Name, user.Age })
	.OrderByDescending(user => user.Age)
	.Skip(0)
	.Take(10)
	.ToRows();

Console.WriteLine($"LINQ-like Rows: {linqRows.Count}");
foreach (var row in linqRows)
{
	Console.WriteLine($"LINQ Row => {row["Id"]} | {row["Name"]} | {row["Age"]}");
}

var anyAdults = context.Query<UserProjection>("Users")
	.Where(user => user.Age >= 18)
	.Any();

var adultCount = context.Query<UserProjection>("Users")
	.Where(user => user.Age >= 18)
	.Count();

var firstAdult = context.Query<UserProjection>("Users")
	.Where(user => user.Age >= 18)
	.OrderBy(user => user.Id)
	.FirstOrDefault();

var singleAda = context.Query<UserProjection>("Users")
	.Where(user => user.Id == 1)
	.Single();

var ids = new[] { 1, 3, 5 };
var containsRows = context.Query<UserProjection>("Users")
	.Where(user => ids.Contains(user.Id))
	.OrderBy(user => user.Id)
	.ToRows();

var startsWithRows = context.Query<UserProjection>("Users")
	.Where(user => user.Name.StartsWith("A"))
	.OrderBy(user => user.Name)
	.ToRows();

var includeRows = context.Query<UserPost>("UserPost")
	.AsSplitQuery()
	.Include(post => post.User)
	.Include(post => post.Reviewer)
	.OrderBy(post => post.Id)
	.ToRows();

var includeSingleQueryRows = context.Query<UserPost>("UserPost")
	.AsSingleQuery()
	.Include(post => post.User)
	.ToRows();

var includeSingleQueryMultiRows = context.Query<UserPost>("UserPost")
	.AsSingleQuery()
	.Include(post => post.User)
	.Include(post => post.Reviewer)
	.OrderBy(post => post.Id)
	.ToRows();

var includeSingleQueryShapedRows = context.Query<UserPost>("UserPost")
	.AsSingleQuery()
	.Include(post => post.User)
	.Where(post => post.Id >= 100)
	.OrderByDescending(post => post.Id)
	.Take(1)
	.ToRows();

var includeCollectionRows = context.Query<UserProjection>("Users")
	.AsSplitQuery()
	.Include(user => user.Posts)
	.ThenInclude((UserPost post) => post.User)
	.OrderBy(user => user.Id)
	.ToRows();

var includeFilteredCollectionRows = context.Query<UserProjection>("Users")
	.AsSplitQuery()
	.Include(user => user.Posts.Where(post => post.Id >= 100).OrderByDescending(post => post.Id).Take(1))
	.OrderBy(user => user.Id)
	.ToRows();

var includeFilteredReferenceRows = context.Query<UserPost>("UserPost")
	.AsSplitQuery()
	.Include(post => post.User!, user => user.Age >= 35)
	.Include(post => post.Reviewer!, user => user.Age >= 35)
	.OrderBy(post => post.Id)
	.ToRows();

var includePagedRows = context.Query<UserProjection>("Users")
	.AsSplitQuery()
	.Include(user => user.Posts)
	.OrderBy(user => user.Id)
	.Skip(1)
	.Take(1)
	.ToRows();

var includePaginationWithoutOrderRejected = false;
try
{
	context.Query<UserProjection>("Users")
		.AsSplitQuery()
		.Include(user => user.Posts)
		.Skip(1)
		.Take(1)
		.ToRows();
}
catch (NotSupportedException)
{
	includePaginationWithoutOrderRejected = true;
}

Console.WriteLine("LINQ Summary:");
Console.WriteLine($"- Adults: any={anyAdults}, count={adultCount}, first={firstAdult?["Name"]}, singleAda={singleAda["Name"]}");
Console.WriteLine($"- Predicates: containsRows={containsRows.Count}, startsWithRows={startsWithRows.Count}");
Console.WriteLine($"- Include split/single: splitRef={includeRows.Count}, singleRef={includeSingleQueryRows.Count}, singleMultiRef={includeSingleQueryMultiRows.Count}, singleShaped={includeSingleQueryShapedRows.Count}");
Console.WriteLine($"- Include deep/filter/paging: collection={includeCollectionRows.Count}, filteredCollection={includeFilteredCollectionRows.Count}, filteredReference={includeFilteredReferenceRows.Count}, pagedCollection={includePagedRows.Count}, pagingGuard={includePaginationWithoutOrderRejected}");

context.ExecuteSql(@"
CREATE TABLE Authors (
    Id INT PRIMARY KEY,
    Name VARCHAR(200) NOT NULL
)");

context.ExecuteSql(@"
CREATE TABLE Books (
    Id INT PRIMARY KEY,
    Title VARCHAR(200) NOT NULL,
    AuthorId INT
)");

context.ExecuteSql("INSERT INTO Authors (Id, Name) VALUES (1, 'Author One')");
context.ExecuteSql("INSERT INTO Authors (Id, Name) VALUES (2, 'Author Two')");
context.ExecuteSql("INSERT INTO Books (Id, Title, AuthorId) VALUES (1, 'Matched Book', 1)");
context.ExecuteSql("INSERT INTO Books (Id, Title, AuthorId) VALUES (2, 'Unmatched Book', 99)");

var leftJoin = context.ExecuteSql("SELECT b.Title, a.Name FROM Books b LEFT JOIN Authors a ON b.AuthorId = a.Id");
Console.WriteLine($"LEFT JOIN Rows: {leftJoin.AffectedRows}");
if (leftJoin.Rows != null)
{
	Console.WriteLine("LEFT JOIN preview:");
	foreach (var row in leftJoin.Rows)
	{
		var title = row.TryGetValue("b.Title", out var titleValue) ? titleValue : null;
		var author = row.TryGetValue("a.Name", out var authorValue) ? authorValue : null;
		Console.WriteLine($"{title} | {author}");
	}
}

var union = context.ExecuteSql("SELECT Id FROM Users WHERE Age >= 40 UNION SELECT Id FROM Users WHERE Age <= 30 UNION ALL SELECT Id FROM Users WHERE Age = 30");
Console.WriteLine($"UNION Chain Rows: {union.AffectedRows}");
if (union.Rows != null)
{
	foreach (var row in union.Rows)
	{
		var id = row.TryGetValue("Id", out var idValue) ? idValue : null;
		Console.WriteLine($"UNION Id: {id}");
	}
}

ExecuteMigrationLockContentionScenario(engine);

Console.WriteLine($"DatabasePath: {dbPath}");

void ExecuteMigrationLockContentionScenario(WalhallaEngine engine)
{
	var mutexName = BuildMigrationMutexName(dbPath);
	using var holderReady = new ManualResetEventSlim(false);
	using var holderRelease = new ManualResetEventSlim(false);

	var holderTask = Task.Run(() =>
	{
		using var mutex = new Mutex(false, mutexName);
		var hasMutex = false;
		try
		{
			hasMutex = mutex.WaitOne(TimeSpan.FromSeconds(2));
			holderReady.Set();
			holderRelease.Wait(TimeSpan.FromSeconds(5));
		}
		finally
		{
			if (hasMutex)
				mutex.ReleaseMutex();
		}
	});

	holderReady.Wait(TimeSpan.FromSeconds(3));

	var lockOptions = new WalhallaSqlEfCoreOptions(engine)
	{
		CollectionNameResolver = entity => entity.ClrType?.Name == nameof(UserProjection) ? "Users" : entity.ClrType?.Name ?? entity.Name,
		MigrationLockWaitTimeout = TimeSpan.FromMilliseconds(300)
	};

	var options = new DbContextOptionsBuilder<AppEfContext>()
		.UseWalhallaSql(lockOptions)
		.Options;

	using var lockContext = new AppEfContext(options);

	var blocked = false;
	try
	{
		lockContext.Migrations.ApplyPlannedChanges("LockContentionScenario");
	}
	catch (InvalidOperationException ex) when (ex.Message.Contains("Could not acquire migration lock", StringComparison.OrdinalIgnoreCase))
	{
		blocked = true;
	}
	finally
	{
		holderRelease.Set();
		holderTask.Wait(TimeSpan.FromSeconds(3));
	}

	if (!blocked)
		throw new InvalidOperationException("Migration lock contention scenario expected a timeout, but migration call succeeded.");

	Console.WriteLine("Migration lock contention guard: PASS");
}

string BuildMigrationMutexName(string databasePath)
{
	var databaseName = "App";
	return WalhallaSqlMigrationService.BuildEmbeddedPathMutexName(
		databasePath,
		databaseName,
		typeof(WalhallaEngine).FullName ?? nameof(WalhallaEngine));
}

public sealed class AppEfContext : WalhallaSqlEfCoreContext
{
	public AppEfContext(DbContextOptions options)
		: base(options)
	{
	}

	protected override void OnModelCreating(ModelBuilder modelBuilder)
	{
		modelBuilder.Entity<UserProjection>(entity =>
		{
			entity.HasKey(x => x.Id);
			entity.Property(x => x.Name).IsRequired();
			entity.Ignore(x => x.PostCount);
		});

		modelBuilder.Entity<UserPost>(entity =>
		{
			entity.HasKey(x => x.Id);
			entity.Property(x => x.Title).IsRequired();
			entity.Property(x => x.ReviewerId);
			entity.HasOne(x => x.User)
				.WithMany(user => user.Posts)
				.HasForeignKey(x => x.UserId)
				.OnDelete(DeleteBehavior.Restrict);
			entity.HasOne(x => x.Reviewer)
				.WithMany()
				.HasForeignKey(x => x.ReviewerId)
				.OnDelete(DeleteBehavior.Restrict);
		});
	}
}

public sealed class UserProjection
{
	public int Id { get; set; }
	public string Name { get; set; } = string.Empty;
	public int Age { get; set; }
	public int PostCount { get; set; }
	public List<UserPost> Posts { get; set; } = new();
}

public sealed class UserPost
{
	public int Id { get; set; }
	public int UserId { get; set; }
	public int? ReviewerId { get; set; }
	public string Title { get; set; } = string.Empty;
	public UserProjection? User { get; set; }
	public UserProjection? Reviewer { get; set; }
}
