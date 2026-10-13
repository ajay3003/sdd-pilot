using BirkNext.Integrations;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

public sealed class SourceAnalysisSnapshotStateTests
{
    [Fact]
    public async Task SameWorkspaceAndEnvironmentReusesTheImmutableSnapshotList()
    {
        var state = new SourceAnalysisSnapshotState();
        var key = new SourceAnalysisSnapshotKey("dev", Guid.NewGuid(), "shop", 4);
        var calls = 0;
        IReadOnlyList<IqrSourceSnapshot> expected = [new() { Id = Guid.NewGuid() }];
        Task<IReadOnlyList<IqrSourceSnapshot>> Load()
        {
            calls++;
            return Task.FromResult(expected);
        }

        var first = await state.GetOrLoadAsync(key, Load);
        var revisit = await state.GetOrLoadAsync(key, Load);

        calls.Should().Be(1);
        ReferenceEquals(first, revisit).Should().BeTrue();
        ReferenceEquals(revisit, expected).Should().BeTrue("the cache holds the canonical response references, not a duplicate evidence graph");
    }

    [Fact]
    public async Task WorkspaceVersionWorkspaceIdentityEnvironmentAndExplicitInvalidationRefresh()
    {
        var state = new SourceAnalysisSnapshotState();
        var workspaceId = Guid.NewGuid();
        var key = new SourceAnalysisSnapshotKey("dev", workspaceId, "shop", 4);
        var calls = 0;
        Task<IReadOnlyList<IqrSourceSnapshot>> Load()
        {
            calls++;
            return Task.FromResult<IReadOnlyList<IqrSourceSnapshot>>([new() { Id = Guid.NewGuid() }]);
        }

        await state.GetOrLoadAsync(key, Load);
        await state.GetOrLoadAsync(key with { WorkspaceVersion = 5 }, Load);
        await state.GetOrLoadAsync(key with { WorkspaceId = Guid.NewGuid() }, Load);
        await state.GetOrLoadAsync(key with { EnvironmentId = "qa" }, Load);
        state.Invalidate();
        await state.GetOrLoadAsync(key, Load);

        calls.Should().Be(5);
    }

    [Fact]
    public async Task FailedLoadIsNotCachedAndSelectedSnapshotIsScopedToTheCacheKey()
    {
        var state = new SourceAnalysisSnapshotState();
        var key = new SourceAnalysisSnapshotKey("dev", Guid.NewGuid(), "shop", 1);
        var calls = 0;
        Func<Task<IReadOnlyList<IqrSourceSnapshot>>> fail = () =>
        {
            calls++;
            return Task.FromException<IReadOnlyList<IqrSourceSnapshot>>(new InvalidOperationException("offline"));
        };

        await FluentActions.Awaiting(() => state.GetOrLoadAsync(key, fail)).Should().ThrowAsync<InvalidOperationException>();
        await state.GetOrLoadAsync(key, () =>
        {
            calls++;
            return Task.FromResult<IReadOnlyList<IqrSourceSnapshot>>([]);
        });
        state.SelectSnapshot(key, "snapshot-a");

        state.SelectedSnapshotId(key).Should().Be("snapshot-a");
        state.SelectedSnapshotId(key with { WorkspaceVersion = 2 }).Should().BeNull();
        calls.Should().Be(2);
    }
}
