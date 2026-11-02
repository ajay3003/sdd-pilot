namespace BirkNext.Integrations;

/// <summary>
/// The one Altinn requirement model. Every unresolved requirement becomes one <see cref="AltinnIssue"/> naming exactly the capabilities it blocks
/// or needs review for; capability readiness, counts, Needs attention, sections and Advanced details all read these issues, so a count can never
/// disagree with a list. Scoping rules:
/// <list type="bullet">
/// <item>Manual preparation / submission: environment, app, reporter, child and the bound valid report payload. The reporter signs in
/// interactively, so no BirkNext credential, execution safety or contract dimension blocks it.</item>
/// <item>End-to-end delivery verification: additionally the integration (Altinn → MU) — its authentication and the report contract are review items.</item>
/// <item>Automated execution: BirkNext's unattended client, secret, execution safety, the confirmed reporter profile and the complete contract.
/// It is required only when automated execution was explicitly enabled; otherwise those items are optional setup, never blockers.</item>
/// <item>Runtime verification: observed stages only; an observation proves its own stage and nothing downstream.</item>
/// </list>
/// </summary>
public static class AltinnCapabilityModel
{
    private static readonly AltinnCapability[] Manual =
        [AltinnCapability.ManualPreparation, AltinnCapability.ManualSubmission, AltinnCapability.EndToEndVerification, AltinnCapability.AutomatedExecution];
    private static readonly AltinnCapability[] Automated = [AltinnCapability.AutomatedExecution];
    private static readonly AltinnCapability[] EndToEnd = [AltinnCapability.EndToEndVerification];
    private const string TargetOwner = "Target Environment · Altinn", ClientOwner = "Target Environment · Altinn unattended client", PolicyOwner = "Backend execution policy",
        TestDataOwner = "Test Data", ContractOwner = "IQR / Altinn contract evidence", CatalogOwner = "Integration Catalog", RuntimeOwner = "Runtime evidence";

    /// <summary>Interaction hop keys (mirrors the backend's AltinnInteractionAuthentication).</summary>
    public const string ReporterAppHop = "reporter-app", AppIntegrationHop = "app-integration";

    /// <summary>Whether a required value counts: configured, confirmed, verified or detected from evidence BirkNext already uses. A suggestion
    /// (Declared) does not count until applied; missing, unknown and conflicting values never do.</summary>
    public static bool Satisfied(AltinnSettingField field) =>
        field.Value is not null && field.State is not (AltinnSettingState.Missing or AltinnSettingState.Unknown or AltinnSettingState.Conflict or AltinnSettingState.Declared);

    public sealed record Inputs(IReadOnlyList<AltinnSettingField> Fields, bool Production, bool Reporter, bool ConfirmedReporter, bool Child,
        (bool Ready, string Problem)? Payload, AltinnAppContractView Contract, IReadOnlyList<InteractionAuthentication> Interactions,
        IReadOnlyList<AltinnRuntimeObservation> Observations, bool AutomationRequested, IReadOnlyList<string> Scopes);

    public sealed record Result(IReadOnlyList<AltinnIssue> Issues, IReadOnlyList<AltinnCapabilityReadiness> Capabilities,
        IReadOnlyList<AltinnContractDimension> ContractDimensions, IReadOnlyList<AltinnRuntimeStage> RuntimeStages);

    public static Result Build(Inputs input)
    {
        var issues = new List<AltinnIssue>();
        AltinnSettingField? F(string key) => input.Fields.FirstOrDefault(field => field.Key == key);
        void Add(string key, string label, string status, string section, string owner, string detail, IReadOnlyList<AltinnCapability> blocks,
            IReadOnlyList<AltinnCapability>? reviews = null) =>
            issues.Add(new AltinnIssue { Key = key, Label = label, Status = status, Section = section, Owner = owner, Detail = detail, Blocks = blocks, Reviews = reviews ?? [] });
        void Field(string key, string section, string owner, IReadOnlyList<AltinnCapability> blocks, IReadOnlyList<AltinnCapability>? reviews = null)
        {
            if (F(key) is { } field && !Satisfied(field)) Add(key, field.Label, Status(field), section, owner, field.Detail, blocks, reviews);
        }

        // ── Manual preparation and submission (and therefore everything after them) ──
        if (input.Production)
            Add("production", "Altinn environment", "Production", "application", TargetOwner, "The environment is Production; Altinn testing never runs there.",
                [.. Manual, AltinnCapability.RuntimeVerification]);
        Field("environment", "application", TargetOwner, Manual);
        Field("app", "application", TargetOwner, Manual);
        if (!input.Reporter) Add("reporter", "Reporter identity", "Missing", "identities", TestDataOwner, "Who signs in and submits: a reporter in Test Data.", Manual);
        if (!input.Child) Add("child", "Child identity", "Missing", "identities", TestDataOwner, "The synthetic child the report is about, in Test Data.", Manual);
        if (input.Payload is { Ready: false } payload)
            Add("payload", "Valid report payload", Capitalize(payload.Problem), "testdata", TestDataOwner, $"What is submitted: XML fixture {payload.Problem}.", Manual);

        // ── End-to-end delivery verification (Altinn → MU and further) ──
        Field("integration", "access", CatalogOwner, EndToEnd);
        if (input.Interactions.FirstOrDefault(hop => hop.Key == AppIntegrationHop) is { } hop
            && (hop.Requirement.Conflict || hop.Requirement.Requirement == InteractionAuthRequirement.Unknown))
            Add("integration-auth", $"{hop.From} → {hop.To} authentication", hop.Requirement.Conflict ? "Conflict" : "Unknown", "access", CatalogOwner,
                "How the app authenticates when it delivers the report — independent of the reporter's browser sign-in and of BirkNext's client. Not needed to prepare or submit a report manually.",
                [], EndToEnd);

        // ── Report contract: dimension by dimension (automated execution needs it complete; end-to-end verification reviews it) ──
        var dimensions = ContractDimensions(input.Contract, F("form-data-type"));
        foreach (var dimension in dimensions.Where(item => !item.Present))
            Add($"contract-{dimension.Key}", dimension.Label, dimension.Status, "contract", ContractOwner, dimension.Detail,
                dimension.Key == "deployed" ? [] : Automated, EndToEnd);
        var covered = dimensions.Any(item => !item.Present && item.Key != "deployed");
        if (!input.Contract.Complete && !covered)
            Add("contract-other", "Contract operations", "Incomplete", "contract", ContractOwner, string.Join(" ", input.Contract.Missing), Automated, EndToEnd);

        // ── Automated execution only ──
        Field("app-base-url", "application", TargetOwner, Automated);
        Field("content-type", "automated", ClientOwner, Automated);
        Field("client-id", "automated", ClientOwner, Automated);
        Field("redirect-uri", "automated", ClientOwner, Automated);
        Field("scopes", "automated", ClientOwner, Automated);
        if (F("scopes") is { } scopes && Satisfied(scopes) && !input.Scopes.Contains("openid", StringComparer.Ordinal))
            Add("scopes-openid", "Scopes", "Incomplete", "automated", ClientOwner, "The unattended authentication requires the openid scope.", Automated);
        if (F("client-secret") is { State: AltinnSettingState.Missing } secret)
            Add("client-secret", secret.Label, "Missing", "automated", ClientOwner,
                "Add a secure credential (backend configuration) or a secret for this backend session. Never discovered, stored in Test Data or shown.", Automated);
        if (!input.AutomationRequested)
            Add("enabled", "Automated execution", "Disabled", "automated", ClientOwner,
                "Automated BirkNext execution is off for this environment — a deliberate, valid state. Manual testing never needs it.", Automated);
        Field("trusted-environment", "safety", PolicyOwner, Automated);
        Field("app-approval", "safety", PolicyOwner, Automated);
        if (input.Reporter && !input.ConfirmedReporter)
            Add("reporter-profile", "Reporter TT02 profile", "Not confirmed", "identities", TestDataOwner,
                "BirkNext submits as the reporter only after a person confirmed the synthetic reporter's TT02 profile. A manual submission does not need it.", Automated);

        // ── Runtime evidence (never configuration) ──
        var stages = RuntimeStages(input.Observations, input.Interactions);
        foreach (var stage in stages.Where(item => !item.Observed))
            issues.Add(new AltinnIssue
            {
                Key = $"runtime-{stage.Key}", Label = stage.Label, Status = stage.Status, Section = "runtime", Owner = RuntimeOwner, Detail = stage.Detail,
                Reviews = [AltinnCapability.RuntimeVerification], Kind = AltinnIssueKind.Runtime,
            });

        var classified = issues.Select(issue => issue.Kind == AltinnIssueKind.Runtime ? issue : issue with { Kind = Kind(issue, input.AutomationRequested) }).ToList();
        return new(classified, Capabilities(classified, input, stages), dimensions, stages);
    }

    /// <summary>Blocker when it blocks a required capability; Optional when it blocks only automated execution while automation is not requested;
    /// otherwise a review item.</summary>
    private static AltinnIssueKind Kind(AltinnIssue issue, bool automationRequested) =>
        issue.Blocks.Any(capability => capability != AltinnCapability.AutomatedExecution || automationRequested) ? AltinnIssueKind.Blocker
        : issue.Blocks.Count > 0 && issue.Reviews.Count == 0 ? AltinnIssueKind.Optional
        : AltinnIssueKind.Review;

    private static IReadOnlyList<AltinnCapabilityReadiness> Capabilities(IReadOnlyList<AltinnIssue> issues, Inputs input, IReadOnlyList<AltinnRuntimeStage> stages)
    {
        int Blocking(AltinnCapability capability) => issues.Count(issue => issue.Blocks.Contains(capability));
        int Reviewing(AltinnCapability capability) => issues.Count(issue => issue.Reviews.Contains(capability) && !issue.Blocks.Contains(capability));
        static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";
        AltinnCapabilityReadiness Manual(AltinnCapability capability, string label, string ready)
        {
            var blockers = Blocking(capability);
            return new(capability, label, blockers == 0 ? AltinnCapabilityStatus.Ready : AltinnCapabilityStatus.Blocked, blockers == 0 ? "Ready" : $"Blocked · {Count(blockers, "blocker")}",
                blockers, 0, blockers == 0 ? ready : string.Join(" · ", issues.Where(issue => issue.Blocks.Contains(capability)).Select(issue => $"{issue.Label}: {issue.Status}")));
        }

        var preparation = Manual(AltinnCapability.ManualPreparation, "Manual preparation",
            "App, reporter, child and the valid report payload are available. BirkNext's unattended credentials are not needed.");
        var submission = Manual(AltinnCapability.ManualSubmission, "Manual submission",
            "The reporter signs in interactively (Browser/OIDC · ID-porten TEST / TestID) and submits in the Altinn app.");

        var e2eBlockers = Blocking(AltinnCapability.EndToEndVerification);
        var e2eReviews = Reviewing(AltinnCapability.EndToEndVerification);
        var endToEnd = new AltinnCapabilityReadiness(AltinnCapability.EndToEndVerification, "End-to-end verification",
            e2eBlockers > 0 ? AltinnCapabilityStatus.Blocked : e2eReviews > 0 ? AltinnCapabilityStatus.NeedsReview : AltinnCapabilityStatus.Ready,
            e2eBlockers > 0 ? $"Blocked · {Count(e2eBlockers, "blocker")}" : e2eReviews > 0 ? $"Needs review · {Count(e2eReviews, "review item")}" : "Ready to verify",
            e2eBlockers, e2eReviews,
            "Delivery beyond Altinn (MU, MM, downstream) needs the integration, its authentication and the report contract reviewed. It never blocks manual preparation.");

        var automationBlockers = Blocking(AltinnCapability.AutomatedExecution) - (input.AutomationRequested ? 0 : 1); // "Disabled" is not a blocker of an unrequested capability
        var automated = input.AutomationRequested
            ? new AltinnCapabilityReadiness(AltinnCapability.AutomatedExecution, "Automated BirkNext execution",
                automationBlockers == 0 ? AltinnCapabilityStatus.Ready : AltinnCapabilityStatus.Blocked,
                automationBlockers == 0 ? "Ready" : $"Blocked · {Count(automationBlockers, "blocker")}", automationBlockers, 0,
                automationBlockers == 0 ? "Ready for a dry run. A live run still needs an explicit acknowledgement." : $"{Count(automationBlockers, "requirement")} unresolved for automated execution.")
            : new AltinnCapabilityReadiness(AltinnCapability.AutomatedExecution, "Automated BirkNext execution", AltinnCapabilityStatus.Optional,
                automationBlockers == 0 ? "Optional · Disabled" : "Optional · Not configured", 0, 0,
                automationBlockers == 0 ? "Everything automated execution needs is in place; it is disabled for this environment."
                    : $"Not enabled for this environment. If enabled, {Count(automationBlockers, "requirement")} would still be unresolved. Manual testing does not need it.")
            { Required = false };

        var observed = stages.Where(stage => stage.Observed).ToList();
        var runtime = new AltinnCapabilityReadiness(AltinnCapability.RuntimeVerification, "Runtime verification",
            observed.Count == 0 ? AltinnCapabilityStatus.NotVerified : AltinnCapabilityStatus.Observed,
            observed.Count == 0 ? "Not verified" : $"Observed · {Count(observed.Count, "stage")}", 0, stages.Count - observed.Count,
            observed.Count == 0 ? "No runtime evidence yet. Configuration readiness is unaffected."
                : $"Observed: {string.Join(", ", observed.Select(stage => stage.Label))}. An observation proves only its own stage; it is not a BirkNext verification.");
        return [preparation, submission, endToEnd, automated, runtime];
    }

    /// <summary>The report contract per dimension, from the evaluated contract (never from one aggregate "complete" flag).</summary>
    public static IReadOnlyList<AltinnContractDimension> ContractDimensions(AltinnAppContractView contract, AltinnSettingField? formDataType)
    {
        const string affects = "Automated execution · End-to-end verification";
        var list = new List<AltinnContractDimension>();
        var identifierKnown = formDataType is not null && Satisfied(formDataType);
        list.Add(new("identifier", "Contract identifier", identifierKnown ? "Known" : formDataType?.State == AltinnSettingState.Conflict ? "Conflict" : "Not confirmed", identifierKnown,
            identifierKnown ? $"{formDataType!.Value} · {formDataType.SourceLabel}" : "The form data type is not confirmed; only activities that need the exact form contract are affected.") { Affects = identifierKnown ? "" : affects });
        foreach (var (kind, key, label) in new[]
        {
            (AltinnContractPartKind.ApplicationMetadata, "metadata", "Application metadata"), (AltinnContractPartKind.FormDataType, "form-data-type", "Form data type"),
            (AltinnContractPartKind.DataModelSchema, "schema", "Data model schema"), (AltinnContractPartKind.ProcessDefinition, "process", "Process contract"),
        })
        {
            var part = contract.Parts.FirstOrDefault(item => item.Part == kind);
            var present = part?.Present == true;
            list.Add(new(key, label, present ? "Known" : kind == AltinnContractPartKind.ProcessDefinition ? "Not confirmed" : "Missing", present,
                part?.Detail ?? "Not evaluated.") { Affects = present ? "" : affects });
        }
        var read = contract.Freshness is not AltinnContractFreshness.NotRead;
        list.Add(new("deployed", "Deployed contract", contract.Freshness switch
        {
            AltinnContractFreshness.NotRead => "Not read",
            AltinnContractFreshness.Changed => "Changed",
            AltinnContractFreshness.RefreshRecommended => "Refresh recommended",
            _ => "Read",
        }, read && contract.Freshness != AltinnContractFreshness.Changed, contract.FreshnessDetail)
        { Affects = read && contract.Freshness != AltinnContractFreshness.Changed ? "" : "End-to-end verification (review)" });
        return list;
    }

    /// <summary>
    /// Runtime stages Altinn observations can establish, plus the reporter's browser login. Downstream stages (MU received, MM processed, the
    /// report stored) are not observable here: they are listed as not verified, never inferred from an Altinn submission.
    /// </summary>
    public static IReadOnlyList<AltinnRuntimeStage> RuntimeStages(IReadOnlyList<AltinnRuntimeObservation> observations, IReadOnlyList<InteractionAuthentication> interactions)
    {
        bool Seen(params AltinnRuntimeObservationKind[] kinds) => observations.Any(item => item.Succeeded && kinds.Contains(item.Kind));
        var reporterRuntime = interactions.FirstOrDefault(hop => hop.Key == ReporterAppHop)?.Runtime ?? "Not observed";
        var login = reporterRuntime != "Not observed" || Seen(AltinnRuntimeObservationKind.ReporterLogin, AltinnRuntimeObservationKind.IdPortenAuthentication);
        var submitted = Seen(AltinnRuntimeObservationKind.Submission);
        var receipt = Seen(AltinnRuntimeObservationKind.Receipt);
        return
        [
            new("login", "Reporter login", login ? "Observed" : "Not observed", login, login ? "The reporter's browser sign-in was observed (evidence, not a BirkNext verification)." : "No reporter login has been observed. Reporter access configuration is unaffected."),
            new("submission", "Report submitted", submitted ? "Observed" : "Not observed", submitted, submitted ? "A report submission in Altinn was observed. It does not prove MU, MM or downstream receipt." : "No report submission observed yet."),
            new("receipt", "Altinn receipt", receipt ? "Observed" : "Not observed", receipt, receipt ? "Altinn generated a receipt." : "No Altinn receipt observed yet."),
            new("delivery", "Delivery to MU and downstream", "Not verified", false, "MU received / MM processed / report stored are not observable from Altinn. Verify them in Integration Quality Review."),
        ];
    }

    private static string Status(AltinnSettingField field) => field.State switch
    {
        AltinnSettingState.Conflict => "Conflict",
        AltinnSettingState.Declared => "Not applied",
        AltinnSettingState.Unknown => "Unknown",
        _ => "Missing",
    };

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
