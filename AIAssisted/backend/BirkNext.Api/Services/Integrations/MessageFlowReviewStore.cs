using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.Api.Data;
using BirkNext.Api.Models;
using BirkNext.Integrations;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Services.Integrations;

public interface IIntegrationMessageFlowStore
{
    Task<IntegrationMessageFlowPackage> GetAsync(string environmentId, CancellationToken ct = default);
    Task<(IntegrationMessageFlowPackage? Package, string? Error)> SaveAsync(string environmentId, IntegrationMessageFlowPackage request, CancellationToken ct = default);
}

public sealed class IntegrationMessageFlowStore(AppDbContext db, TimeProvider? timeProvider = null) : IIntegrationMessageFlowStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<IntegrationMessageFlowPackage> GetAsync(string environmentId, CancellationToken ct = default)
    {
        var row = await db.IntegrationMessageFlows.AsNoTracking().SingleOrDefaultAsync(r => r.EnvironmentId == environmentId, ct);
        if (row is not null && JsonSerializer.Deserialize<IntegrationMessageFlowPackage>(row.DocumentJson, Json) is { } stored)
            return Recalculate(stored with { Flow = stored.Flow with { EnvironmentId = environmentId } });

        // Never infer a project from project names. This is a generic blank workspace; the UI offers explicit example loading.
        var flow = new MessageFlowDefinition { EnvironmentId = environmentId, Name = "Message flow review", Description = "Documented integration design; source and runtime evidence are separate." };
        return Recalculate(new IntegrationMessageFlowPackage(flow, new AltinnTestConfiguration(), new(false, 0, 0, [], []), [], []));
    }

    public async Task<(IntegrationMessageFlowPackage? Package, string? Error)> SaveAsync(string environmentId, IntegrationMessageFlowPackage request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(environmentId) || environmentId.Length > 200) return (null, "A valid Target Environment id is required.");
        if (request.Flow.Nodes.Count > 300 || request.Flow.Hops.Count > 500 || request.Flow.Checkpoints.Count > 500 || request.Flow.ErrorPaths.Count > 300)
            return (null, "The message flow exceeds the supported review size.");
        var nodeIdList = request.Flow.Nodes.Select(n => n.Id).ToList();
        var nodeIds = nodeIdList.ToHashSet(StringComparer.Ordinal);
        if (nodeIds.Count != nodeIdList.Count || request.Flow.Nodes.Any(n => string.IsNullOrWhiteSpace(n.Id) || string.IsNullOrWhiteSpace(n.Name) || string.IsNullOrWhiteSpace(n.StateOwner)) ||
            request.Flow.Hops.Any(h => !nodeIds.Contains(h.FromNodeId) || !nodeIds.Contains(h.ToNodeId)))
            return (null, "Every hop must reference existing flow nodes.");

        var suppliedConfig = request.AltinnConfiguration;
        var configuredValues = new[] { suppliedConfig.AppIdentifier, suppliedConfig.EndpointReference, suppliedConfig.ClientReference,
            suppliedConfig.ReporterReference, suppliedConfig.SyntheticIdentitySetReference, suppliedConfig.MuChannelReference,
            suppliedConfig.ContractReference, suppliedConfig.QueueReference, suppliedConfig.CheckpointConfigurationReference };
        var config = suppliedConfig with { ProductionBlocked = true, VerificationState = FlowEvidenceState.NotAssessed,
            TargetEnvironmentId = environmentId, VerifiedScopes = [],
            ConfigurationState = configuredValues.Any(v => !string.IsNullOrWhiteSpace(v)) ? AltinnConfigurationState.Configured : AltinnConfigurationState.NeedsConfiguration };
        var package = Recalculate(request with { Flow = Unverified(request.Flow) with { EnvironmentId = environmentId, TargetEnvironmentReference = environmentId, UpdatedAt = _clock.GetUtcNow() },
            AltinnConfiguration = config });
        var json = JsonSerializer.Serialize(package, Json);
        if (json.Length > 1_000_000) return (null, "Message flow configuration exceeds the 1 MB limit.");
        if (ContainsCredentialLikeValue(json)) return (null, "Credential-like or raw personal-identifier-like values are not accepted. Store references only; do not enter tokens, passwords, private keys, connection strings or personal identifiers.");

        var row = await db.IntegrationMessageFlows.SingleOrDefaultAsync(r => r.EnvironmentId == environmentId, ct);
        if (row is null) db.IntegrationMessageFlows.Add(new IntegrationMessageFlowRecord { EnvironmentId = environmentId, DocumentJson = json, UpdatedAt = _clock.GetUtcNow() });
        else { row.DocumentJson = json; row.UpdatedAt = _clock.GetUtcNow(); }
        await db.SaveChangesAsync(ct);
        return (package, null);
    }

    private static IntegrationMessageFlowPackage Recalculate(IntegrationMessageFlowPackage package) => package with
    {
        Readiness = IntegrationMessageFlowReadiness.Evaluate(package.Flow, package.AltinnConfiguration),
        ManualTestPlan = IntegrationMessageFlowReadiness.ManualPlan(package.Flow, package.AltinnConfiguration),
        TestCases = package.TestCases.Count > 0 ? package.TestCases : MessageFlowTestCaseDesigner.Generate(package.Flow),
    };

    private static MessageFlowDefinition Unverified(MessageFlowDefinition flow)
    {
        static FlowEvidenceProvenance Clamp(FlowEvidenceProvenance e) => e.State is FlowEvidenceState.SourceConfirmed or FlowEvidenceState.Configured
            or FlowEvidenceState.AzureObserved or FlowEvidenceState.RuntimeObserved or FlowEvidenceState.AssertionPassed or FlowEvidenceState.AssertionFailed
                ? e with { State = FlowEvidenceState.NotAssessed, Note = "Manual configuration cannot assert source/runtime verification or test results." }
                : e;
        return flow with
        {
            Nodes = flow.Nodes.Select(n => n with { Evidence = Clamp(n.Evidence) }).ToList(),
            Hops = flow.Hops.Select(h => h with { Evidence = Clamp(h.Evidence) }).ToList(),
            Lifecycle = flow.Lifecycle.Select(s => s with { Evidence = Clamp(s.Evidence) }).ToList(),
            ErrorPaths = flow.ErrorPaths.Select(e => e with { Evidence = Clamp(e.Evidence) }).ToList(),
            Validation = flow.Validation.Select(v => v with { Evidence = Clamp(v.Evidence) }).ToList(),
            Receipts = flow.Receipts.Select(r => r with { Evidence = Clamp(r.Evidence) }).ToList(),
            DataHandling = flow.DataHandling is { } d ? d with { Evidence = Clamp(d.Evidence) } : null,
            Deadlines = flow.Deadlines.Select(d => d with { Evidence = Clamp(d.Evidence) }).ToList(),
            Checkpoints = flow.Checkpoints.Select(c => c with { RuntimeStatus = FlowCheckpointStatus.NotAssessed }).ToList(),
        };
    }

    private static bool ContainsCredentialLikeValue(string json) =>
        Regex.IsMatch(json, "(?i)(password|passwd|client[_-]?secret|access[_-]?token|refresh[_-]?token|private[_-]?key|connectionstring|sas[_-]?token)\\s*[:=]") ||
        Regex.IsMatch(json, "(?i)https?://[^/\\s:@]+:[^/\\s@]+@") ||
        Regex.IsMatch(json, "(?i)https?://[^\\s\"']+[?&](sig|token|code|key|password)=") ||
        Regex.IsMatch(json, "(?<!\\d)\\d{11}(?!\\d)");
}
