using System;
using System.Collections.Generic;
using System.Threading;
using WalhallaSql.AdoNet;
using WalhallaSql.EfCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace WalhallaSql.EfCore.Tests;

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
            entity.Property(x => x.ExternalCode).HasConversion<string>();
            entity.Ignore(x => x.PostCount);
        });

        modelBuilder.Entity<UserPost>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Title).IsRequired();
            entity.Property(x => x.ExternalCode).HasConversion<string>();
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

public sealed class CancellationAwareEfContext : WalhallaSqlEfCoreContext
{
    private int _executedCount;

    public CancellationAwareEfContext(DbContextOptions options)
        : base(options)
    {
    }

    public int CancelOnAfterExecutionCount { get; set; }
    public CancellationTokenSource? CancelSource { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserProjection>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).IsRequired();
            entity.Ignore(x => x.PostCount);
            entity.Ignore(x => x.Posts);
        });
    }

    protected override void OnSaveChangesCommandExecuted(EntityEntry entry, SqlExecutionResult result)
    {
        _executedCount++;
        if (CancelOnAfterExecutionCount > 0 && _executedCount >= CancelOnAfterExecutionCount)
            CancelSource?.Cancel();

        base.OnSaveChangesCommandExecuted(entry, result);
    }
}

public sealed class UserProjection
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public Guid? ExternalCode { get; set; }
    public int PostCount { get; set; }
    public List<UserPost> Posts { get; set; } = new();
}

public sealed class UserPost
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int? ReviewerId { get; set; }
    public string Title { get; set; } = string.Empty;
    public Guid? ExternalCode { get; set; }
    public UserProjection? User { get; set; }
    public UserProjection? Reviewer { get; set; }
}
