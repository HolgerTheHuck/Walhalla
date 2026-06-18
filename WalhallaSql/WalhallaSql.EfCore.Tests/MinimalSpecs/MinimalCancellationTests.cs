using System.Threading;
using System.Threading.Tasks;
using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore;

namespace WalhallaSql.EfCore.Tests.MinimalSpecs;

/// <summary>
/// Minimalbeispiele für Cancellation-Support:
/// SaveChangesAsync mit pre-cancelled Token.
/// </summary>
[Trait("Category", "MinimalEfSpec")]
public sealed class MinimalCancellationTests
{
    [Fact]
    public async Task SaveChangesAsync_with_precanceled_token_throws_OperationCanceledException()
    {
        using var scope = MinimalSpecScope<CancelContext>.Create(
            "20260616_MinimalCancellation",
            options => new CancelContext(options));

        scope.Context.Items.Add(new CancelItem { Id = 1, Name = "Test" });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            scope.Context.SaveChangesAsync(acceptAllChangesOnSuccess: true, cts.Token));
    }

    public sealed class CancelItem
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    public sealed class CancelContext : WalhallaSqlEfCoreContext
    {
        public CancelContext(DbContextOptions options) : base(options) { }

        public DbSet<CancelItem> Items => Set<CancelItem>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<CancelItem>(entity =>
            {
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Name).IsRequired();
            });
        }
    }
}
