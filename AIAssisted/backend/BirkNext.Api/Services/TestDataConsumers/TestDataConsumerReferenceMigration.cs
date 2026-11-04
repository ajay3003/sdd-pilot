using BirkNext.Api.Services.CriticalE2E;
using BirkNext.Api.Services.PerformanceTests;
using BirkNext.PerformanceTests;
using BirkNext.TestData;

using BirkNext.Api.Services.TestData;

namespace BirkNext.Api.Services.TestDataConsumers;

/// <summary>What one migration pass did. Counts are references (not definitions).</summary>
public sealed record TestDataReferenceMigrationResult(int Migrated, int AlreadyStable, int NeedsReview, int Unresolved, int DefinitionsUpdated, int FlowsUpdated);

/// <summary>
/// Records stable TestDataAssetIds on Performance definitions and Critical E2E flows that still hold alias-only references. Idempotent and safe:
/// an alias is migrated only when it provably maps to one identity (<see cref="TestDataReferenceResolver"/>); a reused or ambiguous alias stays
/// alias-only and is surfaced as "Needs review"; an unknown alias is preserved as written. No new definition/flow version is created (ids are
/// reference bookkeeping), and runs/history are never rewritten.
/// </summary>
public sealed class TestDataConsumerReferenceMigration(TestDataCatalog catalog, PerformanceTestStore performance, ICriticalE2EStore criticalE2E, TimeProvider time)
{
    public async Task<TestDataReferenceMigrationResult> MigrateAsync(CancellationToken ct)
    {
        var assets = catalog.Assets();
        var identities = catalog.Identities?.Identities() ?? [];
        if (identities.Count == 0) return new(0, 0, 0, 0, 0, 0); // no stable ids in this host: nothing to record
        int migrated = 0, stable = 0, review = 0, unresolved = 0, definitions = 0, flows = 0;

        List<string?> Ids(IReadOnlyList<string> aliases, IReadOnlyList<string?>? ids)
        {
            var result = new List<string?>(aliases.Count);
            for (var i = 0; i < aliases.Count; i++)
            {
                var existing = TestDataReferenceText.IdAt(ids, i);
                if (existing is not null) { stable++; result.Add(existing); continue; }
                var reference = TestDataReferenceResolver.Resolve(assets, identities, aliases[i], null);
                switch (reference.State)
                {
                    case TestDataReferenceState.LegacyUnique when reference.AssetId is not null: migrated++; result.Add(reference.AssetId); break;
                    case TestDataReferenceState.LegacyNeedsReview: review++; result.Add(null); break;
                    default: unresolved++; result.Add(null); break;
                }
            }
            return result;
        }
        static bool Same(IReadOnlyList<string?>? a, IReadOnlyList<string?> b) => (a ?? []).Count == b.Count && (a ?? []).SequenceEqual(b) || (a is null && b.All(x => x is null));

        foreach (var definition in await performance.AllActiveDefinitionsAsync(ct))
        {
            var changed = false;
            var steps = definition.Scenario.Steps.Select(step => step.DataMappings is null ? step : step with
            {
                DataMappings = [.. step.DataMappings.Select(m =>
                {
                    if (m.Source != RuntimeValueSource.TestData || m.AssetAliases.Count == 0) return m;
                    var ids = Ids(m.AssetAliases, m.AssetIds);
                    if (Same(m.AssetIds, ids)) return m;
                    changed = true;
                    return m with { AssetIds = ids.Any(x => x is not null) ? ids : null };
                })],
            }).ToList();
            var variables = definition.Scenario.Variables?.Select(v =>
            {
                if (v is not { Source: RuntimeValueSource.TestData, AssetAlias: { Length: > 0 } alias } || v.AssetId is not null) { if (v.AssetId is not null) stable++; return v; }
                var id = Ids([alias], null)[0];
                if (id is null) return v;
                changed = true;
                return v with { AssetId = id };
            }).ToList();
            if (!changed) continue;
            await performance.SaveDefinitionAsync(definition.EnvironmentId, definition with { Scenario = definition.Scenario with { Steps = steps, Variables = variables } }, time.GetUtcNow(), ct);
            definitions++;
        }

        foreach (var flow in criticalE2E.Flows(""))
        {
            var changed = false;
            var mappings = flow.DataMappings?.Select(m =>
            {
                var ids = Ids(m.AssetAliases, m.AssetIds);
                if (Same(m.AssetIds, ids)) return m;
                changed = true;
                return m with { AssetIds = ids.Any(x => x is not null) ? ids : null };
            }).ToList();
            var flowIds = flow.TestDataAliases is { Count: > 0 } aliases ? Ids(aliases, flow.TestDataAssetIds) : null;
            if (flowIds is not null && !Same(flow.TestDataAssetIds, flowIds)) changed = true;
            if (!changed) continue;
            criticalE2E.Save(flow with { DataMappings = mappings, TestDataAssetIds = flowIds is not null && flowIds.Any(x => x is not null) ? flowIds : null });
            flows++;
        }
        return new(migrated, stable, review, unresolved, definitions, flows);
    }
}

/// <summary>Runs the reference migration once after startup (background; failures are logged, never fatal).</summary>
public sealed class TestDataConsumerReferenceMigrationHostedService(IServiceProvider services, ILogger<TestDataConsumerReferenceMigrationHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            using var scope = services.CreateScope();
            var result = await scope.ServiceProvider.GetRequiredService<TestDataConsumerReferenceMigration>().MigrateAsync(stoppingToken);
            logger.LogInformation("Test Data consumer references: {Migrated} migrated to stable ids, {Stable} already stable, {Review} need review, {Unresolved} unresolved",
                result.Migrated, result.AlreadyStable, result.NeedsReview, result.Unresolved);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { logger.LogWarning(ex, "Test Data consumer reference migration skipped"); }
    }
}
