using BirkNext.Api.Models;
using BirkNext.TestPlanning;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.TestPlanning;

/// <summary>
/// Plan lifecycle (2026-11-23): archive, restore and (logical) delete. Each change is made at the revision the tester saw (a changed plan is refused
/// with 409), creates a revision with the reason (audit) and leaves the plan's content, revisions, translations, review history and frozen snapshots
/// untouched. Delete is logical because snapshots are immutable and reference the plan: the row stays and a global query filter hides it from every
/// query. Only an archived plan can be deleted, and only when the tester types its name.
/// </summary>
public sealed partial class TestPlanSuggestionService
{
    private const string ActiveState = nameof(TestPlanLifecycle.Active), ArchivedState = nameof(TestPlanLifecycle.Archived), DeletedState = nameof(TestPlanLifecycle.Deleted);

    /// <summary>The plan's row for a change to its content: refused while the plan is archived (restore it first).</summary>
    private async Task<TestPlanDraftRecord> Mutable(Guid id, CancellationToken ct)
    {
        var row = await Row(id, ct);
        if (row.Lifecycle != ActiveState)
            throw new TestPlanException(409, "plan-archived", "Planen er arkivert og kan bare leses. Gjenopprett den for å endre den.");
        return row;
    }

    public Task<TestPlanDraftSummary> ArchiveAsync(Guid id, TestPlanLifecycleRequest request, string actor, CancellationToken ct) =>
        ChangeLifecycleAsync(id, request.BaseRevision, ActiveState, ArchivedState, "Arkivert", request.Reason, actor, ct);

    public Task<TestPlanDraftSummary> RestoreFromArchiveAsync(Guid id, TestPlanLifecycleRequest request, string actor, CancellationToken ct) =>
        ChangeLifecycleAsync(id, request.BaseRevision, ArchivedState, ActiveState, "Gjenopprettet fra arkivet", request.Reason, actor, ct);

    public async Task DeleteAsync(Guid id, DeleteTestPlanRequest request, string actor, CancellationToken ct)
    {
        var row = await Row(id, ct);
        if (!string.Equals(request.ConfirmName?.Trim(), row.PlanName.Trim(), StringComparison.Ordinal))
            throw new TestPlanException(400, "confirm-name", "Skriv planens navn nøyaktig for å bekrefte slettingen.");
        await ChangeLifecycleAsync(id, request.BaseRevision, ArchivedState, DeletedState, "Slettet", null, actor, ct);
    }

    private async Task<TestPlanDraftSummary> ChangeLifecycleAsync(Guid id, int baseRevision, string from, string to, string reason, string? note, string actor,
        CancellationToken ct)
    {
        var row = await Row(id, ct);
        if (row.Revision != baseRevision) throw Stale(row);
        if (row.Lifecycle != from)
            throw new TestPlanException(409, from == ArchivedState ? "plan-not-archived" : "plan-archived",
                from == ArchivedState ? "Bare en arkivert plan kan gjenopprettes eller slettes. Arkiver planen først." : "Planen er allerede arkivert.");
        var plan = Read(row) with { Revision = row.Revision + 1, UpdatedBy = actor, UpdatedAt = Now };
        row.Lifecycle = to;
        row.LifecycleChangedAt = Now;
        row.LifecycleChangedBy = actor;
        row.LifecycleReason = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(note.Trim().Length, 500)];
        var text = string.IsNullOrWhiteSpace(row.LifecycleReason) ? reason : $"{reason}: {row.LifecycleReason}";
        await WriteAsync(row, plan, ct, text);
        return Summary(plan, row);
    }

    /// <summary>Rows of one owner (project or guide) that are not deleted, newest first, split by lifecycle by the caller.</summary>
    private static bool IsActive(TestPlanDraftRecord row) => row.Lifecycle == ActiveState;
}
