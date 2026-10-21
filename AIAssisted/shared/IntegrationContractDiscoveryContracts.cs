using System.Text.Json.Serialization;

namespace BirkNext.Integrations;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// Integration contract discovery: producer and consumer contracts resolved separately from the integration's CANONICAL source
// binding (never the newest snapshot, never a global scan), ranked by what the evidence is — a validator/handler reference beats
// a component association, which beats declared metadata, a source relationship, a template hint and, last, a file-name
// similarity that is never authoritative. Library schemas support an entry schema; they are never its primary contract.
// Contract availability is not compatibility: compatibility is its own state.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntegrationContractType { Xsd, JsonSchema, OpenApi, AsyncApi, Wsdl, Protobuf, GraphQlSchema, Other }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntegrationContractCandidateState { Resolved, Candidate, NeedsConfirmation, Ambiguous, Unavailable, ParseFailed, NotApplicable }

/// <summary>Evidence strength, strongest first. <see cref="FilenameSimilarity"/> alone never resolves a contract.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntegrationContractEvidenceRank
{
    HandlerReference = 1, ComponentAssociation = 2, ContractMetadata = 3, SourceRelationship = 4, TemplateHint = 5, FilenameSimilarity = 6,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntegrationContractCompatibilityState { NotAssessable, ReadyToAssess, Compatible, DifferencesFound }

/// <summary>Which side of the integration a bound source snapshot holds. Unknown: candidates are listed for both sides and need confirmation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntegrationSourceSide { Unknown, Producer, Consumer }

/// <summary>Where the contract currently selected for a side comes from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntegrationContractOrigin { ManualUpload, SourceSelection }

/// <summary>One piece of evidence behind a candidate ("Referenced by validator code", file and line).</summary>
public sealed record IntegrationContractEvidenceItem(IntegrationContractEvidenceRank Rank, string Text, string? File = null, int? Line = null);

/// <summary>A source contract that may be one side's contract, with every reason it was considered.</summary>
public sealed record IntegrationContractCandidate
{
    public string ContractId { get; init; } = "";
    public string Name { get; init; } = "";
    public string File { get; init; } = "";
    public IntegrationContractType Type { get; init; }
    public string? Version { get; init; }
    public string? TargetNamespace { get; init; }
    /// <summary>False for a library/supporting schema (declares no message entry element).</summary>
    public bool IsEntry { get; init; } = true;
    /// <summary>A copy in a test project: evidence of what tests use, never the deployed contract by itself.</summary>
    public bool TestCopy { get; init; }
    public IntegrationContractEvidenceRank Rank { get; init; } = IntegrationContractEvidenceRank.FilenameSimilarity;
    public List<IntegrationContractEvidenceItem> Evidence { get; init; } = [];
    /// <summary>Library schemas this entry schema imports or includes (supporting, never primary).</summary>
    public List<string> SupportingSchemas { get; init; } = [];
    public bool ParseFailed { get; init; }
    /// <summary>True when the snapshot retained the content, so the contract can be selected and compared.</summary>
    public bool ContentAvailable { get; init; }
}

/// <summary>The contract currently stored for a side, with its origin. A source selection pins the snapshot it was chosen from.</summary>
public sealed record IntegrationContractSelection
{
    public IntegrationContractOrigin Origin { get; init; }
    public string FileName { get; init; } = "";
    public string Format { get; init; } = "";
    public string ShortHash { get; init; } = "";
    public string? ContractId { get; init; }
    public Guid? SourceSnapshotId { get; init; }
    public string? SourceArchive { get; init; }
    /// <summary>A source selection made against another snapshot than the current binding: it needs confirmation again.</summary>
    public bool Stale { get; init; }
}

/// <summary>One side (producer or consumer) resolved on its own.</summary>
public sealed record IntegrationContractSide
{
    public IntegrationContractRole Role { get; init; }
    /// <summary>The configured producer/consumer this side belongs to (display only).</summary>
    public string? Party { get; init; }
    public IntegrationContractCandidateState State { get; init; } = IntegrationContractCandidateState.Unavailable;
    /// <summary>The candidate the evidence points to (Resolved / Candidate), or null when none or several.</summary>
    public IntegrationContractCandidate? Primary { get; init; }
    /// <summary>Entry-schema candidates, strongest first. Several at the same rank: none is chosen.</summary>
    public List<IntegrationContractCandidate> Candidates { get; init; } = [];
    /// <summary>Library schemas found for this side's candidates: supporting only.</summary>
    public List<IntegrationContractCandidate> Supporting { get; init; } = [];
    public IntegrationContractSelection? Selected { get; init; }
    public string Explanation { get; init; } = "";
    /// <summary>Reference metadata about this side's contract (e.g. a declared data type) — a hint, never the contract itself.</summary>
    public List<string> ReferenceHints { get; init; } = [];
}

/// <summary>Contract discovery for one integration. Read-only: nothing is selected, bound or stored by discovering.</summary>
public sealed record IntegrationContractDiscovery
{
    public string IntegrationId { get; init; } = "";
    public bool SourceBound { get; init; }
    public Guid? SnapshotId { get; init; }
    public string? Archive { get; init; }
    public IntegrationSourceSide SourceSide { get; init; }
    public string SourceSideBasis { get; init; } = "";
    public IntegrationContractSide Producer { get; init; } = new() { Role = IntegrationContractRole.Producer };
    public IntegrationContractSide Consumer { get; init; } = new() { Role = IntegrationContractRole.Consumer };
    public IntegrationContractCompatibilityState Compatibility { get; init; }
    public string CompatibilityReason { get; init; } = "";
    public List<string> CompatibilityDifferences { get; init; } = [];
    public List<string> Limitations { get; init; } = [];

    public IntegrationContractSide Side(IntegrationContractRole role) => role == IntegrationContractRole.Producer ? Producer : Consumer;
}

/// <summary>Use a discovered source contract for one side. Explicit and per side; replaces only that side's stored contract.</summary>
public sealed record IntegrationContractSourceSelectionRequest(string EnvironmentId, string IntegrationId, IntegrationContractRole Role, Guid SnapshotId, string ContractId);

public static class IntegrationContractLabels
{
    public static string Type(IntegrationContractType type) => type switch
    {
        IntegrationContractType.Xsd => "XSD",
        IntegrationContractType.JsonSchema => "JSON Schema",
        IntegrationContractType.OpenApi => "OpenAPI",
        IntegrationContractType.AsyncApi => "AsyncAPI",
        IntegrationContractType.Wsdl => "WSDL",
        IntegrationContractType.Protobuf => "Protobuf",
        IntegrationContractType.GraphQlSchema => "GraphQL schema",
        _ => "Other",
    };

    public static string State(IntegrationContractCandidateState state) => state switch
    {
        IntegrationContractCandidateState.Resolved => "Resolved",
        IntegrationContractCandidateState.Candidate => "Candidate found",
        IntegrationContractCandidateState.NeedsConfirmation => "Needs confirmation",
        IntegrationContractCandidateState.Ambiguous => "Ambiguous",
        IntegrationContractCandidateState.ParseFailed => "Parse failed",
        IntegrationContractCandidateState.NotApplicable => "Not applicable",
        _ => "Unavailable",
    };

    public static string Rank(IntegrationContractEvidenceRank rank) => rank switch
    {
        IntegrationContractEvidenceRank.HandlerReference => "Handler/validator reference",
        IntegrationContractEvidenceRank.ComponentAssociation => "Component association",
        IntegrationContractEvidenceRank.ContractMetadata => "Declared contract metadata",
        IntegrationContractEvidenceRank.SourceRelationship => "Source relationship",
        IntegrationContractEvidenceRank.TemplateHint => "Template hint",
        _ => "File-name similarity only",
    };

    public static string Compatibility(IntegrationContractCompatibilityState state) => state switch
    {
        IntegrationContractCompatibilityState.ReadyToAssess => "Ready to assess",
        IntegrationContractCompatibilityState.Compatible => "Compatible",
        IntegrationContractCompatibilityState.DifferencesFound => "Differences found",
        _ => "Not assessable",
    };

    public static string Origin(IntegrationContractOrigin origin) => origin == IntegrationContractOrigin.SourceSelection ? "Selected from source evidence" : "Manual upload";

    /// <summary>Whether a side has a usable contract (resolved by evidence or explicitly selected/uploaded and not stale).</summary>
    public static bool Usable(IntegrationContractSide side) =>
        side.Selected is { Stale: false } || side.State == IntegrationContractCandidateState.Resolved;
}
