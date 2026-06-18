using Microsoft.EntityFrameworkCore.TestUtilities;

namespace Microsoft.EntityFrameworkCore;

public sealed class WalhallaSqlLoadSpecProbeRunnerTests
{
    [Theory]
    [InlineData(EntityState.Unchanged, QueryTrackingBehavior.TrackAll)]
    [InlineData(EntityState.Unchanged, QueryTrackingBehavior.NoTracking)]
    [InlineData(EntityState.Unchanged, QueryTrackingBehavior.NoTrackingWithIdentityResolution)]
    [InlineData(EntityState.Detached, QueryTrackingBehavior.TrackAll)]
    [InlineData(EntityState.Detached, QueryTrackingBehavior.NoTracking)]
    [InlineData(EntityState.Detached, QueryTrackingBehavior.NoTrackingWithIdentityResolution)]
    public Task Load_collection_probe_matches_upstream_tracking_variants(EntityState state, QueryTrackingBehavior queryTrackingBehavior)
        => RunProbe(probe => probe.Probe_load_collection(state, queryTrackingBehavior));

    [Theory]
    [InlineData(EntityState.Unchanged)]
    [InlineData(EntityState.Added)]
    [InlineData(EntityState.Modified)]
    [InlineData(EntityState.Deleted)]
    [InlineData(EntityState.Detached)]
    public Task Load_collection_composite_key_probe_matches_upstream(EntityState state)
        => RunProbe(probe => probe.Probe_load_collection_composite_key(state));

    [Theory]
    [InlineData(EntityState.Unchanged)]
    [InlineData(EntityState.Added)]
    [InlineData(EntityState.Modified)]
    [InlineData(EntityState.Deleted)]
    [InlineData(EntityState.Detached)]
    public Task Load_collection_using_query_composite_key_probe_matches_upstream(EntityState state)
        => RunProbe(probe => probe.Probe_load_collection_using_query_composite_key(state));

    [Theory]
    [InlineData(EntityState.Unchanged)]
    [InlineData(EntityState.Added)]
    [InlineData(EntityState.Modified)]
    [InlineData(EntityState.Deleted)]
    [InlineData(EntityState.Detached)]
    public Task Load_many_to_one_reference_to_principal_composite_key_probe_matches_upstream(EntityState state)
        => RunProbe(probe => probe.Probe_load_many_to_one_reference_to_principal_composite_key(state));

    [Theory]
    [InlineData(EntityState.Unchanged)]
    [InlineData(EntityState.Added)]
    [InlineData(EntityState.Modified)]
    [InlineData(EntityState.Deleted)]
    [InlineData(EntityState.Detached)]
    public Task Load_many_to_one_reference_to_principal_using_query_composite_key_probe_matches_upstream(EntityState state)
        => RunProbe(probe => probe.Probe_load_many_to_one_reference_to_principal_using_query_composite_key(state));

    [Theory]
    [InlineData(EntityState.Unchanged)]
    [InlineData(EntityState.Added)]
    [InlineData(EntityState.Modified)]
    [InlineData(EntityState.Deleted)]
    [InlineData(EntityState.Detached)]
    public Task Load_one_to_one_reference_to_principal_composite_key_probe_matches_upstream(EntityState state)
        => RunProbe(probe => probe.Probe_load_one_to_one_reference_to_principal_composite_key(state));

    [Theory]
    [InlineData(EntityState.Unchanged)]
    [InlineData(EntityState.Added)]
    [InlineData(EntityState.Modified)]
    [InlineData(EntityState.Deleted)]
    [InlineData(EntityState.Detached)]
    public Task Load_one_to_one_reference_to_principal_using_query_composite_key_probe_matches_upstream(EntityState state)
        => RunProbe(probe => probe.Probe_load_one_to_one_reference_to_principal_using_query_composite_key(state));

    [Theory]
    [InlineData(EntityState.Unchanged)]
    [InlineData(EntityState.Added)]
    [InlineData(EntityState.Modified)]
    [InlineData(EntityState.Deleted)]
    [InlineData(EntityState.Detached)]
    public Task Load_one_to_one_reference_to_dependent_composite_key_probe_matches_upstream(EntityState state)
        => RunProbe(probe => probe.Probe_load_one_to_one_reference_to_dependent_composite_key(state));

    [Theory]
    [InlineData(EntityState.Unchanged)]
    [InlineData(EntityState.Added)]
    [InlineData(EntityState.Modified)]
    [InlineData(EntityState.Deleted)]
    [InlineData(EntityState.Detached)]
    public Task Load_one_to_one_reference_to_dependent_using_query_composite_key_probe_matches_upstream(EntityState state)
        => RunProbe(probe => probe.Probe_load_one_to_one_reference_to_dependent_using_query_composite_key(state));

    [Fact]
    public Task Composite_key_seed_data_persists_parent_alternate_key_and_child_foreign_keys()
        => RunProbe(probe => probe.Probe_assert_composite_key_seed_data());

    [Fact]
    public Task Manual_principal_composite_key_query_matches_but_reference_query_path_can_be_compared()
        => RunProbe(probe => probe.Probe_compare_manual_and_reference_principal_queries());

    [Fact]
    public Task Manual_principal_composite_key_query_can_be_decomposed_into_single_predicates()
        => RunProbe(probe => probe.Probe_decompose_manual_principal_query());

    [Fact]
    public Task Manual_principal_composite_key_query_matches_reference_query_path_under_no_tracking()
        => RunProbe(probe => probe.Probe_compare_manual_and_reference_principal_queries_no_tracking());

    [Theory]
    [InlineData(EntityState.Unchanged)]
    [InlineData(EntityState.Added)]
    [InlineData(EntityState.Modified)]
    [InlineData(EntityState.Deleted)]
    [InlineData(EntityState.Detached)]
    public Task Load_one_to_one_reference_to_principal_when_no_tracking_behavior_probe_matches_upstream(EntityState state)
        => RunProbe(probe => probe.Probe_load_one_to_one_reference_to_principal_when_no_tracking_behavior(state));

    private static async Task RunProbe(Func<LoadProbe, Task> run)
    {
        var fixture = new LoadProbe.LoadFixture();
        await fixture.InitializeAsync();

        try
        {
            await run(new LoadProbe(fixture));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private sealed class LoadProbe(LoadProbe.LoadFixture fixture)
        : LoadTestBase<LoadProbe.LoadFixture>(fixture)
    {
        public Task Probe_load_collection(EntityState state, QueryTrackingBehavior queryTrackingBehavior)
            => Load_collection(state, queryTrackingBehavior, async: false);

        public Task Probe_load_collection_composite_key(EntityState state)
            => Load_collection_composite_key(state, async: false);

        public Task Probe_load_collection_using_query_composite_key(EntityState state)
            => Load_collection_using_Query_composite_key(state, async: false);

        public Task Probe_load_many_to_one_reference_to_principal_composite_key(EntityState state)
            => Load_many_to_one_reference_to_principal_composite_key(state, async: false);

        public Task Probe_load_many_to_one_reference_to_principal_using_query_composite_key(EntityState state)
            => Load_many_to_one_reference_to_principal_using_Query_composite_key(state, async: false);

        public Task Probe_load_one_to_one_reference_to_principal_composite_key(EntityState state)
            => Load_one_to_one_reference_to_principal_composite_key(state, async: false);

        public Task Probe_load_one_to_one_reference_to_principal_using_query_composite_key(EntityState state)
            => Load_one_to_one_reference_to_principal_using_Query_composite_key(state, async: false);

        public Task Probe_load_one_to_one_reference_to_dependent_composite_key(EntityState state)
            => Load_one_to_one_reference_to_dependent_composite_key(state, async: false);

        public Task Probe_load_one_to_one_reference_to_dependent_using_query_composite_key(EntityState state)
            => Load_one_to_one_reference_to_dependent_using_Query_composite_key(state, async: false);

        public Task Probe_assert_composite_key_seed_data()
        {
            using var context = CreateContext();

            var parent = context.Set<Parent>().Single(entity => entity.Id == 707);
            Assert.Equal("Root", parent.AlternateId);

            var children = context.Set<ChildCompositeKey>().OrderBy(entity => entity.Id).ToList();
            Assert.Equal(new[] { 51, 52 }, children.Select(entity => entity.Id).ToArray());
            Assert.All(children, child =>
            {
                Assert.Equal(707, child.ParentId);
                Assert.Equal("Root", child.ParentAlternateId);
            });

            var single = context.Set<SingleCompositeKey>().Single(entity => entity.Id == 62);
            Assert.Equal(707, single.ParentId);
            Assert.Equal("Root", single.ParentAlternateId);

            return Task.CompletedTask;
        }

        public Task Probe_compare_manual_and_reference_principal_queries()
        {
            using var context = CreateContext();

            var child = context.Set<ChildCompositeKey>().Single(entity => entity.Id == 52);
            var manualParents = context.Set<Parent>()
                .Where(parent => parent.AlternateId == child.ParentAlternateId && parent.Id == child.ParentId)
                .ToList();

            Assert.Single(manualParents);

            var reference = context.Entry(child).Reference(entity => entity.Parent);
            var referenceParents = reference.Query().ToList();

            Assert.Single(referenceParents);
            return Task.CompletedTask;
        }

        public Task Probe_decompose_manual_principal_query()
        {
            using var context = CreateContext();

            var child = context.Set<ChildCompositeKey>().Single(entity => entity.Id == 52);
            Assert.Equal(707, child.ParentId);
            Assert.Equal("Root", child.ParentAlternateId);

            var parentId = child.ParentId;
            var parentAlternateId = child.ParentAlternateId;

            var byIdConstant = context.Set<Parent>()
                .Where(parent => parent.Id == 707)
                .ToList();

            var byAlternateIdConstant = context.Set<Parent>()
                .Where(parent => parent.AlternateId == "Root")
                .ToList();

            var byIdLocal = context.Set<Parent>()
                .Where(parent => parent.Id == parentId)
                .ToList();

            var byAlternateIdLocal = context.Set<Parent>()
                .Where(parent => parent.AlternateId == parentAlternateId)
                .ToList();

            var byId = context.Set<Parent>()
                .Where(parent => parent.Id == child.ParentId)
                .ToList();

            var byAlternateId = context.Set<Parent>()
                .Where(parent => parent.AlternateId == child.ParentAlternateId)
                .ToList();

            var byBoth = context.Set<Parent>()
                .Where(parent => parent.Id == child.ParentId && parent.AlternateId == child.ParentAlternateId)
                .ToList();

            Assert.Single(byIdConstant);
            Assert.Single(byAlternateIdConstant);
            Assert.Single(byIdLocal);
            Assert.Single(byAlternateIdLocal);
            Assert.Single(byId);
            Assert.Single(byAlternateId);
            Assert.Single(byBoth);
            return Task.CompletedTask;
        }

        public Task Probe_compare_manual_and_reference_principal_queries_no_tracking()
        {
            using var context = CreateContext();
            context.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

            var child = context.Set<ChildCompositeKey>().Single(entity => entity.Id == 52);
            var manualParents = context.Set<Parent>()
                .Where(parent => parent.AlternateId == child.ParentAlternateId && parent.Id == child.ParentId)
                .ToList();

            Assert.Single(manualParents);

            var referenceParents = context.Entry(child)
                .Reference(entity => entity.Parent)
                .Query()
                .ToList();

            Assert.Single(referenceParents);
            Assert.Equal(manualParents[0].Id, referenceParents[0].Id);
            Assert.Equal(manualParents[0].AlternateId, referenceParents[0].AlternateId);
            return Task.CompletedTask;
        }

        public Task Probe_load_one_to_one_reference_to_principal_when_no_tracking_behavior(EntityState state)
            => Load_one_to_one_reference_to_principal_when_NoTracking_behavior(state, async: false);

        public sealed class LoadFixture : LoadFixtureBase
        {
            protected override ITestStoreFactory TestStoreFactory
                => LayeredSqlTestStoreFactory.Instance;

            public override async Task InitializeAsync()
            {
                await base.InitializeAsync();
            }
        }
    }
}
