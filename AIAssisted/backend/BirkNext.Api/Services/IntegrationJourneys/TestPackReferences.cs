using BirkNext.Integrations;

namespace BirkNext.Api.Services.IntegrationJourneys;

/// <summary>
/// Reference metadata a test pack ships with BirkNext for one of its logical sources (provider <see cref="TestPackSourceProvider.TestPackReference"/>),
/// e.g. the facts of an application's OpenAPI document. <see cref="Load"/> returns the reference; a failure makes the source "reference
/// unavailable" — never "missing source package". A reference supports configuration and test planning only: it is never a Source Analysis
/// snapshot, never an IQR binding and never runtime truth.
/// </summary>
public sealed record TestPackReference(string RoleKey, string Kind, string Title, Func<TestPackReferenceDocument> Load)
{
    /// <summary>Which application the facts describe (matched against a configuration requirement's scope hint, like a source value's scope).</summary>
    public string? Scope { get; init; }
    public string Limitation { get; init; } = "Reference data supports configuration and test planning. It is not canonical source evidence and does not verify the deployed application.";
    public IReadOnlyList<string> NotProven { get; init; } = [];
}

/// <summary>A loaded reference: its facts, version provenance and the fingerprint of the document the facts were taken from.</summary>
public sealed record TestPackReferenceDocument(IReadOnlyList<TestPackReferenceFact> Facts)
{
    public IReadOnlyList<string> Provenance { get; init; } = [];
    public string? Fingerprint { get; init; }
}

public static class TestPackReferences
{
    public const string ProvenanceLabel = "Test Pack reference";
    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

    private sealed record Document(List<TestPackReferenceFact>? Facts, List<string>? Provenance, string? Fingerprint);

    /// <summary>Reads a reference document embedded in this assembly ("TestPackReference.{file}"): facts, provenance and fingerprint. A missing
    /// or unreadable resource throws, which makes the source "reference unavailable" with the reason.</summary>
    public static TestPackReferenceDocument Embedded(string fileName)
    {
        var name = $"TestPackReference.{fileName}";
        using var stream = typeof(TestPackReferences).Assembly.GetManifestResourceStream(name)
            ?? throw new FileNotFoundException($"The built-in reference {fileName} is not part of this build.");
        var document = System.Text.Json.JsonSerializer.Deserialize<Document>(stream, Json) ?? throw new InvalidDataException($"The built-in reference {fileName} is empty.");
        return new TestPackReferenceDocument(document.Facts ?? []) { Provenance = document.Provenance ?? [], Fingerprint = document.Fingerprint };
    }

    public static TestPackReference? For(IIntegrationJourneyPack pack, TestPackSourceRole role) =>
        pack.References.FirstOrDefault(reference => string.Equals(reference.RoleKey, role.Key, StringComparison.Ordinal));

    /// <summary>Loads and validates a reference: at least one fact, every fact a non-empty safe reference (no credential-shaped value).
    /// Anything else is "unavailable" with the reason; nothing is retried, fetched or substituted.</summary>
    public static (TestPackReferenceView View, bool Loaded) Load(TestPackReference reference)
    {
        var view = new TestPackReferenceView(reference.Kind, reference.Title) { Limitation = reference.Limitation, NotProven = reference.NotProven };
        TestPackReferenceDocument document;
        try { document = reference.Load(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (view with { Diagnostics = [$"The reference could not be loaded ({ex.GetType().Name}: {ex.Message})."] }, false);
        }
        var problems = new List<string>();
        if (document.Facts.Count == 0) problems.Add("The reference declares no facts.");
        foreach (var fact in document.Facts)
        {
            if (string.IsNullOrWhiteSpace(fact.Label) || string.IsNullOrWhiteSpace(fact.Value)) problems.Add("A reference fact has no label or value.");
            else if (Integrations.MessageFlowCredentialGuard.Reason(fact.Value) is { } reason) problems.Add($"{fact.Label} is not a safe reference value: {reason}");
        }
        return problems.Count > 0
            ? (view with { Diagnostics = [.. problems.Distinct()] }, false)
            : (view with { Facts = document.Facts, Provenance = document.Provenance, Fingerprint = document.Fingerprint }, true);
    }

    /// <summary>The state of a reference-provided source. Imported snapshots recognized as the same source stay listed as additional imported
    /// evidence (candidates) and never replace the reference; nothing is bound, created or verified.</summary>
    public static TestPackSourceStatus Status(IIntegrationJourneyPack pack, TestPackSourceRole role, IReadOnlyList<TestPackSourceCandidate> imported)
    {
        if (For(pack, role) is not { } reference)
            return new(role, TestPackSourceState.ReferenceUnavailable)
            {
                Candidates = imported, Reference = new TestPackReferenceView("Reference", role.Label) { Diagnostics = ["The test pack declares this source as a Test Pack reference but provides none."] },
                Detail = $"{role.Label}: Test Pack reference unavailable.",
            };
        var (view, loaded) = Load(reference);
        return loaded
            ? new(role, TestPackSourceState.ReferenceAvailable)
            {
                Candidates = imported, Reference = view,
                Detail = $"{role.Label} is provided by the Test Pack reference ({reference.Kind}, built into BirkNext). No separate import is required.",
            }
            : new(role, TestPackSourceState.ReferenceUnavailable)
            {
                Candidates = imported, Reference = view,
                Detail = $"{role.Label}: the Test Pack reference could not be loaded. {string.Join(" ", view.Diagnostics)}",
            };
    }

    /// <summary>The values a pack's loaded references declare for one semantic key within a scope hint, each with its provenance (artifact
    /// "Test Pack reference", location = the fact's label) so every consumer shows where it came from.</summary>
    public static IReadOnlyList<SourceDomains.SourceConfigurationValue> Values(IIntegrationJourneyPack pack, IEnumerable<TestPackSourceRole> roles, string semanticKey, string? scopeHint)
    {
        var values = new List<SourceDomains.SourceConfigurationValue>();
        foreach (var role in roles.Where(item => item.Provider == TestPackSourceProvider.TestPackReference))
        {
            if (For(pack, role) is not { } reference || !SourceAnalysis.SourceConfigurationGuidance.InScope(reference.Scope, scopeHint)) continue;
            var (view, loaded) = Load(reference);
            if (!loaded) continue;
            values.AddRange(view.Facts.Where(fact => fact.SemanticKey == semanticKey).Select(fact => new SourceDomains.SourceConfigurationValue
            {
                SemanticKey = semanticKey, Value = fact.Value, Scope = reference.Scope, LogicalSource = role.Label, Artifact = ProvenanceLabel, Location = fact.Label,
                EvidenceKind = reference.Kind == "OpenAPI" ? SourceDomains.SourceConfigurationEvidenceKind.OpenApi : SourceDomains.SourceConfigurationEvidenceKind.Other,
                Basis = reference.Title,
            }));
        }
        return values;
    }
}
