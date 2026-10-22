using BirkNext.Integrations;
using BirkNext.Web.Models;
using BirkNext.Web.Services.ProjectImport;

namespace BirkNext.Web.Services;

/// <summary>The evidence source of an integration being added or edited, as the Add integration form shows it. Availability is never a binding.</summary>
public enum TemplateEvidenceState { NotRequired, Missing, Available, NeedsBinding, Ambiguous, Bound }

/// <summary>What the Add/Edit integration form saves: the record, and the evidence snapshot a person explicitly chose to bind (null: none).</summary>
public sealed record IntegrationEditorSave(IntegrationDefinition Definition, Guid? EvidenceSnapshotId, string? EvidenceArchive)
{
    /// <summary>Review decisions still open when saved (consumer mapping, source evidence, contract relationship). Saving never resolves them.</summary>
    public IReadOnlyList<string> ReviewRemaining { get; init; } = [];
}

/// <summary>How template values meet values already in the form. <see cref="WhereEmpty"/> is the safe default; nothing is replaced silently.</summary>
public enum TemplateApplyMode { KeepMine, WhereEmpty, Replace }

/// <summary>
/// Add-integration templates in the form: which fields a template fills, which form values it would replace (a person decides), and the
/// evidence source state. Generic over every pack's template metadata — no pack is named here. Nothing here creates, binds or trusts.
/// </summary>
public static class IntegrationTemplatePresentation
{
    /// <summary>The form fields a template may fill, in form order (IntegrationDefinition property names; "Consumer" is the consumer display name).</summary>
    public static readonly IReadOnlyList<string> Fields =
        ["DisplayName", "Kind", "SystemName", "EndpointOrTopic", "Producer", "SourceResource", "Consumer", "SourceSystem", "DestinationSystem"];

    public static string FieldLabel(string field) => field switch
    {
        "DisplayName" => "Display name",
        "Kind" => "Type",
        "SystemName" => "Integration system",
        "EndpointOrTopic" => "Endpoint / channel",
        "SourceResource" => "Source",
        "SourceSystem" => "Source system",
        "DestinationSystem" => "Destination system",
        _ => field,
    };

    /// <summary>A template's value for one form field (the type comes from the template itself, never from a field value).</summary>
    public static IntegrationTemplateValue? Value(IntegrationTemplateView template, string field) =>
        field == "Kind" ? new(template.Kind.ToString(), "Template") : template.Values.TryGetValue(field, out var value) ? value : null;

    /// <summary>
    /// Fields whose current value was entered by a person (non-empty, not what a template filled) and differs from the template value.
    /// <paramref name="templateFilled"/> holds what a previously applied template wrote, so switching templates does not ask about them.
    /// </summary>
    public static IReadOnlyList<string> Conflicts(IReadOnlyDictionary<string, string?> current, IReadOnlyDictionary<string, string> templateFilled, IntegrationTemplateView template) =>
        Fields.Where(field => Value(template, field) is { } value
                && current.TryGetValue(field, out var mine) && !string.IsNullOrWhiteSpace(mine)
                && !(templateFilled.TryGetValue(field, out var filled) && filled == mine)
                && !string.Equals(mine.Trim(), value.Value, StringComparison.Ordinal))
            .ToList();

    /// <summary>The field values to write: template values where the field is empty or template-filled; person-entered values only on Replace.</summary>
    public static IReadOnlyDictionary<string, string> Apply(IReadOnlyDictionary<string, string?> current, IReadOnlyDictionary<string, string> templateFilled, IntegrationTemplateView template, TemplateApplyMode mode)
    {
        var conflicts = Conflicts(current, templateFilled, template).ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in Fields)
        {
            if (Value(template, field) is not { } value) continue;
            if (conflicts.Contains(field) && mode != TemplateApplyMode.Replace) continue;
            result[field] = value.Value;
        }
        return result;
    }

    /// <summary>The environment kind templates resolve endpoints for ("Development", "QA"); Custom or an unset profile has none.</summary>
    public static string? EnvironmentType(FrontendAnalysisProfile? profile) =>
        profile is null || profile.EnvironmentType == FrontendEnvironmentType.Custom ? null : profile.EnvironmentType.ToString();

    /// <summary>
    /// The form's evidence state: the integration's own canonical binding first (Bound — never replaced here), then what the pack's source
    /// requirement finds among stored snapshots. A source bound to another integration still needs this integration's own binding.
    /// </summary>
    public static TemplateEvidenceState Evidence(TestPackSourceStatus? status, IntegrationEvidenceBinding? ownBinding)
    {
        if (ownBinding is not null) return TemplateEvidenceState.Bound;
        if (status is null || status.Role.Provider == TestPackSourceProvider.TestPackReference) return TemplateEvidenceState.NotRequired;
        var choices = Choices(status);
        if (choices.Count == 0) return TemplateEvidenceState.Missing;
        if (SourceSetupPresentation.IsAmbiguous(status) || choices.Select(c => c.Fingerprint).Distinct(StringComparer.Ordinal).Count() > 1) return TemplateEvidenceState.Ambiguous;
        return status.State is TestPackSourceState.Bound or TestPackSourceState.ReviewRequired ? TemplateEvidenceState.NeedsBinding : TemplateEvidenceState.Available;
    }

    /// <summary>The snapshots a person may choose from (one per snapshot, in the backend's order). Never chosen automatically.</summary>
    public static IReadOnlyList<TestPackSourceCandidate> Choices(TestPackSourceStatus status) =>
        status.Candidates.GroupBy(c => c.SnapshotId).Select(g => g.First()).ToList();

    /// <summary>The same wording as every other page (<see cref="SourceSetupPresentation"/>): an unbound match is "Available · needs binding",
    /// several matches are "Review required", and only no match at all is "Missing".</summary>
    public static string Label(TemplateEvidenceState state) => state switch
    {
        TemplateEvidenceState.NotRequired => "Not required",
        TemplateEvidenceState.Available or TemplateEvidenceState.NeedsBinding => SourceSetupPresentation.AvailableNeedsBinding,
        TemplateEvidenceState.Ambiguous => "Review required",
        TemplateEvidenceState.Bound => "Bound",
        _ => "Missing",
    };

    public static string Meaning(TemplateEvidenceState state) => state switch
    {
        TemplateEvidenceState.NotRequired => "This integration does not require source evidence.",
        TemplateEvidenceState.Available => "One matching Source Analysis snapshot exists. It becomes this integration's evidence only when you choose it.",
        TemplateEvidenceState.NeedsBinding => "Matching source exists, but this integration still needs its own explicit binding.",
        TemplateEvidenceState.Ambiguous => "Several matching snapshots found. Choose one — the newest is never picked for you.",
        TemplateEvidenceState.Bound => "This integration already has canonical source evidence. It is changed only in IQR Evidence.",
        _ => "No matching imported source found.",
    };

    /// <summary>The template's Test Pack reference as a compact state ("Reference available"), null when the template uses none.</summary>
    public static string? ReferenceLabel(IntegrationTemplateView? template) => template?.ReferenceSource?.State switch
    {
        null => null,
        TestPackSourceState.ReferenceAvailable => "Reference available",
        _ => "Reference unavailable",
    };

    /// <summary>
    /// A section's state in the form: what the person configured, else known reference metadata, else whether the section is something to set
    /// at all. Reference metadata never reads as "Configured" or "Verified"; an optional section never reads as missing setup.
    /// </summary>
    public static string SectionHint(bool configured, bool referenceAvailable, bool optional) =>
        configured ? referenceAvailable ? "Configured · reference available" : "Configured"
        : referenceAvailable ? "Reference available"
        : optional ? "Optional · not set" : "Not configured";

    /// <summary>Where the schema the reference names stands in the evidence source. The reference only names it; the source holds it.</summary>
    public enum SchemaEvidenceState { NotApplicable, NoSource, NotFound, Available, Ambiguous, Bound }

    /// <summary>
    /// The schema named by the reference in the evidence source: follows the source's own state (no source → nothing to look in; several
    /// sources → choose the source first) and then the exact-name matches inside it (one → its candidate; several → review). Never chosen.
    /// </summary>
    public static SchemaEvidenceState SchemaEvidence(IntegrationTemplateView? template, TemplateEvidenceState evidence, Guid? boundSnapshot)
    {
        if (template?.SchemaName is null || template.EvidenceSource is null) return SchemaEvidenceState.NotApplicable;
        if (evidence == TemplateEvidenceState.Missing) return SchemaEvidenceState.NoSource;
        if (evidence == TemplateEvidenceState.Ambiguous) return SchemaEvidenceState.Ambiguous;
        var inScope = boundSnapshot is { } bound ? template.SchemaCandidates.Where(c => c.SnapshotId == bound).ToList() : template.SchemaCandidates.ToList();
        var files = inScope.Select(c => c.File).Distinct(StringComparer.Ordinal).Count();
        return files == 0 ? SchemaEvidenceState.NotFound : files > 1 ? SchemaEvidenceState.Ambiguous
            : evidence == TemplateEvidenceState.Bound ? SchemaEvidenceState.Bound : SchemaEvidenceState.Available;
    }

    public static string SchemaLabel(SchemaEvidenceState state) => state switch
    {
        SchemaEvidenceState.NoSource => "No matching source",
        SchemaEvidenceState.NotFound => "Not found in the matching source",
        SchemaEvidenceState.Available => SourceSetupPresentation.AvailableNeedsBinding,
        SchemaEvidenceState.Ambiguous => "Review required",
        SchemaEvidenceState.Bound => "In bound source",
        _ => "Not applicable",
    };

    /// <summary>Pill tone (the label always carries the meaning; colour is never the only signal).</summary>
    public static string Tone(TemplateEvidenceState state) => state switch
    {
        TemplateEvidenceState.Bound => "ready",
        TemplateEvidenceState.Available or TemplateEvidenceState.NeedsBinding or TemplateEvidenceState.Ambiguous => "attention",
        TemplateEvidenceState.Missing => "warning",
        _ => "muted",
    };
}
