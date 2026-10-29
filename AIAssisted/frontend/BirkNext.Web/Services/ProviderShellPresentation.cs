using BirkNext.Integrations;
using Microsoft.AspNetCore.Components;

namespace BirkNext.Web.Services;

/// <summary>One summary card of a provider detail view: status as text plus tone, and one short reason when not ready.</summary>
public sealed record ShellCard(string Key, string Label, string Status, string Tone, string Reason)
{
    /// <summary>A short explanation of the status (tooltip), e.g. what "No executor" means.</summary>
    public string? Help { get; init; }
}

public sealed record ShellFixItem(string Label, string Problem);

/// <summary>
/// One owner's unresolved items with the single action that resolves them: a link (<see cref="Href"/>), opening a provider section
/// (<see cref="OpenSection"/>), applying the listed pending values (<see cref="ApplyKeys"/>), or a provider-specific action (<see cref="Action"/>
/// handled by the provider through the shell's fix callback). <see cref="Severity"/> separates true blockers from review-only issues;
/// <see cref="Impact"/> says what it blocks; <see cref="Details"/> (long diagnostics) stays collapsed.
/// </summary>
public sealed record ShellFixGroup(string Key, string Title, string Owner, string ActionLabel, IReadOnlyList<ShellFixItem> Items)
{
    public string? Href { get; init; }
    public string? OpenSection { get; init; }
    public IReadOnlyList<string> ApplyKeys { get; init; } = [];
    public string? Action { get; init; }
    public string Severity { get; init; } = ShellSeverity.Blocked;
    public string? Impact { get; init; }
    public string? CountBadge { get; init; }
    public string? Details { get; init; }
}

/// <summary>Issue severities: a blocker prevents a relevant action; a review item needs a decision but blocks nothing by itself.</summary>
public static class ShellSeverity
{
    public const string Blocked = "Blocked", Review = "Review required";
    public static string Tone(string severity) => severity == Blocked ? "bad" : "warn";
}

/// <summary>A detected or suggested value waiting for an explicit Apply: field, readable value, state and source.</summary>
public sealed record ShellProposal(string Key, string Label, string Value, string State, string Source, string? Current, bool Conflict);

/// <summary>
/// One provider-specific section: a summary line always visible, its detail (<see cref="Body"/>) behind a button, or a link to the owner
/// that manages it (<see cref="LinkHref"/>). <see cref="Always"/> is a small always-visible row (e.g. a session secret's availability).
/// </summary>
public sealed record ShellSection(string Key, string Title, string Summary)
{
    public string? Status { get; init; }
    public string Tone { get; init; } = "muted";
    public string ToggleLabel { get; init; } = "Review";
    public RenderFragment? Body { get; init; }
    public RenderFragment? Always { get; init; }
    public string? LinkLabel { get; init; }
    public string? LinkHref { get; init; }
    public string? LinkTestId { get; init; }
}

/// <summary>
/// The compact detail view of a connected system that has no dedicated component (REST API, GraphQL, Service Bus, Event Hub, Database,
/// File/SFTP, Generic HTTP, Other): summary cards from the provider's own dimensions, things to fix grouped by setting group, detected values
/// awaiting Apply, and one section per setting group. Only a saved value satisfies a requirement — a detected or suggested value is "Not
/// applied" until a person applies it (suggested != configured), and an optional value never becomes a thing to fix.
/// </summary>
public static class ProviderShellPresentation
{
    public static IReadOnlyList<SetupReadinessItem> Dimensions(ExternalIntegrationView view) =>
        view.Dimensions.Count > 0 ? view.Dimensions : ExternalIntegrationPresentation.Dimensions(view.Summary);

    public static IReadOnlyList<ShellCard> Cards(ExternalIntegrationView view) =>
        [.. Dimensions(view).Select(item => new ShellCard(item.Key, item.Label, ExternalIntegrationPresentation.Label(item.State), ExternalIntegrationPresentation.Tone(item.State), CardReason(item, view))
            { Help = ExternalIntegrationPresentation.Help(item.State) })];

    /// <summary>One short phrase under a card; empty when ready or not assessed (the full reason stays in Advanced details).</summary>
    public static string CardReason(SetupReadinessItem item, ExternalIntegrationView view)
    {
        if (item.State is SetupReadinessState.Ready or SetupReadinessState.NotAssessed or SetupReadinessState.NotApplicable or SetupReadinessState.Verified) return "";
        if (item.State == SetupReadinessState.NoExecutor) return item.Reason;
        if (item.Key == "configuration")
        {
            var open = view.Fields.Where(Required).Where(field => !Saved(field)).ToList();
            var notApplied = open.Count(Pending);
            return notApplied == open.Count && notApplied > 0 ? $"{notApplied} not applied"
                : $"{open.Count - notApplied} missing{(notApplied > 0 ? $" · {notApplied} not applied" : "")}";
        }
        if (item.Reason.Length <= 48) return item.Reason;
        var cut = item.Reason.IndexOfAny(['.', '—', '·']);
        return item.Reason[..(cut > 0 && cut <= 48 ? cut : 48)].Trim();
    }

    /// <summary>Required for configuration: the provider's required non-secret fields.</summary>
    public static bool Required(AltinnSettingField field) => field.Requirement == AltinnRequirement.Manual;
    private static bool Saved(AltinnSettingField field) => field.State is AltinnSettingState.Configured or AltinnSettingState.Confirmed or AltinnSettingState.Verified;
    private static bool Pending(AltinnSettingField field) => field.State is AltinnSettingState.Detected or AltinnSettingState.Declared && field.Proposal is { Value: not null };

    public static string Problem(AltinnSettingField field) => field.State switch
    {
        AltinnSettingState.Conflict => "Conflict",
        AltinnSettingState.Detected => $"Not applied · Detected from {field.SourceLabel}",
        AltinnSettingState.Declared => $"Not applied · Suggested by {field.SourceLabel}",
        _ => "Missing",
    };

    public static string SectionKey(string group) => group switch
    {
        "Connection" or "Application" => "configuration",
        _ => group.ToLowerInvariant().Replace(' ', '-'),
    };

    public static string SectionTitle(string group) => group switch
    {
        "Connection" or "Application" => "Configuration",
        _ => group,
    };

    /// <summary>Required unresolved settings grouped by setting group (missing, conflict, or detected/suggested but not applied), plus
    /// unresolved dependencies with their canonical owner. One action per group.</summary>
    public static IReadOnlyList<ShellFixGroup> ThingsToFix(ExternalIntegrationView view, ExternalProviderDescriptor descriptor)
    {
        var owner = $"Connected Systems · {descriptor.DisplayName}";
        var groups = new List<ShellFixGroup>();
        foreach (var group in view.Fields.Where(Required).Where(field => !Saved(field)).GroupBy(field => field.Group))
        {
            var items = group.ToList();
            var applyOnly = items.All(Pending);
            var key = SectionKey(group.Key);
            groups.Add(new ShellFixGroup(key, SectionTitle(group.Key), owner, applyOnly ? "Apply detected settings" : $"Edit {SectionTitle(group.Key).ToLowerInvariant()}",
                [.. items.Select(field => new ShellFixItem(field.Label, Problem(field)))])
            {
                ApplyKeys = applyOnly ? [.. items.Select(field => field.Key)] : [],
                OpenSection = applyOnly ? null : key,
                // Values that only wait for Apply need a decision; missing required values block.
                Severity = applyOnly ? ShellSeverity.Review : ShellSeverity.Blocked,
                Impact = applyOnly ? "Detected values wait for your decision" : "Blocks use of this system",
            });
        }
        foreach (var dependency in view.Dependencies.Where(item => item.Dependency.Required && item.State != SetupDependencyState.Resolved))
            groups.Add(new ShellFixGroup($"dependency-{dependency.Dependency.Reference}", "Dependencies", dependency.Owner, $"Open {dependency.Owner}",
                [new(ExternalIntegrationPresentation.DependencyLabel(dependency.Dependency), ExternalIntegrationPresentation.Label(dependency.State))])
                { Href = dependency.OwnerHref, Impact = "A required dependency is unresolved" });
        return groups;
    }

    /// <summary>Detected or suggested values a person may apply (never a secret).</summary>
    public static IReadOnlyList<ShellProposal> Proposals(ExternalIntegrationView view, ExternalProviderDescriptor descriptor) =>
        [.. view.Fields.Where(field => field.Proposal is { Value: not null } && descriptor.Fields.Any(item => item.Key == field.Key && !item.Secret))
            .Select(field => new ShellProposal(field.Key, field.Label, AltinnEnvironmentPresentation.Display(field.Proposal!), AltinnEnvironmentPresentation.StateLabel(field.Proposal!.State),
                field.Proposal.SourceLabel, field.State == AltinnSettingState.Conflict ? AltinnEnvironmentPresentation.Display(field) : null, field.State == AltinnSettingState.Conflict))];

    /// <summary>"2 of 3 saved · 1 not applied" for one setting group.</summary>
    public static string GroupLine(ExternalIntegrationView view, string group)
    {
        var fields = view.Fields.Where(field => field.Group == group && field.Requirement != AltinnRequirement.Automated).ToList();
        var saved = fields.Count(Saved);
        var pending = fields.Count(Pending);
        var missing = fields.Count(field => Required(field) && field.State is AltinnSettingState.Missing or AltinnSettingState.Unknown);
        var parts = new List<string> { $"{saved} of {fields.Count} saved" };
        if (pending > 0) parts.Add($"{pending} not applied");
        if (missing > 0) parts.Add($"{missing} required missing");
        return string.Join(" · ", parts);
    }

    public static SetupReadinessItem? Dimension(ExternalIntegrationView view, string key) => Dimensions(view).FirstOrDefault(item => item.Key == key);

    /// <summary>Concise help for the shared status vocabulary (tooltip next to the always-visible word).</summary>
    public static string? StatusHelp(string status) => status switch
    {
        "Ready" => "Required configuration for this dimension is complete.",
        "Blocked" => "A known blocker prevents a relevant action.",
        "Partial" => "Some required evidence or configuration is incomplete.",
        "Needs review" or "Review required" => "A user decision or confirmation is required.",
        "Observed" => "Runtime evidence exists, but it is not necessarily verified.",
        "Not verified" => "No sufficient runtime verification exists yet.",
        "Not configured" => "The capability exists, but nothing is configured.",
        "Unknown" => "The state cannot currently be determined.",
        "Optional" => "Missing does not block readiness.",
        _ => null,
    };
}
