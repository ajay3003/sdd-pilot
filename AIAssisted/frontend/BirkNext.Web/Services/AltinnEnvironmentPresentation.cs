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

    /// <summary>A summary item's tone: an undetermined Altinn → MU authentication needs review (amber), everything else by state.</summary>
    public static string Tone(AltinnReadinessItem item) =>
        item.Key == "integration-auth" && item.State == AltinnReadinessState.NotAssessed ? "warn" : Tone(item.State);

    /// <summary>Red only for a blocked capability that is required; amber for review; grey for optional, not verified and observed.</summary>
    public static string Tone(AltinnCapabilityStatus status) => status switch
    {
        AltinnCapabilityStatus.Ready => "ok",
        AltinnCapabilityStatus.Blocked => "bad",
        AltinnCapabilityStatus.NeedsReview => "warn",
        AltinnCapabilityStatus.Observed => "info",
        _ => "muted",
    };

    public static AltinnCapabilityReadiness? Capability(AltinnEnvironmentView view, AltinnCapability capability) =>
        view.Capabilities.FirstOrDefault(item => item.Capability == capability);

    public static string CapabilityLabel(AltinnCapability capability) => capability switch
    {
        AltinnCapability.ManualPreparation => "Manual preparation",
        AltinnCapability.ManualSubmission => "Manual submission",
        AltinnCapability.EndToEndVerification => "End-to-end verification",
        AltinnCapability.AutomatedExecution => "Automated execution",
        _ => "Runtime verification",
    };

    /// <summary>"Affects: Manual preparation · Manual submission" — the capabilities an issue blocks, then those it needs review for.</summary>
    public static string Affects(IEnumerable<AltinnIssue> issues)
    {
        var list = issues.ToList();
        var blocks = list.SelectMany(issue => issue.Blocks).Distinct().OrderBy(c => c).Select(CapabilityLabel).ToList();
        var reviews = list.SelectMany(issue => issue.Reviews).Distinct().Where(c => !list.Any(issue => issue.Blocks.Contains(c))).OrderBy(c => c).Select(CapabilityLabel).ToList();
        return string.Join(" · ", new[] { blocks.Count > 0 ? $"Affects: {string.Join(", ", blocks)}" : null, reviews.Count > 0 ? $"Review for: {string.Join(", ", reviews)}" : null }.OfType<string>());
    }

    /// <summary>
    /// The combined manual-testing state (preparation + submission): Ready when both are; otherwise blocked by exactly their blockers. Manual
    /// testing never depends on BirkNext's unattended credentials, execution safety or the report contract.
    /// </summary>
    public static (string Status, string Tone, int Blockers, string Reason) ManualTesting(AltinnEnvironmentView view)
    {
        var parts = new[] { Capability(view, AltinnCapability.ManualPreparation), Capability(view, AltinnCapability.ManualSubmission) }.OfType<AltinnCapabilityReadiness>().ToList();
        if (parts.Count == 0) return (ReadinessLabel(view.ManualReadiness), Tone(view.ManualReadiness.State), view.ManualMissing.Count, ManualLine(view));
        var blockers = view.Issues.Count(issue => issue.Blocks.Contains(AltinnCapability.ManualPreparation) || issue.Blocks.Contains(AltinnCapability.ManualSubmission));
        return blockers == 0 ? ("Ready", "ok", 0, "Prepare and submit manually now: the reporter signs in interactively. Automated execution is not needed.")
            : ($"Blocked · {blockers} blocker{(blockers == 1 ? "" : "s")}", "bad", blockers,
                "Missing: " + string.Join(", ", view.Issues.Where(issue => issue.Blocks.Contains(AltinnCapability.ManualPreparation) || issue.Blocks.Contains(AltinnCapability.ManualSubmission))
                    .Select(issue => issue.Status == "Missing" ? issue.Label : $"{issue.Label} ({issue.Status.ToLowerInvariant()})")));
    }

    /// <summary>"0 blockers" / "2 blockers · 3 review items" for one capability.</summary>
    public static string Counts(AltinnCapabilityReadiness capability) =>
        string.Join(" · ", new[] { $"{capability.Blockers} blocker{(capability.Blockers == 1 ? "" : "s")}",
            capability.ReviewItems > 0 ? $"{capability.ReviewItems} review item{(capability.ReviewItems == 1 ? "" : "s")}" : null }.OfType<string>());

    public static string Tone(AltinnReadinessState state) => state switch
    {
        AltinnReadinessState.Ready => "ok",
        AltinnReadinessState.Partial => "warn",
        AltinnReadinessState.Blocked => "bad",
        _ => "muted",
    };

    /// <summary>Runtime evidence reads Observed (never verified by observation alone); an integration-auth requirement nobody has determined
    /// reads Unknown — it is a requirement state, not missing runtime verification.</summary>
    public static string ReadinessLabel(AltinnReadinessItem item) =>
        item.Key == "runtime" && item.State == AltinnReadinessState.Partial ? "Observed"
        : item.Key == "integration-auth" && item.State == AltinnReadinessState.NotAssessed ? "Needs review"
        // Reporter access: the attended Browser/OIDC configuration is known; whether a login was observed is runtime evidence, shown apart.
        : item.Key == "reporter-access" && item.State == AltinnReadinessState.Ready ? "Configured"
        // Automated execution nobody requested is optional — never Blocked, never "Not verified".
        : item.Key is "automated" or "authentication" && item.State == AltinnReadinessState.NotAssessed ? "Optional"
        : item.State switch
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
    public enum FixAction { Detect, Application, Automated, Safety, TestData, Contract, ApplySuggested, Access, Runtime }

    /// <summary>Needs attention categories, in display order. Only a blocker of a required capability is red.</summary>
    public static class FixCategories
    {
        public const string BlocksManual = "Blocks manual testing", BlocksAutomated = "Blocks automated execution", BlocksEndToEnd = "Blocks end-to-end verification",
            Review = "Needs review", Runtime = "Runtime not verified", Optional = "Optional setup";
        public static readonly string[] Order = [BlocksManual, BlocksAutomated, BlocksEndToEnd, Review, Runtime, Optional];
    }

    public sealed record FixItem(string Label, string Problem);

    /// <summary>One owner's unresolved required items with the single action that resolves them. <see cref="ApplyKeys"/> lists the pending
    /// suggestions an "Apply suggested settings" action applies (exactly those, nothing else).</summary>
    public sealed record FixGroup(string Key, string Title, string Owner, string Action, FixAction Kind, IReadOnlyList<FixItem> Items)
    {
        public IReadOnlyList<string> ApplyKeys { get; init; } = [];
        /// <summary>Blocked (prevents manual or automated testing) or Review required (incomplete evidence that blocks nothing by itself).</summary>
        public string Severity { get; init; } = ShellSeverity.Blocked;
        public string? Impact { get; init; }
        public string? CountBadge { get; init; }
        public string? Details { get; init; }
        public string? Category { get; init; }
    }

    /// <summary>The shared shell's form of a fix group (the action is reported back by key).</summary>
    public static ShellFixGroup ToShell(FixGroup group) => new(group.Key, group.Title, group.Owner, group.Action, group.Items.Select(item => new ShellFixItem(item.Label, item.Problem)).ToList())
    {
        Href = group.Kind == FixAction.TestData ? TestDataHref : null,
        Action = group.Kind.ToString(),
        ApplyKeys = group.ApplyKeys,
        Severity = group.Severity, Impact = group.Impact, CountBadge = group.CountBadge, Details = group.Details, Category = group.Category,
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

    /// <summary>
    /// Needs attention from the backend's one issue model, grouped by what each issue affects (blocks manual testing, blocks automated execution
    /// — only when automation was requested —, blocks end-to-end verification, needs review, runtime not verified, optional setup) and, within
    /// a category, by the section that resolves it (one owner, one action). Nothing is counted twice and nothing is recounted here.
    /// </summary>
    public static IReadOnlyList<FixGroup> ThingsToFix(AltinnEnvironmentView view)
    {
        var groups = new List<FixGroup>();
        foreach (var category in view.Issues.GroupBy(issue => Category(issue)).OrderBy(group => Array.IndexOf(FixCategories.Order, group.Key)))
            foreach (var section in category.GroupBy(issue => issue.Section))
            {
                var items = section.ToList();
                var (title, actionLabel, kind, applyKeys) = SectionAction(section.Key, items, view);
                var key = groups.Any(group => group.Key == section.Key) ? $"{section.Key}-{Slug(category.Key)}" : section.Key;
                groups.Add(new FixGroup(key, title, items[0].Owner, actionLabel, kind, [.. items.Select(issue => new FixItem(issue.Label, issue.Status))])
                {
                    Category = category.Key, ApplyKeys = applyKeys,
                    Severity = category.Key switch
                    {
                        FixCategories.BlocksManual or FixCategories.BlocksAutomated or FixCategories.BlocksEndToEnd => ShellSeverity.Blocked,
                        FixCategories.Review => ShellSeverity.Review,
                        FixCategories.Runtime => ShellSeverity.NotVerified,
                        _ => ShellSeverity.Optional,
                    },
                    Impact = section.Key == "safety" ? "Required for: Automated execution · Not required for: Manual submission" : Affects(items),
                    CountBadge = items.Count > 1 ? $"{items.Count} items" : null,
                    Details = section.Key == "contract" && view.Contract.Reason.Length > 0 ? view.Contract.Reason : null,
                });
            }
        return groups;
    }

    /// <summary>The Needs attention category of one issue.</summary>
    public static string Category(AltinnIssue issue) => issue.Kind switch
    {
        AltinnIssueKind.Runtime => FixCategories.Runtime,
        AltinnIssueKind.Optional => FixCategories.Optional,
        AltinnIssueKind.Review => FixCategories.Review,
        _ when issue.Blocks.Contains(AltinnCapability.ManualPreparation) || issue.Blocks.Contains(AltinnCapability.ManualSubmission) => FixCategories.BlocksManual,
        _ when issue.Blocks.Contains(AltinnCapability.AutomatedExecution) && !issue.Blocks.Contains(AltinnCapability.EndToEndVerification) => FixCategories.BlocksAutomated,
        _ => FixCategories.BlocksEndToEnd,
    };

    private static string Slug(string text) => new([.. text.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]);

    /// <summary>The single action that resolves a section's issues (deep link to the exact section, Test Data, or applying suggestions).</summary>
    private static (string Title, string Action, FixAction Kind, IReadOnlyList<string> ApplyKeys) SectionAction(string section, IReadOnlyList<AltinnIssue> items, AltinnEnvironmentView view)
    {
        switch (section)
        {
            case "application":
                var suggested = items.Where(issue => issue.Status == "Not applied" && view.Fields.Any(field => field.Key == issue.Key && field.Proposal is { Value: not null })).Select(issue => issue.Key).ToList();
                if (suggested.Count > 0 && suggested.Count == items.Count) return ("Application", "Apply suggested settings", FixAction.ApplySuggested, suggested);
                return !view.Detected && items.Any(issue => issue.Status == "Missing") ? ("Application", "Detect settings", FixAction.Detect, []) : ("Application", "Review application", FixAction.Application, []);
            case "identities": return ("Test identities", "Manage", FixAction.TestData, []);
            case "testdata": return ("Test data", "Manage", FixAction.TestData, []);
            case "access": return ("Altinn → MU integration", "Review integration", FixAction.Access, []);
            case "automated": return ("Automated authentication", "Configure", FixAction.Automated, []);
            case "safety": return ("Execution safety", "Review safety", FixAction.Safety, []);
            case "contract": return ("Report contract", "Review contract", FixAction.Contract, []);
            default: return ("Runtime evidence", "Record observation", FixAction.Runtime, []);
        }
    }

    public static int FixCount(IReadOnlyList<FixGroup> groups) => groups.Sum(group => group.Items.Count);

    /// <summary>"2 blockers · 1 review item" (null when nothing needs attention): issues are counted per card, never per setting; optional setup
    /// and runtime evidence are neither blockers nor review items.</summary>
    public static string? FixLine(IReadOnlyList<FixGroup> groups)
    {
        var blockers = groups.Count(group => group.Severity == ShellSeverity.Blocked);
        var reviews = groups.Count(group => group.Severity == ShellSeverity.Review);
        if (blockers + reviews == 0) return null;
        return string.Join(" · ", new[] { blockers > 0 ? $"{blockers} blocker{(blockers == 1 ? "" : "s")}" : null, reviews > 0 ? $"{reviews} review item{(reviews == 1 ? "" : "s")}" : null }.OfType<string>());
    }

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
        string Join(string section) => string.Join(" · ", view.Issues.Where(issue => issue.Section == section && issue.Kind is AltinnIssueKind.Blocker or AltinnIssueKind.Review)
            .Select(issue => $"{issue.Label} {issue.Status.ToLowerInvariant()}"));
        if (item.Key == "automated") return Capability(view, AltinnCapability.AutomatedExecution)?.Reason ?? "";
        if (item.Key == "runtime")
        {
            var stages = view.RuntimeStages;
            return stages.Count == 0 ? "" : stages.Any(stage => stage.Observed)
                ? $"Observed: {string.Join(", ", stages.Where(stage => stage.Observed).Select(stage => stage.Label))} · not verified by BirkNext"
                : "No runtime evidence yet · configuration unaffected";
        }
        if (item.State == AltinnReadinessState.Ready) return "";
        return item.Key switch
        {
            "app" => Join("application") is { Length: > 0 } application ? application : item.Reason,
            "identities" => Join("identities"),
            "testdata" => $"XML fixture {PayloadProblem(item)}",
            _ => "",
        };
    }

    /// <summary>The manual readiness line: "Missing: Form data type, Reporter, Child" (the backend reason when nothing is missing).</summary>
    public static string ManualLine(AltinnEnvironmentView view) =>
        view.ManualMissing.Count > 0 ? $"Missing: {string.Join(", ", view.ManualMissing)}" : view.ManualReadiness.Reason;

    /// <summary>The automated readiness line: "2 requirements unresolved" only when automation was requested; otherwise the optional reason.</summary>
    public static string AutomatedLine(AltinnEnvironmentView view) =>
        view.AutomatedBlockers.Count > 0 ? $"{view.AutomatedBlockers.Count} requirement{(view.AutomatedBlockers.Count == 1 ? "" : "s")} unresolved"
        : Capability(view, AltinnCapability.AutomatedExecution)?.Reason ?? view.AutomatedReadiness.Reason;

    /// <summary>Application section summary: "TT02 · buf/skolenaervaer · 2 settings missing".</summary>
    public static string ApplicationLine(AltinnEnvironmentView view)
    {
        string? V(string key) => view.Fields.FirstOrDefault(field => field.Key == key)?.Value;
        var environment = V("environment") is { } env && env.StartsWith("Test", StringComparison.Ordinal) ? "TT02" : V("environment");
        var (missing, pending) = ApplicationGaps(view);
        // The badge says Ready; the line names the app (and only what is open).
        var state = missing + pending == 0 ? null
            : string.Join(" · ", new[] { missing > 0 ? $"{missing} setting{(missing == 1 ? "" : "s")} missing" : null, pending > 0 ? $"{pending} not applied" : null }.OfType<string>());
        return string.Join(" · ", new[] { environment, V("app"), state }.Where(part => !string.IsNullOrEmpty(part)));
    }

    /// <summary>Application row status: Blocked (required setting missing or in conflict), Needs review (only suggested values to apply), Ready.</summary>
    public static (string Label, string Tone) ApplicationStatus(AltinnEnvironmentView view) => ApplicationGaps(view) switch
    {
        ( > 0, _) => ("Blocked", "bad"),
        (_, > 0) => ("Needs review", "warn"),
        _ => ("Ready", "ok"),
    };

    /// <summary>
    /// Access &amp; authentication aggregates the three hops without hiding any: Ready only when reporter access, integration auth and BirkNext's
    /// execution credentials are all ready; otherwise Needs review (an unresolved hop is never masked by a ready one).
    /// </summary>
    public static (string Label, string Tone) AccessStatus(AltinnEnvironmentView view)
    {
        var hops = view.Summary.Where(item => item.Key is "reporter-access" or "integration-auth" or "authentication").ToList();
        return hops.Count > 0 && hops.All(item => item.State == AltinnReadinessState.Ready) ? ("Ready", "ok") : ("Needs review", "warn");
    }

    /// <summary>"Contract identifier known · Contract evidence incomplete" — the long diagnostic stays in the section body.</summary>
    public static string ContractLine(AltinnEnvironmentView view) => view.Contract.State switch
    {
        AltinnReadinessState.Ready => "Contract identifier known · Contract evidence resolved",
        AltinnReadinessState.Partial => "Contract identifier known · Contract evidence incomplete",
        AltinnReadinessState.Blocked => "Contract identifier missing",
        _ => "Not assessed",
    };

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

    /// <summary>
    /// Browser authentication row: the primary state is runtime evidence (Observed from the app's own Browser Discovery evidence, else a
    /// manual observation), independent of public metadata completeness. Provenance is named, never merged.
    /// </summary>
    public static (string Status, string Tone, string Summary) BrowserStatus(AltinnEnvironmentView view, BrowserAuthEvidenceResult discovery)
    {
        var manual = view.Fields.FirstOrDefault(field => field.Group == "Browser authentication" && field.State == AltinnSettingState.Observed);
        var metadata = view.Fields.Any(field => field.Key.StartsWith("idporten-", StringComparison.Ordinal))
            ? BrowserLine(view).Replace("ID-porten TEST · ", "", StringComparison.Ordinal) : "Public metadata not read";
        if (discovery.Usable) return ("Observed", "info", $"ID-porten TEST · Login observed · Source: Browser Discovery · {metadata}");
        if (manual is not null) return ("Observed", "info", $"ID-porten TEST · Login observation recorded · Source: Manual observation ({manual.SourceLabel}) · {metadata}");
        // Runtime absence: the attended Browser/OIDC configuration is still known — "Not observed", never "missing".
        return ("Not observed", "muted", $"ID-porten TEST · Configured (attended login) · No login observed yet · {metadata}");
    }

    /// <summary>Automated execution summary from the issues: "2 settings missing: Client secret, Redirect URI · Disabled" or "Complete". A detected
    /// value counts as found (it is applied through Detected settings) and a deliberately disabled toggle is "Disabled", never missing.</summary>
    public static string AutomatedSettingsLine(AltinnEnvironmentView view)
    {
        var open = view.Issues.Where(issue => issue.Section == "automated" && issue.Key != "enabled").Select(issue => issue.Label).Distinct().ToList();
        var disabled = view.Issues.Any(issue => issue.Key == "enabled") ? "Disabled" : null;
        var line = open.Count == 0 ? "Complete" : $"{open.Count} setting{(open.Count == 1 ? "" : "s")} missing: {string.Join(", ", open)}";
        return disabled is null ? line : $"{line} · {disabled}";
    }

    /// <summary>"Reporter login: Not observed · Report submitted: Not observed · Delivery to MU and downstream: Not verified".</summary>
    public static string RuntimeLine(AltinnEnvironmentView view) =>
        view.RuntimeStages.Count == 0 ? $"Manual observations: {view.Saved.RuntimeObservations.Count} · Verification: Not verified"
            : string.Join(" · ", view.RuntimeStages.Where(stage => stage.Key is "login" or "submission" or "delivery").Select(stage => $"{stage.Label}: {stage.Status}"));

    public sealed record DetectedLine(string Text, bool Found);
    public sealed record DetectedGroup(string Title, IReadOnlyList<DetectedLine> Lines);

    /// <summary>
    /// What Detect Altinn settings found, grouped like the page: application, reporter access, test data, automated execution, Altinn → MU and
    /// contract. Each line says found (✓) or not (○) with its state and source; a public metadata value is never runtime evidence and a secret is
    /// only "available" or "missing".
    /// </summary>
    public static IReadOnlyList<DetectedGroup> DetectedGroups(AltinnEnvironmentView view)
    {
        AltinnSettingField? F(string key) => view.Fields.FirstOrDefault(field => field.Key == key);
        DetectedLine Line(string key, string label)
        {
            var field = F(key);
            var found = field is { Value: not null } && field.State is not (AltinnSettingState.Missing or AltinnSettingState.Unknown);
            return new(found ? $"{label}: {Display(field!)} · {StateLabel(field!)} · {field!.SourceLabel}" : $"{label}: {(field is null ? "not evaluated" : StateLabel(field))}", found);
        }
        var groups = new List<DetectedGroup>
        {
            new("Application", [Line("app", "App ID"), Line("environment", "Environment"), Line("app-base-url", "Host")]),
        };
        var reporter = view.Identities.FirstOrDefault(item => item.Role == "reporter" && item.State is not AltinnSettingState.Missing);
        var metadata = view.Fields.Any(field => field.Key.StartsWith("idporten-", StringComparison.Ordinal));
        var login = view.RuntimeStages.FirstOrDefault(stage => stage.Key == "login");
        groups.Add(new("Reporter access",
        [
            new("Browser/OIDC · ID-porten TEST / TestID (attended login)", true),
            new(metadata ? "Public OIDC metadata read (issuer, endpoints) — not a login" : "Public OIDC metadata not readable", metadata),
            new(reporter is null ? "Reporter asset: missing" : $"Reporter asset: {reporter.Alias}", reporter is not null),
            new($"Runtime login: {login?.Status ?? "Not observed"}", login?.Observed == true),
        ]));
        var child = view.Identities.FirstOrDefault(item => item.Role == "child" && item.State is not AltinnSettingState.Missing);
        var payload = view.Summary.FirstOrDefault(item => item.Key == "testdata");
        groups.Add(new("Test data",
        [
            new(reporter is null ? "Reporter asset: missing" : $"Reporter asset: {reporter.Alias}", reporter is not null),
            new(child is null ? "Child asset: missing" : $"Child asset: {child.Alias}", child is not null),
            new(payload is null ? "Valid report payload: not assessed" : payload.State == AltinnReadinessState.Ready ? "Valid report payload: bound" : $"Valid report payload: {PayloadProblem(payload)}",
                payload?.State == AltinnReadinessState.Ready),
        ]));
        groups.Add(new("Automated execution",
        [
            Line("client-id", "Client ID"), Line("redirect-uri", "Redirect URI"), Line("scopes", "Scopes"),
            new($"Client secret: {(F("client-secret") is { } secret ? StateLabel(secret) : "Missing")}", F("client-secret")?.State is AltinnSettingState.AvailableForSession or AltinnSettingState.AvailableToBackend),
            new($"Automated execution: {F("enabled")?.Value ?? "Disabled"}", view.AutomationRequested),
        ]));
        var mu = view.Summary.FirstOrDefault(item => item.Key == "integration-auth");
        groups.Add(new("Altinn → MU", [Line("integration", "Integration"),
            new(mu is null ? "Authentication: not assessed" : mu.State == AltinnReadinessState.Ready ? $"Authentication: {mu.Reason}" : "Authentication: not determined (needs review)", mu?.State == AltinnReadinessState.Ready)]));
        groups.Add(new("Contract", [.. view.ContractDimensions.Select(dimension => new DetectedLine($"{dimension.Label}: {dimension.Status}", dimension.Present))]));
        return groups;
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
