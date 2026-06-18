using Microsoft.EntityFrameworkCore;

namespace WalhallaSql.EfCore.Tests;

public sealed class CyclicGraphEfContext : WalhallaSqlEfCoreContext
{
    public CyclicGraphEfContext(DbContextOptions options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CyclicNode>(entity =>
        {
            entity.HasKey(node => node.Id);
            entity.Property(node => node.Name).IsRequired();
            entity.HasOne(node => node.Parent)
                .WithMany(node => node.Children)
                .HasForeignKey(node => node.ParentId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}

public sealed class CyclicNode
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? ParentId { get; set; }
    public CyclicNode? Parent { get; set; }
    public List<CyclicNode> Children { get; set; } = new();
}
