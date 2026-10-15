using System.Text.Json.Serialization;

namespace BirkNext.SourceDomains;

// Contract Evolution: a general capability. A contract of any supported type is normalized into a snapshot of nodes, fingerprinted,
// compared with a baseline, classified per direction (producer / consumer) and walked through a data-driven graph of boundaries. Nothing
// here knows a project, integration or business meaning; domain rules come in through a semantic overlay.

/// <summary>Compatibility, in increasing severity. <see cref="NotEvaluated"/> is never read as compatible.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContractCompatibility { Compatible, NotEvaluated, NeedsReview, Breaking }

/// <summary>Which side of a contract a party is on: it writes documents of the contract, or it reads them.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContractPerspective { Producer, Consumer }

/// <summary>What a boundary does with a contract: produce it, carry it unread, consume it, or map it to another contract.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContractBoundaryRole { Produces, Transports, Consumes, Maps }

/// <summary>How a consumer treats members it does not know. <see cref="Unknown"/> means there is no evidence either way.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContractUnknownMemberPolicy { Unknown, Ignore, Reject }

/// <summary>A contract's identity. Never derived from a file name alone: the id is the stable key, the fingerprint the content.</summary>
public sealed record ContractDescriptor(string ContractId, SourceContractType Type, string Name, string? Version, string Source,
    string? Producer = null, string? Environment = null, string? Location = null);

/// <summary>
/// One normalized member of a contract: an element, attribute, property, parameter or operation. Paths use "/" separators.
/// <see cref="Position"/> is the declaration order among siblings, which matters for ordered formats (an XSD sequence).
/// </summary>
public sealed record ContractNode(string Path, string Kind, string? DataType, bool Required, string Cardinality, List<string> EnumValues,
    string? Namespace, int Position, List<string>? Restrictions = null, string? DeclaredIn = null);

/// <summary>One schema file a structured contract was built from: provenance only (its structure is already in the nodes).</summary>
public sealed record ContractSchemaDependency(string File, string Kind, string? Namespace, string? Version, string Fingerprint, string? ReferencedBy, string Status);

/// <summary>A normalized contract at one moment. <see cref="Fingerprint"/> covers the normalized form only, so formatting does not change it.</summary>
public sealed record ContractSnapshot(ContractDescriptor Descriptor, string? Namespace, List<ContractNode> Nodes, string Fingerprint,
    DateTimeOffset CapturedAt, string Evidence, List<ContractSchemaDependency>? Dependencies = null);

/// <summary>One structural difference between two snapshots. A removal plus an addition is never guessed to be a rename.</summary>
public sealed record ContractChange(ContractChangeClass Change, string Path, string? OldValue, string? NewValue, string Detail, string? DeclaredIn = null);

/// <summary>A domain rule's verdict on one change (for example "business code list changed: needs review").</summary>
public sealed record ContractSemanticAssessment(ContractCompatibility Compatibility, string Reason);

/// <summary>One change with its structural compatibility for each direction and the domain overlay's semantic verdict.</summary>
public sealed record ContractChangeAssessment(ContractChange Change, ContractCompatibility ProducerStructural, ContractCompatibility ConsumerStructural,
    ContractCompatibility Semantic, string SemanticReason);

/// <summary>
/// One step of a contract dependency graph: a component that produces, transports, consumes or maps a contract. Graphs are data.
/// <see cref="Paths"/>: the paths the party writes (producer) or reads (consumer); null when unknown. <see cref="Mapping"/>: input path →
/// output paths for a mapping; null when unknown. <see cref="SupportedVersions"/>: null when no version support is declared.
/// </summary>
public sealed record ContractBoundary
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public string FromComponent { get; init; } = "";
    public string ToComponent { get; init; } = "";
    public ContractBoundaryRole Role { get; init; }
    public string ContractId { get; init; } = "";
    public string? OutputContractId { get; init; }
    public List<string>? Paths { get; init; }
    public Dictionary<string, List<string>>? Mapping { get; init; }
    /// <summary>The mapping copies each mapped value unchanged (declared, not inferred): a change to a mapped source then IS a change to its
    /// target paths, so downstream consumers are evaluated on it. Otherwise impact beyond the mapping stays potential.</summary>
    public bool PassThrough { get; init; }
    public ContractUnknownMemberPolicy UnknownMembers { get; init; }
    public List<string>? SupportedVersions { get; init; }
    /// <summary>Where the facts about this boundary come from (source, documentation, configuration). Not a verification.</summary>
    public string Evidence { get; init; } = "";
}

public sealed record ContractDependencyGraph(List<ContractBoundary> Boundaries);

/// <summary>
/// A boundary's result. <see cref="Potential"/> marks an impact that only might reach it (through a mapping), never a confirmed failure.
/// <see cref="Depth"/> is the number of contracts between the changed contract and this boundary.
/// </summary>
public sealed record ContractBoundaryImpact(string BoundaryId, string Label, ContractBoundaryRole Role, string ContractId, ContractCompatibility Structural,
    ContractCompatibility Semantic, ContractCompatibility Overall, bool Potential, int Depth, List<string> Reasons, string Evidence);

/// <summary>
/// The impact of one contract's change: what changed, how each boundary of the graph is affected, the first boundary that is known to be
/// incompatible, and what could not be evaluated. Compatible is a contract statement, never "runtime observed".
/// </summary>
public sealed record ContractImpactResult
{
    public ContractDescriptor Contract { get; init; } = new("", SourceContractType.XmlSchema, "", null, "");
    public string? PreviousFingerprint { get; init; }
    public string CurrentFingerprint { get; init; } = "";
    /// <summary>False when there is no baseline to compare with; then nothing is called changed or unchanged.</summary>
    public bool BaselineAvailable { get; init; }
    public bool Changed { get; init; }
    public List<ContractChangeAssessment> Changes { get; init; } = [];
    public List<ContractBoundaryImpact> Boundaries { get; init; } = [];
    public ContractBoundaryImpact? FirstIncompatibleBoundary { get; init; }
    public ContractCompatibility Overall { get; init; }
    public List<string> Unknowns { get; init; } = [];
}
