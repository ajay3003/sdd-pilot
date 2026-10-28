using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>
/// Labels and tones for Target Environment → Connected Systems → Altinn. Every state is shown as text (never colour alone) and states are never collapsed into a
/// generic "available": configured, detected (from source or metadata), suggested (a test-pack default), observed (runtime evidence) and
/// verified stay distinct.
/// </summary>
public static class AltinnEnvironmentPresentation
{
    /// <summary>The environment's Altinn system under Target Environment → Connected Systems.</summary>
    public static string AltinnHref(string environmentId) => ExternalIntegrationPresentation.ExternalHref(environmentId, "altinn");

    /// <summary>The Altinn system's contract evidence (public metadata, app source, deployed-contract changes).</summary>
    public static string ContractHref(string environmentId) => ExternalIntegrationPresentation.ExternalHref(environmentId, "altinn-contract");

    public const string TestDataHref = "/test-data";

    public static string StateLabel(AltinnSettingField field) => field.State switch
    {
        AltinnSettingState.Detected => field.Source switch
        {
            AltinnSettingSource.SourceAnalysis => "Detected from source",
            AltinnSettingSource.PublicMetadata => "Detected · public metadata",
            _ => "Detected",
        },
        _ => StateLabel(field.State),
    };

    public static string StateLabel(AltinnSettingState state) => state switch
    {
        AltinnSettingState.Configured => "Configured",
        AltinnSettingState.Detected => "Detected",
        AltinnSettingState.Observed => "Observed",
        AltinnSettingState.Declared => "Suggested",
        AltinnSettingState.Confirmed => "Confirmed",
        AltinnSettingState.Verified => "Runtime verified",
        AltinnSettingState.Missing => "Missing",
        AltinnSettingState.Conflict => "Conflict",
        AltinnSettingState.AvailableToBackend => "Available to backend",
        AltinnSettingState.AvailableForSession => "Available for this session",
        _ => "Unknown",
    };

    public static string Tone(AltinnSettingField field) => field.State switch
    {
        AltinnSettingState.Configured or AltinnSettingState.Confirmed or AltinnSettingState.Verified
            or AltinnSettingState.AvailableToBackend or AltinnSettingState.AvailableForSession => "ok",
        AltinnSettingState.Conflict => "warn",
        AltinnSettingState.Missing => field.Requirement == AltinnRequirement.Optional ? "muted" : "warn",
        AltinnSettingState.Unknown => "muted",
        _ => "info",
    };

    public static string Tone(AltinnReadinessState state) => state switch
    {
        AltinnReadinessState.Ready => "ok",
        AltinnReadinessState.Partial => "warn",
        AltinnReadinessState.Blocked => "bad",
        _ => "muted",
    };

    public static string ReadinessLabel(AltinnReadinessItem item) => item.Key == "runtime" && item.State == AltinnReadinessState.Partial ? "Observed" : item.State switch
    {
        AltinnReadinessState.Ready => "Ready",
        AltinnReadinessState.Partial => "Partial",
        AltinnReadinessState.Blocked => "Blocked",
        _ => "Not verified",
    };

    public static string RequirementLabel(AltinnRequirement requirement) => requirement switch
    {
        AltinnRequirement.Manual => "Required",
        AltinnRequirement.Automated => "Automated execution",
        _ => "Optional",
    };

    public static string ObservationLabel(AltinnRuntimeObservationKind kind) => kind switch
    {
        AltinnRuntimeObservationKind.ReporterLogin => "Reporter TestID login",
        AltinnRuntimeObservationKind.IdPortenAuthentication => "ID-porten authentication",
        AltinnRuntimeObservationKind.AppOpened => "TT02 app opened",
        AltinnRuntimeObservationKind.FormCompleted => "Form completed",
        AltinnRuntimeObservationKind.Submission => "Report submitted",
        _ => "Altinn receipt generated",
    };

    /// <summary>"7 configured · 4 detected · 2 runtime observations · 1 value missing · 0 conflicts".</summary>
    public static string DetectionLine(AltinnDetectionSummary summary) =>
        $"{summary.Configured} configured · {summary.Detected} detected · {summary.Observed} runtime observation{(summary.Observed == 1 ? "" : "s")} · "
        + $"{summary.Missing} value{(summary.Missing == 1 ? "" : "s")} missing · {summary.Conflicts} conflict{(summary.Conflicts == 1 ? "" : "s")}";

    /// <summary>"1 required setting missing" (top summary), or null when nothing required is missing or in conflict.</summary>
    public static string? MissingLine(AltinnEnvironmentView view) =>
        view.Detection.ActionableGaps.Count == 0 ? null
            : $"{view.Detection.ActionableGaps.Count} required setting{(view.Detection.ActionableGaps.Count == 1 ? "" : "s")} missing or in conflict";

    /// <summary>The fields a person may deliberately apply: a proposal that is not secret and not evidence.</summary>
    public static IReadOnlyList<AltinnSettingField> Proposals(AltinnEnvironmentView view) =>
        [.. view.Fields.Where(field => field.Proposal is { Value: not null } && Applicable.Contains(field.Key))];

    private static readonly string[] Applicable = ["environment", "app", "app-base-url", "form-data-type", "content-type", "integration", "client-id", "redirect-uri", "scopes"];

    /// <summary>The save request for deliberately applied proposals (key → value). Observations and secrets are never applicable.</summary>
    public static AltinnEnvironmentSettingsUpdate Apply(IEnumerable<(string Key, string Value)> selected)
    {
        var update = new AltinnEnvironmentSettingsUpdate();
        foreach (var (key, value) in selected)
            update = key switch
            {
                "environment" => update with { Environment = value.StartsWith("Test", StringComparison.Ordinal) ? "Test" : value },
                "app" => update with { AppIdentifier = value },
                "app-base-url" => update with { AppBaseUrl = value },
                "form-data-type" => update with { FormDataType = value },
                "content-type" => update with { ContentType = value },
                "integration" => update with { IntegrationId = value },
                "client-id" => update with { ClientId = value },
                "redirect-uri" => update with { RedirectUri = value },
                "scopes" => update with { Scopes = [.. value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)] },
                _ => update,
            };
        return update;
    }

    // ── Compact tester view: what is ready, what is missing, what to click ──

    /// <summary>Where a "things to fix" group sends the tester. Each maps to one control on the page (or the Test Data link).</summary>
    public enum FixAction { Detect, Application, Automated, Safety, TestData, Contract, ApplySuggested }

    public sealed record FixItem(string Label, string Problem);

    /// <summary>One owner's unresolved required items with the single action that resolves them. <see cref="ApplyKeys"/> lists the pending
    /// suggestions an "Apply suggested settings" action applies (exactly those, nothing else).</summary>
    public sealed record FixGroup(string Key, string Title, string Owner, string Action, FixAction Kind, IReadOnlyList<FixItem> Items)
    {
        public IReadOnlyList<string> ApplyKeys { get; init; } = [];
    }

    /// <summary>The shared shell's form of a fix group (the action is reported back by key).</summary>
    public static ShellFixGroup ToShell(FixGroup group) => new(group.Key, group.Title, group.Owner, group.Action, group.Items.Select(item => new ShellFixItem(item.Label, item.Problem)).ToList())
    {
        Href = group.Kind == FixAction.TestData ? TestDataHref : null,
        Action = group.Kind.ToString(),
        ApplyKeys = group.ApplyKeys,
    };

    /// <summary>The value a tester reads: a reference's display name (e.g. an integration's name) before its raw id.</summary>
    public static string Display(AltinnSettingField field) => field.DisplayValue ?? field.Value ?? "—";
    public static string Display(AltinnSettingCandidate candidate) => candidate.DisplayValue ?? candidate.Value ?? "—";

    private static bool Unresolved(AltinnSettingField field) => field.State is AltinnSettingState.Missing or AltinnSettingState.Conflict or AltinnSettingState.Unknown;
    /// <summary>A required value that is only suggested (a test pack default or reference): it satisfies nothing until applied.</summary>
    private static bool Suggested(AltinnSettingField field) => field.State == AltinnSettingState.Declared && field.Proposal is { Value: not null };
    private static bool AutomatedOpen(AltinnSettingField field) =>
        field.State is not (AltinnSettingState.Configured or AltinnSettingState.Confirmed or AltinnSettingState.Verified or AltinnSettingState.AvailableForSession or AltinnSettingState.AvailableToBackend);
    private static string Problem(AltinnSettingField field) => field.State switch
    {
        AltinnSettingState.Conflict => "Conflict",
        AltinnSettingState.Declared => $"Not applied · Suggested by {field.Proposal?.SourceLabel ?? field.SourceLabel}",
        AltinnSettingState.Detected => "Not applied",
        _ => "Missing",
    };
    private static string ShortLabel(AltinnSettingField field) => field.Key switch
    {
        "enabled" => "Enabled",
        "redirect-uri" => "Redirect URI",
        _ => field.Label,
    };

    /// <summary>Every required unresolved item, grouped by the owner that resolves it, each group with one action. Manual and automated
    /// requirements both appear (the requirement decides only which readiness they block).</summary>
    public static IReadOnlyList<FixGroup> ThingsToFix(AltinnEnvironmentView view)
    {
        var groups = new List<FixGroup>();
        var applicationFields = view.Fields.Where(field => field.Group == "Application" && field.Requirement != AltinnRequirement.Optional && (Unresolved(field) || Suggested(field))).ToList();
        var application = applicationFields.Select(field => new FixItem(field.Label, Problem(field))).ToList();
        if (application.Count > 0)
        {
            var suggested = applicationFields.Where(Suggested).Select(field => field.Key).ToList();
            var detect = !view.Detected && application.Any(item => item.Problem == "Missing");
            groups.Add(suggested.Count > 0
                ? new("application", "Application", "Target Environment · Altinn", "Apply suggested settings", FixAction.ApplySuggested, application) { ApplyKeys = suggested }
                : new("application", "Application", "Target Environment · Altinn", detect ? "Detect settings" : "Review application", detect ? FixAction.Detect : FixAction.Application, application));
        }
        var automated = view.Fields.Where(field => field.Group == "Automated execution" && AutomatedOpen(field)).Select(field => new FixItem(ShortLabel(field), Problem(field))).ToList();
        if (automated.Count > 0) groups.Add(new("automated", "Automated authentication", "Target Environment · Altinn (unattended client)", "Configure automated execution", FixAction.Automated, automated));
        var safety = view.Fields.Where(field => field.Group == "Execution safety" && Unresolved(field)).Select(field => new FixItem(field.Label, Problem(field))).ToList();
        if (safety.Count > 0) groups.Add(new("safety", "Execution safety", "Backend execution policy", "Review execution safety", FixAction.Safety, safety));
        var identities = IdentityGaps(view).Select(gap => new FixItem(gap.Role, gap.Problem)).ToList();
        if (identities.Count > 0) groups.Add(new("identities", "Test identities", "Test Data", "Manage test identities", FixAction.TestData, identities));
        // The payload (what is submitted) is Test Data too, but a different gap than the identities (who is involved): one group, owner Test Data.
        if (view.Summary.FirstOrDefault(item => item.Key == "testdata") is { State: not AltinnReadinessState.Ready } payload)
            groups.Add(new("testdata", "Test data", "Test Data", "Open Test Data", FixAction.TestData, [new("XML fixture", PayloadProblem(payload))]));
        if (view.Contract.State is AltinnReadinessState.Partial or AltinnReadinessState.Blocked)
            groups.Add(new("contract", "Report contract", "IQR / Altinn contract evidence", "Review contract", FixAction.Contract,
                [new("Report contract", view.Contract.State == AltinnReadinessState.Partial ? view.Contract.Reason : "Missing")]));
        return groups;
    }

    public static int FixCount(IReadOnlyList<FixGroup> groups) => groups.Sum(group => group.Items.Count);

    /// <summary>"7 things need attention" (null when nothing does).</summary>
    public static string? FixLine(IReadOnlyList<FixGroup> groups) =>
        FixCount(groups) is var count && count == 0 ? null : $"{count} thing{(count == 1 ? " needs" : "s need")} attention";

    private static List<(string Role, string Problem)> IdentityGaps(AltinnEnvironmentView view)
    {
        var gaps = new List<(string, string)>();
        var reporters = view.Identities.Where(item => item.Role == "reporter" && item.State is not AltinnSettingState.Missing).ToList();
        if (reporters.Count == 0) gaps.Add(("Reporter", "Missing"));
        else if (reporters.All(item => item.State != AltinnSettingState.Confirmed)) gaps.Add(("Reporter", "TT02 profile not confirmed"));
        if (!view.Identities.Any(item => item.Role == "child" && item.State is not AltinnSettingState.Missing)) gaps.Add(("Child", "Missing"));
        return gaps;
    }

    /// <summary>The short reason under a summary card: empty when ready, otherwise one phrase (details live in the sections below).</summary>
    /// <summary>"missing", "invalid against its XSD", "needs revalidation" … from the backend's payload reason (the shared Test Data rule decided it).</summary>
    public static string PayloadProblem(AltinnReadinessItem payload) =>
        System.Text.RegularExpressions.Regex.Match(payload.Reason, @"XML fixture (?<p>.+?)(?: \(| ·|$)") is { Success: true } match ? match.Groups["p"].Value : payload.Reason;

    public static string CardReason(AltinnReadinessItem item, AltinnEnvironmentView view)
    {
        if (item.State == AltinnReadinessState.Ready) return "";
        var fix = ThingsToFix(view);
        int Count(string key) => fix.FirstOrDefault(group => group.Key == key)?.Items.Count ?? 0;
        static string Settings(int n, string kind) => $"{n} {kind}setting{(n == 1 ? "" : "s")} missing";
        var gaps = IdentityGaps(view);
        var observations = view.Saved.RuntimeObservations.Count;
        return item.Key switch
        {
            "app" => ApplicationGaps(view) is var (missing, pending) && missing + pending > 0
                ? string.Join(" · ", new[] { missing > 0 ? Settings(missing, "") : null, pending > 0 ? $"{pending} not applied" : null }.OfType<string>()) : item.Reason,
            "authentication" => Count("automated") > 0 ? Settings(Count("automated"), "automated ") : "",
            "identities" => gaps.Count == 2 && gaps.All(gap => gap.Problem == "Missing") ? "Reporter and child missing"
                : string.Join(" · ", gaps.Select(gap => $"{gap.Role} {gap.Problem.ToLowerInvariant()}")),
            "testdata" => $"XML fixture {PayloadProblem(item)}",
            "runtime" => item.State == AltinnReadinessState.Partial ? $"{observations} observation{(observations == 1 ? "" : "s")} · not verified" : "",
            "automated" => view.AutomatedBlockers.Count > 0 ? $"{view.AutomatedBlockers.Count} requirement{(view.AutomatedBlockers.Count == 1 ? "" : "s")}" : "",
            _ => "",
        };
    }

    /// <summary>The manual readiness line: "Missing: Form data type, Reporter, Child" (the backend reason when nothing is missing).</summary>
    public static string ManualLine(AltinnEnvironmentView view) =>
        view.ManualMissing.Count > 0 ? $"Missing: {string.Join(", ", view.ManualMissing)}" : view.ManualReadiness.Reason;

    /// <summary>The automated readiness line: "12 requirements unresolved" (the backend reason when ready).</summary>
    public static string AutomatedLine(AltinnEnvironmentView view) =>
        view.AutomatedBlockers.Count > 0 ? $"{view.AutomatedBlockers.Count} requirement{(view.AutomatedBlockers.Count == 1 ? "" : "s")} unresolved" : view.AutomatedReadiness.Reason;

    /// <summary>Application section summary: "TT02 · buf/skolenaervaer · 2 settings missing".</summary>
    public static string ApplicationLine(AltinnEnvironmentView view)
    {
        string? V(string key) => view.Fields.FirstOrDefault(field => field.Key == key)?.Value;
        var environment = V("environment") is { } env && env.StartsWith("Test", StringComparison.Ordinal) ? "TT02" : V("environment");
        var (missing, pending) = ApplicationGaps(view);
        var state = missing + pending == 0 ? "Complete"
            : string.Join(" · ", new[] { missing > 0 ? $"{missing} setting{(missing == 1 ? "" : "s")} missing" : null, pending > 0 ? $"{pending} not applied" : null }.OfType<string>());
        return string.Join(" · ", new[] { environment, V("app"), state }.Where(part => !string.IsNullOrEmpty(part)));
    }

    /// <summary>Required application settings that are missing/in conflict, and those only suggested (not applied).</summary>
    private static (int Missing, int Pending) ApplicationGaps(AltinnEnvironmentView view)
    {
        var fields = view.Fields.Where(field => field.Group == "Application" && field.Requirement != AltinnRequirement.Optional).ToList();
        return (fields.Count(Unresolved), fields.Count(Suggested));
    }

    /// <summary>Browser authentication summary from public metadata (descriptive platform evidence, never client configuration).</summary>
    public static string BrowserLine(AltinnEnvironmentView view)
    {
        var metadata = view.Fields.Where(field => field.Key.StartsWith("idporten-", StringComparison.Ordinal)).ToList();
        if (metadata.Count == 0) return "ID-porten TEST · public metadata not read yet (Detect settings reads it)";
        var pkce = metadata.FirstOrDefault(field => field.Key == "idporten-pkce")?.Value?.Split(' ').Contains("S256") == true ? " · PKCE S256" : "";
        return $"ID-porten TEST · Detected from public metadata{pkce}";
    }

    public static string BrowserObservationLine(AltinnEnvironmentView view) =>
        view.Fields.Any(field => field.Group == "Browser authentication" && field.State == AltinnSettingState.Observed) ? "Recorded" : "Not recorded";

    /// <summary>Automated execution summary: "5 settings missing: Client ID, Redirect URI, Scopes, Enabled, Client secret" or "Complete".</summary>
    public static string AutomatedSettingsLine(AltinnEnvironmentView view)
    {
        var open = view.Fields.Where(field => field.Group == "Automated execution" && AutomatedOpen(field)).Select(ShortLabel).ToList();
        return open.Count == 0 ? "Complete" : $"{open.Count} setting{(open.Count == 1 ? "" : "s")} missing: {string.Join(", ", open)}";
    }

    public static AltinnReadinessState SafetyState(AltinnEnvironmentView view) =>
        view.Fields.Where(field => field.Group == "Execution safety").Any(Unresolved) ? AltinnReadinessState.Blocked : AltinnReadinessState.Ready;

    /// <summary>"App approved: Yes · Trusted target: Missing".</summary>
    public static string SafetyLine(AltinnEnvironmentView view)
    {
        string State(string key) => view.Fields.FirstOrDefault(field => field.Key == key) is { } field ? Unresolved(field) ? "Missing" : "Yes" : "Unknown";
        return $"App approved: {State("app-approval")} · Trusted target: {State("trusted-environment")}";
    }

    /// <summary>"reporter-a (Confirmed)" per configured identity of a role, or "Missing".</summary>
    public static string IdentityLine(AltinnEnvironmentView view, string role)
    {
        var items = view.Identities.Where(item => item.Role == role).ToList();
        return items.Count == 0 ? "Missing" : string.Join(" · ", items.Select(item => $"{item.Alias} ({StateLabel(item.State)})"));
    }

    public static readonly (string Key, string Label)[] Groups =
    [
        ("Application", "Application"),
        ("Browser authentication", "Browser authentication"),
        ("Automated execution", "Automated execution"),
        ("Execution safety", "Execution safety"),
    ];
}
