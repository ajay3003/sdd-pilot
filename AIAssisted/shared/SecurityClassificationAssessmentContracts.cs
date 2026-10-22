using System.Text.Json.Serialization;

namespace BirkNext.Integrations;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// Security Classification as a shared, reusable capability. One stored review run (source scope + safe runtime observations) is projected
// onto generic security requirements; each consumer (M01 / M2LB, Skolenærvær, a future domain) reads that same projection and adds only its
// own domain requirements. Nothing is re-parsed per consumer and no consumer keeps its own source selection, test context or identities:
// identities are controlled Test Data references and are never generated. Source evidence ≠ runtime verification; a denied unauthorized call
// or an allowed authorized call proves that surface only, never "all authorization is correct"; source and runtime disagreeing is Review
// required; runtime results recorded under another test context are not current.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The shared requirements every consumer can reference.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SecurityRequirementKind
{
    ClassificationModel, ClassificationPropagation, AuthorizationSource, UnauthorizedRuntimeDenial, AuthorizedRuntimeAccess, TestContext, IdentityReferences,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SecurityRequirementState { Verified, SourceEvidence, Partial, IssueDetected, ReviewRequired, NotTested, NotCurrent, Missing, NotApplicable }

/// <summary>One requirement as a consumer sees it: the shared evidence, what runtime testing showed, its state and the owner of the fix.</summary>
public sealed record SecurityRequirementAssessment(string Key, string Title, SecurityRequirementState State, string SharedEvidence, string RuntimeTest, string Owner)
{
    /// <summary>A consumer's own rule (e.g. downstream protected-data handling), not part of the shared classification rules.</summary>
    public bool DomainSpecific { get; init; }
}

/// <summary>A domain requirement a consumer adds on top of the shared ones (never evaluated by the shared rules).</summary>
public sealed record SecurityDomainRequirement(string Key, string Title, string Detail, string Owner);

/// <summary>A consumer of the shared capability: which shared requirements matter to it and which domain requirements it adds.</summary>
public sealed record SecurityClassificationConsumerDefinition(string ConsumerId, string DisplayName, string Context, IReadOnlyList<SecurityRequirementKind> Shared,
    IReadOnlyList<SecurityDomainRequirement> Domain);

/// <summary>A consumer's assessment — different consumers can differ while referencing the same run.</summary>
public sealed record SecurityClassificationConsumerAssessment(string ConsumerId, string DisplayName, string Context, Guid? RunId, DateTimeOffset? AssessedAt,
    IReadOnlyList<SecurityRequirementAssessment> Requirements, string Summary);

public static class SecurityClassificationAssessments
{
    public const string ReviewOwner = "Security Classification review";
    public const string TestDataOwner = "Test Data";

    public static string Label(SecurityRequirementState state) => state switch
    {
        SecurityRequirementState.Verified => "Verified (tested surfaces)",
        SecurityRequirementState.SourceEvidence => "Source evidence",
        SecurityRequirementState.IssueDetected => "Issue detected",
        SecurityRequirementState.ReviewRequired => "Review required",
        SecurityRequirementState.NotTested => "Not tested",
        SecurityRequirementState.NotCurrent => "Not current",
        SecurityRequirementState.NotApplicable => "Not applicable",
        _ => state.ToString(),
    };

    private static readonly ClassificationState[] Bad = [ClassificationState.Fail, ClassificationState.IssueDetected];
    private static readonly ClassificationState[] Good = [ClassificationState.Pass, ClassificationState.SourceVerified, ClassificationState.Verified, ClassificationState.Configured];

    private static ClassificationState? Worst(ClassificationReviewResult run, params ClassificationArea[] areas)
    {
        var states = run.Summary.Where(r => areas.Contains(r.Area)).Select(r => r.State).ToList();
        if (states.Count == 0) return null;
        return states.FirstOrDefault(s => Bad.Contains(s)) is var bad && Bad.Contains(bad) ? bad
            : states.All(s => Good.Contains(s)) ? ClassificationState.SourceVerified : ClassificationState.Partial;
    }

    private static SecurityRequirementState FromSource(ClassificationState? state) => state switch
    {
        null => SecurityRequirementState.NotTested,
        ClassificationState.Fail or ClassificationState.IssueDetected => SecurityRequirementState.IssueDetected,
        ClassificationState.SourceVerified or ClassificationState.Pass or ClassificationState.Verified or ClassificationState.Configured => SecurityRequirementState.SourceEvidence,
        ClassificationState.NeedsDecision => SecurityRequirementState.ReviewRequired,
        _ => SecurityRequirementState.Partial,
    };

    /// <summary>Whether a run's runtime results still apply: recorded under the test context that is current now (environment, approval,
    /// identities). Without a current context nothing runtime is current.</summary>
    public static bool RuntimeCurrent(ClassificationReviewResult run, ClassificationContextSummary? current) =>
        run.ContextSummary is { } recorded && current is not null && recorded.Environment == current.Environment && recorded.Approved == current.Approved
        && recorded.UnauthorizedIdentityConfigured == current.UnauthorizedIdentityConfigured && recorded.AuthorizedIdentityConfigured == current.AuthorizedIdentityConfigured
        && recorded.ConfiguredLevels.SequenceEqual(current.ConfiguredLevels);

    /// <summary>The shared projection of one run (null: never reviewed). <paramref name="identityReferences"/> = approved controlled identity
    /// references in Test Data (null: Test Data unavailable).</summary>
    public static SecurityRequirementAssessment Shared(SecurityRequirementKind kind, ClassificationReviewResult? run, ClassificationContextSummary? currentContext, int? identityReferences)
    {
        const string notReviewed = "No Security Classification review has been recorded for this environment.";
        if (kind == SecurityRequirementKind.IdentityReferences)
            return new("identity-references", "Controlled Kode 6/7 identity references", identityReferences is null ? SecurityRequirementState.NotTested
                    : identityReferences > 0 ? SecurityRequirementState.SourceEvidence : SecurityRequirementState.Missing,
                identityReferences is null ? "Test Data is not available." : $"{identityReferences} approved controlled reference(s) in Test Data — never generated, environment-scoped.",
                "—", TestDataOwner);
        if (kind == SecurityRequirementKind.TestContext)
        {
            var context = currentContext;
            var ready = context is { Approved: true, EndpointConfigured: true } && (context.UnauthorizedIdentityConfigured || context.AuthorizedIdentityConfigured);
            return new("test-context", "Approved security test context", ready ? SecurityRequirementState.SourceEvidence : SecurityRequirementState.Missing,
                ready ? $"Approved {context!.Environment} context (one canonical, temporary context; values never shown)." : "No approved test context — a prerequisite, not a security defect.",
                "—", ReviewOwner);
        }
        if (run is null) return new(Key(kind), Title(kind), SecurityRequirementState.NotTested, notReviewed, "Not tested", ReviewOwner);

        var current = RuntimeCurrent(run, currentContext);
        SecurityRequirementAssessment Runtime(ClassificationIdentity identity, string title, string key)
        {
            var observations = run.Live.Observations.Where(o => o.Identity == identity).ToList();
            var source = Worst(run, ClassificationArea.DirectAccess, ClassificationArea.Search, ClassificationArea.GraphQL, ClassificationArea.ChildAccess);
            if (observations.Count == 0)
                return new(key, title, SecurityRequirementState.NotTested, $"Source: {(source is null ? "not assessed" : FromSource(source).ToString())}", "Not tested at runtime", ReviewOwner);
            var surfaces = string.Join(", ", observations.Select(o => $"{o.Surface} (level {o.Nivaa})").Distinct());
            var failed = observations.Any(o => Bad.Contains(o.State));
            var passed = observations.All(o => o.State is ClassificationState.Pass or ClassificationState.Verified);
            var state = !current ? SecurityRequirementState.NotCurrent
                : failed ? SecurityRequirementState.IssueDetected
                : passed && source is { } s && Bad.Contains(s) ? SecurityRequirementState.ReviewRequired
                : passed ? SecurityRequirementState.Verified : SecurityRequirementState.Partial;
            var runtime = $"{observations.Count} observation(s) on {surfaces} — proves these surfaces only, never all authorization."
                + (!current ? " Recorded under another test context: not current." : "")
                + (state == SecurityRequirementState.ReviewRequired ? " Source evidence and runtime disagree." : "");
            return new(key, title, state, $"Source: {(source is null ? "not assessed" : FromSource(source).ToString())}", runtime, ReviewOwner);
        }
        return kind switch
        {
            SecurityRequirementKind.ClassificationModel => new(Key(kind), Title(kind), FromSource(Worst(run, ClassificationArea.Model)),
                run.Levels.Count > 0 ? $"{run.Levels.Count} classification level(s) in source." : "No classification levels found in source.", "— (source only)", ReviewOwner),
            SecurityRequirementKind.ClassificationPropagation => new(Key(kind), Title(kind), FromSource(Worst(run, ClassificationArea.Pipeline, ClassificationArea.Guard)),
                $"{run.Pipeline.Count} propagation stage(s) assessed from source.", "Not verified at runtime (source evidence ≠ runtime propagation).", ReviewOwner),
            SecurityRequirementKind.AuthorizationSource => new(Key(kind), Title(kind), FromSource(Worst(run, ClassificationArea.DirectAccess, ClassificationArea.Search, ClassificationArea.GraphQL, ClassificationArea.ChildAccess)),
                $"{run.Findings.Count} finding(s) in the reviewed source scope.", "— (source ≠ runtime enforcement)", ReviewOwner),
            SecurityRequirementKind.UnauthorizedRuntimeDenial => Runtime(ClassificationIdentity.Unauthorized, Title(kind), Key(kind)),
            SecurityRequirementKind.AuthorizedRuntimeAccess => Runtime(ClassificationIdentity.Authorized, Title(kind), Key(kind)),
            _ => new(Key(kind), Title(kind), SecurityRequirementState.NotApplicable, "", "", ReviewOwner),
        };
    }

    public static string Key(SecurityRequirementKind kind) => kind switch
    {
        SecurityRequirementKind.ClassificationModel => "classification-model",
        SecurityRequirementKind.ClassificationPropagation => "classification-propagation",
        SecurityRequirementKind.AuthorizationSource => "authorization-source",
        SecurityRequirementKind.UnauthorizedRuntimeDenial => "unauthorized-access",
        SecurityRequirementKind.AuthorizedRuntimeAccess => "authorized-graded-access",
        SecurityRequirementKind.TestContext => "test-context",
        _ => "identity-references",
    };

    public static string Title(SecurityRequirementKind kind) => kind switch
    {
        SecurityRequirementKind.ClassificationModel => "Classification model",
        SecurityRequirementKind.ClassificationPropagation => "Classification propagation",
        SecurityRequirementKind.AuthorizationSource => "Authorization protection in source",
        SecurityRequirementKind.UnauthorizedRuntimeDenial => "Unauthorized access denied",
        SecurityRequirementKind.AuthorizedRuntimeAccess => "Authorized graded access",
        SecurityRequirementKind.TestContext => "Approved security test context",
        _ => "Controlled Kode 6/7 identity references",
    };

    /// <summary>The classification rules that changed between two snapshots' stored classification evidence: levels (value, graded-access
    /// flag) and facts (id → state). Line moves are not changes. Empty when neither snapshot has classification evidence.</summary>
    public static IReadOnlyList<string> ChangedRules(ClassificationSnapshotEvidence? before, ClassificationSnapshotEvidence? after)
    {
        if (before is null && after is null) return [];
        var changes = new List<string>();
        var levelsBefore = (before?.Levels ?? []).ToDictionary(l => l.Nivaa, l => (l.Verdi, l.KreverGradertTilgang));
        var levelsAfter = (after?.Levels ?? []).ToDictionary(l => l.Nivaa, l => (l.Verdi, l.KreverGradertTilgang));
        foreach (var level in levelsBefore.Keys.Union(levelsAfter.Keys).Order())
            if (!levelsBefore.TryGetValue(level, out var b) || !levelsAfter.TryGetValue(level, out var a) || b != a) changes.Add($"Classification level {level}");
        var factsBefore = (before?.Facts ?? []).GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First());
        var factsAfter = (after?.Facts ?? []).GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var id in factsBefore.Keys.Union(factsAfter.Keys).Order(StringComparer.Ordinal))
            if (!factsBefore.TryGetValue(id, out var b) || !factsAfter.TryGetValue(id, out var a) || b.State != a.State)
                changes.Add((factsAfter.GetValueOrDefault(id) ?? factsBefore[id]).Title);
        return changes;
    }

    /// <summary>One consumer's assessment over the shared projection plus its own domain requirements (never verified by shared evidence).</summary>
    public static SecurityClassificationConsumerAssessment For(SecurityClassificationConsumerDefinition consumer, ClassificationReviewResult? run,
        ClassificationContextSummary? currentContext, int? identityReferences)
    {
        var requirements = consumer.Shared.Select(kind => Shared(kind, run, currentContext, identityReferences))
            .Concat(consumer.Domain.Select(d => new SecurityRequirementAssessment(d.Key, d.Title, SecurityRequirementState.NotTested, d.Detail, "No runtime evidence for this domain rule yet.", d.Owner) { DomainSpecific = true }))
            .ToList();
        var open = requirements.Count(r => r.State is not (SecurityRequirementState.Verified or SecurityRequirementState.SourceEvidence or SecurityRequirementState.NotApplicable));
        var issues = requirements.Count(r => r.State == SecurityRequirementState.IssueDetected);
        return new(consumer.ConsumerId, consumer.DisplayName, consumer.Context, run?.RunId, run?.CompletedAt, requirements,
            issues > 0 ? $"{issues} issue(s) detected · {open} requirement(s) not verified." : open == 0 ? "Every requirement has evidence." : $"{open} of {requirements.Count} requirement(s) not verified yet.");
    }
}
