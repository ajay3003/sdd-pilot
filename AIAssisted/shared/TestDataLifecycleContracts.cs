using System.Text.Json.Serialization;
using BirkNext.ContractSchemaEvidence;
using BirkNext.DataModelEvidence;

namespace BirkNext.TestData;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// Shared reusable Test Data lifecycle. Candidate providers (contract evidence, data model evidence, Schema Studio generation, imported
// fixtures, manual entries) propose structure — never values for identities, never secrets — and a candidate is never approved test data:
// approval is a separate, explicit step owned by the Test Data catalog. Approved assets are versioned by content fingerprint; executions keep
// the exact version they used; environment-scoped assets are never reused elsewhere; a source change marks affected assets for review and
// structural revalidation — it never edits them. Expected results stay apart from actual results.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestDataLifecycleState { Candidate, Generated, NeedsReview, Approved, Registered, InUse, ReviewRequired, PotentiallyStale, Deprecated, Archived }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestDataCandidateOrigin { ContractEvidence, DataModelEvidence, SchemaStudio, ApiExample, ImportedFixture, Manual, EnvironmentReference }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestDataIntent { Positive, Negative, ExpectedResult }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestDataReadinessState { Ready, CandidateOnly, Missing, Stale, EnvironmentMismatch }

/// <summary>A proposed asset: structure and rules with provenance. Never approved by creation; never contains identities or secrets.</summary>
public sealed record TestDataCandidate(string Id, TestDataCandidateOrigin Origin, string Label, string MediaType, TestDataIntent Intent, string? Environment,
    IReadOnlyList<string> Structure, IReadOnlyList<string> Rules, string DerivedFrom, string? ExpectedOutcome, TestDataLifecycleState State = TestDataLifecycleState.Candidate);

/// <summary>Which asset version an execution used (alias + content fingerprint) — kept with the run; never a value.</summary>
public sealed record TestDataUsage(string Alias, string? Fingerprint, string? Environment, string Source);

public sealed record TestDataReadiness(string Role, TestDataReadinessState State, string Text);

public sealed record TestDataImpact(string Alias, TestDataLifecycleState State, string Reason);

public static class TestDataLifecycle
{
    public const string ControlledReference = "<controlled identity reference — never generated>";

    /// <summary>Structural candidates from a contract's normalized constraints: one valid case and, per required field, a negative
    /// "missing required field" case with its expected rejection. Identity-like fields get a controlled reference placeholder.</summary>
    public static IReadOnlyList<TestDataCandidate> FromContract(ContractFact fact)
    {
        var constraints = ContractEvidenceQueries.GetConstraints(fact);
        if (constraints.Count == 0 && fact.EntryPoint.State != ContractEntryPointState.SingleMessageRoot) return [];
        var media = fact.Contract.Type == BirkNext.SourceDomains.SourceContractType.XmlSchema ? "application/xml" : "application/json";
        var fields = ContractEvidenceQueries.GetFields(fact);
        var structure = fields.Take(60).Select(f => $"{f.Path}{(IsIdentity(f.Name) ? $" = {ControlledReference}" : "")}").ToList();
        var rules = constraints.Select(c => $"{c.Path}: {c.PlainText}").Take(60).ToList();
        var result = new List<TestDataCandidate>
        {
            new($"contract:{fact.Id}:valid", TestDataCandidateOrigin.ContractEvidence, $"{fact.Name} — valid structure", media, TestDataIntent.Positive, null, structure, rules,
                $"{fact.Contract.File} (snapshot {fact.Provenance.SnapshotId:D})", "Accepted"),
        };
        foreach (var required in constraints.Where(c => c.Kind == "Required").Take(10))
            result.Add(new($"contract:{fact.Id}:missing:{required.Path}", TestDataCandidateOrigin.ContractEvidence, $"{fact.Name} — missing {required.Path}", media, TestDataIntent.Negative, null,
                [.. structure.Where(s => !s.StartsWith(required.Path, StringComparison.Ordinal))], [$"{required.Path}: omitted on purpose"],
                $"{fact.Contract.File} (snapshot {fact.Provenance.SnapshotId:D})", "Rejected (schema validation) — a rejection is the expected, passing outcome"));
        return result;
    }

    /// <summary>A structural candidate per data-model entity (columns + constraints), without values.</summary>
    public static IReadOnlyList<TestDataCandidate> FromDataModel(IEnumerable<DataEntity> entities) =>
        [.. entities.Where(e => e.Fields.Count > 0).Select(e => new TestDataCandidate($"data:{e.Id}", TestDataCandidateOrigin.DataModelEvidence, $"{e.Entity ?? e.Table} — row structure",
            "application/json", TestDataIntent.Positive, null, [.. e.Fields.Select(f => $"{f.Property ?? f.Column}{(IsIdentity(f.Column) ? $" = {ControlledReference}" : "")}")],
            DataModelQueries.Constraints(e), $"{e.Store}/{e.Table} ({e.Mapping})", null))];

    /// <summary>Identity-like fields (national identity numbers, D-numbers, personal ids): never generated, only controlled references.</summary>
    public static bool IsIdentity(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("fodselsnummer") || n.Contains("fødselsnummer") || n.Contains("personnummer") || n.Contains("dnummer") || n.Contains("d-nummer") || n.Contains("ssn")
            || n.Contains("nationalid") || n.Contains("personident") || n is "fnr" or "pid";
    }

    /// <summary>An environment-scoped asset is usable only in its environment; an unscoped asset is usable anywhere only when allowed. A null
    /// environment means the consumer has none selected: nothing is reused across environments, so only the asset's own scope matters.</summary>
    public static bool UsableIn(TestDataAssetSummary asset, string? environment, bool allowEnvironmentAgnostic) =>
        asset.Environment is null ? allowEnvironmentAgnostic : environment is null || string.Equals(asset.Environment, environment, StringComparison.OrdinalIgnoreCase);

    /// <summary>The version an execution used: alias + content fingerprint (the asset's identity at run time).</summary>
    public static TestDataUsage Usage(TestDataAssetSummary asset) => new(asset.Alias, asset.Fingerprint, asset.Environment, asset.Source);

    public static TestDataLifecycleState State(TestDataAssetSummary asset) => asset.Approval switch
    {
        TestDataApproval.Approved when asset.Validation == TestDataValidation.Invalid && !asset.Capabilities.Contains("negative-test") => TestDataLifecycleState.ReviewRequired,
        TestDataApproval.Approved => TestDataLifecycleState.Approved,
        TestDataApproval.Expired => TestDataLifecycleState.Deprecated,
        TestDataApproval.Disabled => TestDataLifecycleState.Archived,
        _ => TestDataLifecycleState.NeedsReview,
    };

    /// <summary>Readiness of one role for one environment: approved and usable → Ready; only candidates → Candidate only; an asset scoped to
    /// another environment → Environment mismatch (never silently reused); a schema-stale asset → Stale; none → Missing.</summary>
    public static TestDataReadiness Readiness(string role, IEnumerable<TestDataAssetSummary> assets, int candidates, string? environment)
    {
        var list = assets.ToList();
        if (list.Any(a => a.Approval == TestDataApproval.Approved && UsableIn(a, environment, true) && a.SchemaCheck?.Status is not TestDataSchemaStatus.SchemaUnavailable))
            return new(role, TestDataReadinessState.Ready, "Approved test data available for this environment.");
        if (list.Any(a => a.Approval == TestDataApproval.Approved && a.SchemaCheck?.Status == TestDataSchemaStatus.SchemaUnavailable))
            return new(role, TestDataReadinessState.Stale, "Approved asset's schema is no longer available — review and revalidate.");
        if (list.Any(a => a.Approval == TestDataApproval.Approved && !UsableIn(a, environment, true)))
            return new(role, TestDataReadinessState.EnvironmentMismatch, $"Approved assets exist for another environment only — they are never reused in {environment}.");
        return candidates > 0 ? new(role, TestDataReadinessState.CandidateOnly, $"{candidates} candidate(s) — review and approve before use.")
            : new(role, TestDataReadinessState.Missing, "No test data for this role.");
    }

    /// <summary>Assets affected by a change (schema path, contract fingerprint, entity, environment): marked for review / potentially stale —
    /// never modified automatically. Structural revalidation is the next step.</summary>
    public static IReadOnlyList<TestDataImpact> Affected(IEnumerable<TestDataAssetSummary> assets, IEnumerable<string> changedContractFiles, IEnumerable<string> changedEntities,
        IEnumerable<string> changedEnvironments)
    {
        var files = changedContractFiles.Select(f => f.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var entities = changedEntities.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var envs = changedEnvironments.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<TestDataImpact>();
        foreach (var a in assets)
        {
            if (a.Schema is { } s && files.Any(f => f.EndsWith(s.SchemaPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)))
                result.Add(new(a.Alias, TestDataLifecycleState.ReviewRequired, $"Its schema {s.SchemaPath} changed — review and revalidate structurally; the asset is not modified."));
            else if (a.EntityType is { } entity && entities.Contains(entity))
                result.Add(new(a.Alias, TestDataLifecycleState.PotentiallyStale, $"Data model entity {entity} changed — review; the asset is not modified."));
            else if (a.Environment is { } env && envs.Contains(env))
                result.Add(new(a.Alias, TestDataLifecycleState.PotentiallyStale, $"Environment {env} changed — review the environment-scoped asset."));
        }
        return result;
    }

    /// <summary>Safe metadata for documentation: aliases, kinds, intent, environment, approval, version — never values.</summary>
    public static string Documentation(IEnumerable<TestDataAssetSummary> assets)
    {
        var lines = new List<string> { "## Test data", "", "Metadata only — no values, identities or secrets.", "" };
        foreach (var a in assets.OrderBy(a => a.Alias, StringComparer.Ordinal))
            lines.Add($"- `{a.Alias}` · {a.Kind} · {(a.Capabilities.Contains("negative-test") ? "negative" : "positive")} · {a.Environment ?? "any environment"} · {State(a)} · version {(a.Fingerprint is { Length: > 12 } f ? f[..12] : a.Fingerprint ?? "n/a")}");
        return string.Join("\n", lines) + "\n";
    }
}
