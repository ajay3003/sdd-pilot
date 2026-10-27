using System.Text.Json.Serialization;

namespace BirkNext.Integrations;

// Setup profiles: a versioned YAML/JSON description of DESIRED configuration. Importing one validates (no mutation), previews a change plan
// against the current owners (no mutation) and applies only the safe changes a person selected. A profile never becomes source verified,
// runtime verified, a confirmed mapping, a confirmed contract, an execution permission or an approved identity, and it never carries secrets.
// The owners (Target Environment, IQR bindings, Test Pack run configuration) stay authoritative; nothing is stored as a parallel configuration.

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SetupProfileFormat { Yaml, Json }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SetupIssueSeverity { Error, Warning, Rejected }

/// <summary>One validation finding at a profile path. A Rejected field never carries its value back (secrets are never echoed or logged).</summary>
public sealed record SetupProfileIssue(string Path, SetupIssueSeverity Severity, string Message);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SetupChangeClassification { New, Same, Change, Conflict, CannotImport, RequiresConfirmation, ReferenceOnly }

/// <summary>What an imported value is: desired configuration, an expected mapping, reference metadata or a proposed source binding — never a fact.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SetupValueKind { DesiredConfiguration, ExpectedMapping, ReferenceMetadata, ProposedSourceBinding }

/// <summary>One planned change, routed to the owner that holds the value. Generic: the preview renders every item from this shape.</summary>
public sealed record SetupChange
{
    public string Id { get; init; } = "";
    public string Journey { get; init; } = "";
    /// <summary>The owner that keeps the value ("Target Environment → Integration Catalog", "Integration Quality Review → evidence binding", "Test pack run configuration").</summary>
    public string Owner { get; init; } = "";
    public string Setting { get; init; } = "";
    public string? Current { get; init; }
    public string? Proposed { get; init; }
    /// <summary>Where the current value comes from, and that the proposed one comes from the imported profile.</summary>
    public string Provenance { get; init; } = "";
    public SetupValueKind Kind { get; init; }
    public SetupChangeClassification Classification { get; init; }
    public bool SafeToApply { get; init; }
    public bool RequiresConfirmation { get; init; }
    /// <summary>Preselected in the preview: safe New values only (never a conflict, never anything needing confirmation).</summary>
    public bool Preselected { get; init; }
    public string Reason { get; init; } = "";
    public string Destination { get; init; } = "";
}

public sealed record SetupProfileSummary(int Supported, int ReadyToApply, int AlreadyConfigured, int Conflicts, int RequiresConfirmation, int CannotImport, int ReferenceOnly,
    int Rejected, int ManualRequirements);

public sealed record SetupProfilePreview
{
    public bool Valid { get; init; }
    public string? ProfileName { get; init; }
    public int? Version { get; init; }
    public string? TestPack { get; init; }
    public SetupProfileFormat Format { get; init; }
    public IReadOnlyList<SetupProfileIssue> Issues { get; init; } = [];
    public IReadOnlyList<SetupChange> Changes { get; init; } = [];
    public IReadOnlyList<string> ManualRequirements { get; init; } = [];
    public SetupProfileSummary Summary { get; init; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0);
    /// <summary>"Profile targets Development · current environment is QA" — every change then requires confirmation.</summary>
    public string? EnvironmentNotice { get; init; }
    /// <summary>Always zero: a profile can never carry a secret into BirkNext.</summary>
    public int SecretsAccepted => 0;
}

public sealed record SetupProfileRequest(string FileName, string Content);

public sealed record SetupApplyRequest(string FileName, string Content, IReadOnlyList<string> SelectedChangeIds, IReadOnlyList<string>? ConfirmedChangeIds = null);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SetupApplyOutcome { Applied, AlreadyMatched, NotSelected, Refused, Failed }

public sealed record SetupApplyItemResult(string ChangeId, string Setting, SetupApplyOutcome Outcome, string Detail);

/// <summary>Per-operation results — never a generic success. Active execution and every trust state are always unchanged.</summary>
public sealed record SetupApplyResult
{
    public IReadOnlyList<SetupApplyItemResult> Items { get; init; } = [];
    public int Applied => Items.Count(item => item.Outcome == SetupApplyOutcome.Applied);
    public int AlreadyMatched => Items.Count(item => item.Outcome == SetupApplyOutcome.AlreadyMatched);
    public int ConflictsLeft { get; init; }
    public int Failed => Items.Count(item => item.Outcome == SetupApplyOutcome.Failed);
    public int ManualRemaining { get; init; }
    public int SecretsImported => 0;
    public bool ActiveExecutionUnchanged => true;
    public IReadOnlyList<SetupProfileIssue> Issues { get; init; } = [];
}

public sealed record SetupProfileExport(SetupProfileFormat Format, string FileName, string Content, IReadOnlyList<string> Excluded);
