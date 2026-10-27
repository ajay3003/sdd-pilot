using System.Text.Json;
using BirkNext.Api.Services.ActiveEventTesting;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.Integrations;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.AltinnTesting;

/// <summary>
/// Target Environment → Connected Systems → Altinn: resolves every Altinn value of one environment with its state and provenance, and assesses manual testing and
/// automated BirkNext execution separately. Precedence for non-secret values: the Target Environment's saved settings, the backend's own
/// configuration, the Integration Quality Review binding, Source Analysis, stored public metadata, the test pack's declared default. Explicit
/// sources that disagree are a Conflict; a declared default never conflicts with or overrides an explicit value. Reading and detecting never
/// write; Detect additionally reads ID-porten TEST's public discovery document in memory (no authentication, no persistence). Browser login
/// observations are evidence of Altinn's own portal client and never fill the unattended client. No secret, token or identifier is returned.
/// Form data type and content type may also be suggested by a test pack's built-in reference whose scope is exactly the resolved app (e.g.
/// the Skolenærvær pack's OpenAPI facts for buf/skolenaervaer): a suggestion with provenance, never an override, and nothing when the app
/// is unresolved, the references disagree or the app's own contract evidence is ambiguous.
/// </summary>
public sealed class AltinnEnvironmentService(AltinnEnvironmentSettingsStore store, IIntegrationMessageFlowStore messageFlows, AltinnTestingPolicy policy,
    AltinnAppContractService contracts, IIntegrationCatalogService catalog, ITrustedExecutionEnvironmentRegistry trusted, IHttpClientFactory http,
    IEnumerable<IIntegrationJourneyPack>? packs = null)
{
    public const string FormDataTypeKey = "Application.FormDataType";
    public const string ContentTypeKey = "Payload.ContentType";

    public const string ApplicationGroup = "Application";
    public const string BrowserGroup = "Browser authentication";
    public const string AutomatedGroup = "Automated execution";
    public const string SafetyGroup = "Execution safety";
    public const string IntegrationName = "Skolenærvær – Altinn report intake";

    public async Task<AltinnEnvironmentView> ViewAsync(string environmentId, bool detect, CancellationToken ct)
    {
        var saved = await store.GetAsync(environmentId, ct);
        var package = await messageFlows.GetAsync(environmentId, ct);
        var config = package.AltinnConfiguration with { TargetEnvironmentId = environmentId };
        var record = package.AppContract;
        var fields = new List<AltinnSettingField>();
        var notes = new List<string>();

        // ── Application ──
        var environment = Resolve("environment", ApplicationGroup, "Altinn environment", AltinnRequirement.Manual,
            [Candidate(saved.Environment is { } e ? Env(e) : null, AltinnSettingState.Configured, AltinnSettingSource.TargetEnvironment),
             Candidate(config.Environment is ExternalTestEnvironment.Unknown ? null : Env(config.Environment), AltinnSettingState.Detected, AltinnSettingSource.IntegrationBinding),
             Candidate(Env(SkoleNaervaerAltinnDefaults.Environment), AltinnSettingState.Declared, AltinnSettingSource.TestPack)],
            "Altinn app testing runs only in TT02 (Test); Production is refused by name on every request.");
        fields.Add(environment);

        var app = Resolve("app", ApplicationGroup, "App identifier", AltinnRequirement.Manual,
            [Candidate(saved.AppIdentifier, AltinnSettingState.Configured, AltinnSettingSource.TargetEnvironment),
             Candidate(config.AppIdentifier?.Trim(), AltinnSettingState.Detected, AltinnSettingSource.IntegrationBinding),
             Candidate(record?.SourceAnalysis?.AppId, AltinnSettingState.Detected, AltinnSettingSource.SourceAnalysis),
             Candidate(record?.PublicMetadata?.AppId, AltinnSettingState.Detected, AltinnSettingSource.PublicMetadata),
             Candidate(SkoleNaervaerAltinnDefaults.AppIdentifier, AltinnSettingState.Declared, AltinnSettingSource.TestPack)], "");
        var approved = policy.ApprovedApp(app.Value, out var approvalReason);
        fields.Add(app with { Detail = (app.State == AltinnSettingState.Conflict ? app.Detail + " " : "") + approvalReason
            + (app.State == AltinnSettingState.Detected ? " Detected from source evidence is not deployed runtime." : "") });

        string? derivedBase = null;
        try { derivedBase = approved is { } a ? policy.Endpoints.AppBase(a.Org, a.App).ToString().TrimEnd('/') : null; }
        catch (AltinnContractException ex) { notes.Add(ex.Message); }
        fields.Add(Resolve("app-base-url", ApplicationGroup, "App base URL", AltinnRequirement.Manual,
            [Candidate(saved.AppBaseUrl, AltinnSettingState.Configured, AltinnSettingSource.TargetEnvironment),
             Candidate(config.BaseUrlReference?.Trim().TrimEnd('/'), AltinnSettingState.Detected, AltinnSettingSource.IntegrationBinding),
             Candidate(derivedBase, AltinnSettingState.Detected, AltinnSettingSource.BackendConfiguration, "Derived from the app id and the configured TT02 apps domain")],
            "Where the app is served. Configured or derived, not verified: reachability is a separate runtime verification."));

        var contract = contracts.Evaluate(environmentId, app.Value, record);
        // The app's own contract evidence names several form data types without choosing one: ambiguous, so no suggestion may paper over it.
        var ambiguousForm = new[] { record?.SourceAnalysis, record?.PublicMetadata }
            .Any(snapshot => snapshot is { FormDataTypeId: null, FormDataTypeCandidates.Count: > 1 });
        var appResolved = app.State != AltinnSettingState.Conflict ? app.Value : null;
        var formReference = ambiguousForm ? null : ReferenceFact(appResolved, FormDataTypeKey, notes);
        var contentReference = ambiguousForm ? null : ReferenceFact(appResolved, ContentTypeKey, notes);
        var form = Resolve("form-data-type", ApplicationGroup, "Form data type", AltinnRequirement.Manual,
            [Candidate(saved.FormDataType, AltinnSettingState.Configured, AltinnSettingSource.TargetEnvironment),
             Candidate(record?.SourceAnalysis?.FormDataTypeId, AltinnSettingState.Detected, AltinnSettingSource.SourceAnalysis),
             Candidate(record?.PublicMetadata?.FormDataTypeId, AltinnSettingState.Detected, AltinnSettingSource.PublicMetadata),
             Candidate(formReference?.Value, AltinnSettingState.Declared, AltinnSettingSource.TestPack, formReference?.Label)],
            "The form contract the report is written against. Detected or suggested values are not verified in TT02.");
        if (ambiguousForm && form.State == AltinnSettingState.Missing)
            form = form with { Detail = "The app's contract evidence names several form data types and does not choose one. Choose one deliberately (nothing is selected automatically)." };
        fields.Add(form);
        fields.Add(Resolve("content-type", ApplicationGroup, "Content type", AltinnRequirement.Automated,
            [Candidate(saved.ContentType, AltinnSettingState.Configured, AltinnSettingSource.TargetEnvironment),
             Candidate(contract.SubmissionContentType, AltinnSettingState.Detected, record?.PublicMetadata is not null ? AltinnSettingSource.PublicMetadata : AltinnSettingSource.SourceAnalysis),
             Candidate(contentReference?.Value, AltinnSettingState.Declared, AltinnSettingSource.TestPack, contentReference?.Label)],
            "How BirkNext writes the form data when it submits automatically. A manual browser submission does not need it."));

        var integrations = await IntegrationsAsync(environmentId, ct);
        var bound = integrations.FirstOrDefault(item => item.DisplayName == IntegrationName || item.Id.Contains("report-intake", StringComparison.Ordinal));
        var integration = Resolve("integration", ApplicationGroup, "Integration", AltinnRequirement.Manual,
            [Candidate(saved.IntegrationId, AltinnSettingState.Configured, AltinnSettingSource.TargetEnvironment),
             Candidate(bound?.Id, AltinnSettingState.Detected, AltinnSettingSource.IntegrationBinding)],
            $"{IntegrationName}: Altinn → Meldingsutveksleren (MU). Referenced from Target Environment → Integration Catalog, never copied.");
        if (integration.Value is { } integrationId && integrations.Count > 0 && integrations.All(item => item.Id != integrationId))
            integration = integration with { State = AltinnSettingState.Missing, Detail = $"The integration {integrationId} is not configured in this environment (Target Environment → Integration Catalog)." };
        fields.Add(IntegrationReferenceNames.Apply(integration, integrations));

        // ── Browser authentication: evidence of Altinn's own login client (observed), plus ID-porten public metadata on Detect ──
        if (saved.BrowserAuthentication is { } browser)
        {
            var source = $"{browser.EvidenceSource} · {browser.ObservedAt:yyyy-MM-dd}";
            foreach (var (key, label, value) in new[]
            {
                ("browser-issuer", "Issuer", browser.Issuer), ("browser-authorize", "Authorization endpoint", browser.AuthorizationEndpoint),
                ("browser-acr", "Authentication level", browser.AuthenticationLevel), ("browser-client", "Observed browser client ID", browser.ClientId),
                ("browser-redirect", "Observed browser redirect URI", browser.RedirectUri), ("browser-scopes", "Observed browser scopes", browser.Scopes.Count == 0 ? null : string.Join(" ", browser.Scopes)),
                ("browser-pkce", "PKCE method", browser.PkceMethod),
            }.Where(item => !string.IsNullOrWhiteSpace(item.Item3)))
                fields.Add(new AltinnSettingField
                {
                    Key = key, Group = BrowserGroup, Label = label, Value = value, State = AltinnSettingState.Observed, Source = AltinnSettingSource.RuntimeObservation,
                    SourceLabel = source, Requirement = AltinnRequirement.Optional,
                    Detail = "Observed for Altinn's browser login client. It is not BirkNext's unattended client and never configures it.",
                });
        }
        else
            fields.Add(new AltinnSettingField
            {
                Key = "browser-auth", Group = BrowserGroup, Label = "Browser login observation", State = AltinnSettingState.Unknown, Source = AltinnSettingSource.None,
                SourceLabel = "No runtime evidence", Requirement = AltinnRequirement.Optional, Detail = "No browser login to Altinn has been recorded for this environment.",
            });
        if (detect) fields.AddRange(await IdPortenMetadataAsync(notes, ct));

        // ── Automated execution: BirkNext's own registered client (never the browser client) ──
        policy.ClientId(config, out _, out var clientProblem);
        fields.Add(Resolve("client-id", AutomatedGroup, "Client ID", AltinnRequirement.Automated,
            [Candidate(saved.Unattended.ClientId, AltinnSettingState.Configured, AltinnSettingSource.TargetEnvironment),
             Candidate(policy.Options.UnattendedIdPortenTest.ClientId?.Trim(), AltinnSettingState.Configured, AltinnSettingSource.BackendConfiguration),
             Candidate(config.ClientReference?.Trim(), AltinnSettingState.Detected, AltinnSettingSource.IntegrationBinding)],
            "The registered ID-porten TEST client BirkNext uses for unattended execution. Not read from browser traffic."));
        fields.Add(Resolve("redirect-uri", AutomatedGroup, "Registered redirect URI", AltinnRequirement.Automated,
            [Candidate(saved.Unattended.RedirectUri, AltinnSettingState.Configured, AltinnSettingSource.TargetEnvironment),
             Candidate(policy.Options.UnattendedIdPortenTest.RedirectUri?.Trim(), AltinnSettingState.Configured, AltinnSettingSource.BackendConfiguration),
             Candidate(policy.Options.IdPortenTest.RedirectUri?.Trim(), AltinnSettingState.Declared, AltinnSettingSource.BackendConfiguration, "Backend sign-in callback (fallback)")],
            "The redirect URI registered for the unattended client. A browser redirect URI observed for Altinn's login is never this value."));
        var scopes = policy.Scopes(config);
        fields.Add(Resolve("scopes", AutomatedGroup, "Scopes", AltinnRequirement.Automated,
            [Candidate(saved.Unattended.Scopes.Count == 0 ? null : string.Join(" ", saved.Unattended.Scopes), AltinnSettingState.Configured, AltinnSettingSource.TargetEnvironment),
             Candidate(config.ConfiguredScopes.Count == 0 ? null : string.Join(" ", config.ConfiguredScopes.Select(item => item.Trim())), AltinnSettingState.Detected, AltinnSettingSource.IntegrationBinding)],
            scopes.Contains("openid", StringComparer.Ordinal) || scopes.Count == 0 ? "Not checked against the client registration: BirkNext cannot see it." : "The unattended authentication requires openid."));
        var enabled = policy.UnattendedEnabled(config);
        fields.Add(new AltinnSettingField
        {
            Key = "enabled", Group = AutomatedGroup, Label = "Automated execution", Value = enabled ? "Enabled" : "Not enabled", Requirement = AltinnRequirement.Automated,
            State = enabled ? AltinnSettingState.Configured : AltinnSettingState.Missing,
            Source = saved.Unattended.Enabled is not null ? AltinnSettingSource.TargetEnvironment : AltinnSettingSource.BackendConfiguration,
            SourceLabel = saved.Unattended.Enabled is not null ? Label(AltinnSettingSource.TargetEnvironment) : Label(AltinnSettingSource.BackendConfiguration),
            Detail = "An explicit choice per environment. Trust, the execution policy and the run acknowledgement still apply.",
        });
        var secret = policy.SecretState(config);
        fields.Add(new AltinnSettingField
        {
            Key = "client-secret", Group = AutomatedGroup, Label = "Client secret", Requirement = AltinnRequirement.Automated, State = secret,
            Source = secret == AltinnSettingState.AvailableForSession ? AltinnSettingSource.Session : secret == AltinnSettingState.AvailableToBackend ? AltinnSettingSource.BackendConfiguration : AltinnSettingSource.None,
            SourceLabel = secret switch { AltinnSettingState.AvailableForSession => "This backend session (memory only)", AltinnSettingState.AvailableToBackend => "Backend process configuration", _ => "Not available" },
            Detail = secret == AltinnSettingState.Missing ? "Add it for this session when automated execution needs it. Never persisted, logged or returned." : "The value is never shown.",
        });

        // ── Execution safety (backend-owned trust; nothing here is writable through the API) ──
        var trust = trusted.Resolve(environmentId, out var trustReason);
        var targetBlock = AltinnTestingPolicy.TargetBlock(trust, trustReason);
        fields.Add(new AltinnSettingField
        {
            Key = "trusted-environment", Group = SafetyGroup, Label = "Trusted execution environment", Requirement = AltinnRequirement.Automated,
            State = targetBlock is null ? AltinnSettingState.Configured : AltinnSettingState.Missing, Source = AltinnSettingSource.BackendConfiguration,
            SourceLabel = "Backend trust configuration", Value = trust is null ? null : $"{trust.EnvironmentType} · execution {(trust.ExecutionAllowed ? "allowed" : "not allowed")}",
            Detail = targetBlock ?? "Non-production, trusted, execution explicitly allowed. Trust is backend configuration only and is never enrolled from the UI.",
        });
        fields.Add(new AltinnSettingField
        {
            Key = "app-approval", Group = SafetyGroup, Label = "App approved for testing", Requirement = AltinnRequirement.Automated,
            State = approved is null ? AltinnSettingState.Missing : AltinnSettingState.Configured, Source = AltinnSettingSource.BackendConfiguration,
            SourceLabel = "Backend approved apps", Value = approved is null ? null : app.Value, Detail = approvalReason,
        });

        var identities = Identities();
        var view = Assess(environmentId, saved, fields, identities, config, contract, targetBlock, approved is not null, clientProblem);
        return view with { Detected = detect, Notes = notes, Contract = ContractItem(contract) };
    }

    /// <summary>The value one semantic key has in the built-in test pack references scoped exactly to <paramref name="appId"/>, with a provenance
    /// label. Null when the app is unresolved, no reference covers it, or references disagree (noted, never chosen).</summary>
    private (string Value, string Label)? ReferenceFact(string? appId, string semanticKey, List<string> notes)
    {
        if (string.IsNullOrWhiteSpace(appId) || packs is null) return null;
        var facts = new List<(string Value, string Label)>();
        foreach (var reference in packs.SelectMany(pack => pack.References).Where(reference => string.Equals(reference.Scope, appId, StringComparison.OrdinalIgnoreCase)))
        {
            var (view, loaded) = TestPackReferences.Load(reference);
            if (!loaded) continue;
            facts.AddRange(view.Facts.Where(fact => fact.SemanticKey == semanticKey && !string.IsNullOrWhiteSpace(fact.Value))
                .Select(fact => (fact.Value.Trim(), $"{TestPackReferences.ProvenanceLabel} · {reference.Kind}")));
        }
        var values = facts.Select(fact => fact.Value).Distinct(StringComparer.Ordinal).ToList();
        if (values.Count > 1)
        {
            notes.Add($"Test Pack references disagree on {semanticKey} for {appId} ({string.Join(", ", values)}); nothing is suggested.");
            return null;
        }
        return values.Count == 1 ? facts[0] : null;
    }

    private static AltinnReadinessItem ContractItem(AltinnAppContractView contract)
    {
        var sources = contract.Sources.Select(source => source.Source switch
        {
            AltinnContractSourceKind.SourceAnalysisSnapshot => "Source Analysis",
            AltinnContractSourceKind.PublicTt02Metadata => "Public TT02 metadata",
            AltinnContractSourceKind.DeployedOpenApi => "Deployed OpenAPI",
            _ => "Imported OpenAPI",
        }).Distinct().ToList();
        var from = sources.Count == 0 ? "No contract evidence" : string.Join(" · ", sources);
        return contract.Complete ? new("contract", "Report contract", AltinnReadinessState.Ready, from)
            : sources.Count > 0 ? new("contract", "Report contract", AltinnReadinessState.Partial, $"{from} · missing: {string.Join("; ", contract.Missing.DefaultIfEmpty("not confirmed"))}")
            : new("contract", "Report contract", AltinnReadinessState.Blocked, "No contract evidence yet. Read the public TT02 metadata or use the app's Source Analysis.");
    }

    private AltinnEnvironmentView Assess(string environmentId, AltinnEnvironmentSettings saved, List<AltinnSettingField> fields, IReadOnlyList<AltinnIdentitySummary> identities,
        AltinnTestConfiguration config, AltinnAppContractView contract, string? targetBlock, bool appApproved, string? clientProblem)
    {
        AltinnSettingField F(string key) => fields.First(field => field.Key == key);
        var environmentValue = F("environment");
        var production = environmentValue.Value?.StartsWith("Production", StringComparison.Ordinal) == true;
        var reporter = identities.Any(item => item.Role == AltinnTestDataProvider.Reporter && item.State is AltinnSettingState.Configured or AltinnSettingState.Confirmed);
        var confirmedReporter = identities.Any(item => item.Role == AltinnTestDataProvider.Reporter && item.State == AltinnSettingState.Confirmed);
        var child = identities.Any(item => item.Role == AltinnTestDataProvider.Child && item.State == AltinnSettingState.Configured);

        // Manual testing needs the TT02 app, a reporter, a child, the integration and the contract — never the unattended client. A suggested
        // value (a test pack default or reference) is not configuration: it satisfies nothing until it is applied. Detected values keep their
        // meaning — they come from the IQR binding or the app's own contract evidence, which BirkNext already uses.
        var manualGaps = new List<string>();
        var manualMissing = new List<string>();
        foreach (var key in new[] { "environment", "app", "form-data-type", "integration" })
            if (!Satisfied(F(key)))
            {
                var problem = Problem(F(key));
                manualGaps.Add($"{F(key).Label}: {problem}");
                manualMissing.Add(problem == "missing" ? F(key).Label : $"{F(key).Label} ({problem})");
            }
        if (!reporter) { manualGaps.Add("Reporter: missing"); manualMissing.Add("Reporter"); }
        if (!child) { manualGaps.Add("Synthetic child: missing"); manualMissing.Add("Child"); }
        var manual = production
            ? new AltinnReadinessItem("manual", "Manual test readiness", AltinnReadinessState.Blocked, "The environment is Production; Altinn testing never runs there.")
            : manualGaps.Count == 0
                ? new AltinnReadinessItem("manual", "Manual test readiness", AltinnReadinessState.Ready, "TT02 app, reporter, child, integration and contract are known.")
                : new AltinnReadinessItem("manual", "Manual test readiness", AltinnReadinessState.Partial, string.Join(" · ", manualGaps));

        // Automated execution additionally needs the unattended client, the secret, trust and approval; every reason stays explicit.
        var blockers = new List<string>(manualGaps);
        if (production) blockers.Add("Production is never executable.");
        blockers.AddRange(fields.Where(field => field.Requirement == AltinnRequirement.Automated && field.State is AltinnSettingState.Conflict or AltinnSettingState.Declared)
            .Select(field => $"{field.Label}: {Problem(field)}"));
        blockers.AddRange(policy.UnattendedProblems(config));
        if (targetBlock is not null) blockers.Add(targetBlock);
        if (!appApproved) blockers.Add(F("app-approval").Detail);
        if (!contract.Complete) blockers.Add($"Contract: incomplete ({string.Join("; ", contract.Missing.DefaultIfEmpty("not confirmed"))}).");
        if (reporter && !confirmedReporter) blockers.Add("Reporter TT02 profile: not confirmed.");
        blockers = [.. blockers.Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.Ordinal)];
        var automated = new AltinnReadinessItem("automated", "Automated BirkNext execution", blockers.Count == 0 ? AltinnReadinessState.Ready : AltinnReadinessState.Blocked,
            blockers.Count == 0 ? "Ready for dry run. A live run still needs an explicit acknowledgement; at most one process transition, no automatic retry."
                : $"Blocked by {blockers.Count} requirement{(blockers.Count == 1 ? "" : "s")}: {blockers[0]}");

        var appState = production ? AltinnReadinessState.Blocked
            : new[] { "environment", "app", "app-base-url" }.All(key => Satisfied(F(key))) ? AltinnReadinessState.Ready : AltinnReadinessState.Partial;
        var automatedFields = fields.Where(field => field.Group == AutomatedGroup).ToList();
        var authState = automatedFields.All(field => field.State is AltinnSettingState.Configured or AltinnSettingState.AvailableForSession or AltinnSettingState.AvailableToBackend)
            ? AltinnReadinessState.Ready : automatedFields.Any(field => field.State is AltinnSettingState.Configured or AltinnSettingState.Detected or AltinnSettingState.AvailableForSession or AltinnSettingState.AvailableToBackend)
            ? AltinnReadinessState.Partial : AltinnReadinessState.Blocked;
        var observations = saved.RuntimeObservations;
        var summary = new List<AltinnReadinessItem>
        {
            new("app", "App", appState, appState == AltinnReadinessState.Ready ? $"{F("environment").Value} · {F("app").Value}" : "Environment, app or base URL is missing, in conflict or only suggested (not applied)."),
            new("authentication", "Authentication", authState, authState == AltinnReadinessState.Ready ? "Unattended client complete." : "Unattended client incomplete — only automated execution needs it."),
            new("identities", "Test identities", reporter && child ? AltinnReadinessState.Ready : reporter || child ? AltinnReadinessState.Partial : AltinnReadinessState.Blocked,
                $"Reporter {(reporter ? confirmedReporter ? "configured · profile confirmed" : "configured · profile not confirmed" : "missing")} · child {(child ? "configured" : "missing")}"),
            new("runtime", "Runtime", observations.Count > 0 ? AltinnReadinessState.Partial : AltinnReadinessState.NotAssessed,
                observations.Count > 0 ? $"Observed ({observations.Count} observation{(observations.Count == 1 ? "" : "s")}); not verified by BirkNext." : "Not verified."),
            new("automated", "Automated execution", automated.State, automated.Reason),
        };

        var required = fields.Where(field => field.Requirement != AltinnRequirement.Optional).ToList();
        var gaps = required.Where(field => field.State is AltinnSettingState.Missing or AltinnSettingState.Conflict or AltinnSettingState.Declared)
            .Select(field => $"{field.State switch { AltinnSettingState.Conflict => "Conflict", AltinnSettingState.Declared => "Not applied", _ => "Missing" }}: {field.Label}{(field.Requirement == AltinnRequirement.Automated ? " (automated execution)" : "")}").ToList();
        if (clientProblem is not null && !gaps.Any(gap => gap.Contains("Client ID", StringComparison.Ordinal))) gaps.Add($"Conflict: Client ID (automated execution)");
        var detection = new AltinnDetectionSummary(
            fields.Count(field => field.State == AltinnSettingState.Configured), fields.Count(field => field.State is AltinnSettingState.Detected or AltinnSettingState.Declared),
            fields.Count(field => field.State == AltinnSettingState.Observed) + observations.Count, required.Count(field => field.State == AltinnSettingState.Missing),
            fields.Count(field => field.State == AltinnSettingState.Conflict), gaps);

        return new AltinnEnvironmentView
        {
            EnvironmentId = environmentId, Saved = saved, Fields = fields, Identities = identities, Summary = summary,
            ManualReadiness = manual, AutomatedReadiness = automated, AutomatedBlockers = blockers, Detection = detection,
            ManualMissing = production ? [] : manualMissing,
        };
    }

    /// <summary>Whether a required value counts: configured, confirmed, verified or detected from evidence BirkNext already uses. A suggestion
    /// (Declared) does not count until applied; missing, unknown and conflicting values never do.</summary>
    public static bool Satisfied(AltinnSettingField field) =>
        field.Value is not null && field.State is not (AltinnSettingState.Missing or AltinnSettingState.Unknown or AltinnSettingState.Conflict or AltinnSettingState.Declared);

    private static string Problem(AltinnSettingField field) => field.State switch
    {
        AltinnSettingState.Conflict => "conflict",
        AltinnSettingState.Declared => "not applied",
        _ => "missing",
    };

    /// <summary>Reporter and child availability from Test Data (aliases and states only; identifiers never leave the backend).</summary>
    private IReadOnlyList<AltinnIdentitySummary> Identities()
    {
        var list = new List<AltinnIdentitySummary>();
        var provider = new AltinnTestDataProvider(policy.Options);
        foreach (var reporter in policy.Options.UnattendedIdPortenTest.Reporters.Where(item => !string.IsNullOrWhiteSpace(item.Alias)))
        {
            var problem = AltinnTestingPolicy.ReporterConfigurationProblem(reporter);
            list.Add(new(AltinnTestDataProvider.Reporter, reporter.Alias, string.IsNullOrWhiteSpace(reporter.Label) ? reporter.Alias : reporter.Label,
                problem is not null ? AltinnSettingState.Missing : reporter.AltinnProfileConfirmed ? AltinnSettingState.Confirmed : AltinnSettingState.Configured,
                reporter.SessionExpiresAt is null ? "Backend configuration" : "Test Data · this backend session",
                problem ?? (reporter.AltinnProfileConfirmed ? "Configured reporter · TT02 profile confirmed (a human confirmation, not a login)." : "Configured reporter · TT02 profile not confirmed.")));
        }
        foreach (var child in policy.Options.SyntheticChildren.Where(item => !string.IsNullOrWhiteSpace(item.Alias)))
        {
            var problem = provider.ChildProblem(child);
            list.Add(new(AltinnTestDataProvider.Child, child.Alias, string.IsNullOrWhiteSpace(child.Label) ? child.Alias : child.Label,
                problem is null ? AltinnSettingState.Configured : AltinnSettingState.Missing, child.SessionExpiresAt is null ? "Backend configuration" : "Test Data · this backend session",
                problem ?? (child.KnownToPerson ? "Declared known to Person; not verified by BirkNext." : "Declared unknown to Person; not verified by BirkNext.")));
        }
        return list;
    }

    private async Task<IReadOnlyList<IntegrationDefinition>> IntegrationsAsync(string environmentId, CancellationToken ct)
    {
        try { return (await catalog.GetAsync(environmentId, null, null, ct)).Integrations; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return []; }
    }

    /// <summary>ID-porten TEST's public discovery document, read anonymously and kept in memory. Proves no client registration, redirect, secret or login.</summary>
    private async Task<IReadOnlyList<AltinnSettingField>> IdPortenMetadataAsync(List<string> notes, CancellationToken ct)
    {
        const string source = "ID-porten TEST public discovery";
        const string detail = "Public metadata. It does not prove a client registration, a registered redirect URI, a valid secret, a login or a scope entitlement.";
        try
        {
            var client = http.CreateClient(AltinnTestingRequestGuard.ClientName);
            using var response = await client.GetAsync(AltinnTestingPolicy.IdPortenTestDiscovery, ct);
            if (!response.IsSuccessStatusCode) { notes.Add($"ID-porten TEST public metadata: Unknown (HTTP {(int)response.StatusCode})."); return []; }
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = json.RootElement;
            string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            string? List(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
                ? string.Join(" ", value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString())) : null;
            return new (string Key, string Label, string? Value)[]
            {
                ("idporten-issuer", "ID-porten issuer", Text("issuer")), ("idporten-authorize", "Authorization endpoint", Text("authorization_endpoint")),
                ("idporten-token", "Token endpoint", Text("token_endpoint")), ("idporten-par", "PAR endpoint", Text("pushed_authorization_request_endpoint")),
                ("idporten-response-types", "Supported response types", List("response_types_supported")), ("idporten-pkce", "Supported PKCE methods", List("code_challenge_methods_supported")),
            }.Where(item => !string.IsNullOrWhiteSpace(item.Value)).Select(item => new AltinnSettingField
            {
                Key = item.Key, Group = BrowserGroup, Label = item.Label, Value = item.Value, State = AltinnSettingState.Detected, Source = AltinnSettingSource.PublicMetadata,
                SourceLabel = source, Requirement = AltinnRequirement.Optional, Detail = detail,
            }).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or AltinnContractException)
        {
            if (ct.IsCancellationRequested) throw;
            notes.Add("ID-porten TEST public metadata: Unknown (not reachable or not readable). Nothing else is affected.");
            return [];
        }
    }

    private static string Env(ExternalTestEnvironment environment) => environment == ExternalTestEnvironment.Test ? "Test (TT02)" : environment.ToString();

    private static AltinnSettingCandidate Candidate(string? value, AltinnSettingState state, AltinnSettingSource source, string? label = null) =>
        new(string.IsNullOrWhiteSpace(value) ? null : value.Trim(), state, source, label ?? Label(source));

    public static string Label(AltinnSettingSource source) => source switch
    {
        AltinnSettingSource.TargetEnvironment => "Target Environment",
        AltinnSettingSource.Session => "This backend session",
        AltinnSettingSource.BackendConfiguration => "Backend configuration",
        AltinnSettingSource.IntegrationBinding => "Integration Quality Review",
        AltinnSettingSource.SourceAnalysis => "Source Analysis",
        AltinnSettingSource.PublicMetadata => "Stored public metadata",
        AltinnSettingSource.RuntimeObservation => "Runtime observation",
        AltinnSettingSource.TestPack => "Skolenærvær test pack",
        _ => "None",
    };

    /// <summary>
    /// The highest-precedence candidate wins; any other explicit or detected candidate with a different value makes the field a Conflict
    /// (shown with every candidate, never resolved silently). A declared test-pack default never conflicts: it only fills an absent value
    /// as a suggestion. A winner that is not the Target Environment's saved value is offered as a proposal to apply.
    /// </summary>
    public static AltinnSettingField Resolve(string key, string group, string label, AltinnRequirement requirement, IEnumerable<AltinnSettingCandidate> candidates, string detail)
    {
        var present = candidates.Where(candidate => candidate.Value is not null).ToList();
        var winner = present.FirstOrDefault();
        if (winner is null)
            return new AltinnSettingField { Key = key, Group = group, Label = label, Requirement = requirement, State = AltinnSettingState.Missing, Source = AltinnSettingSource.None, SourceLabel = "Not found", Detail = detail };
        var comparable = present.Where(candidate => candidate.State != AltinnSettingState.Declared).ToList();
        var conflict = comparable.Select(candidate => candidate.Value).Distinct(StringComparer.Ordinal).Count() > 1;
        var savedWins = winner.Source == AltinnSettingSource.TargetEnvironment;
        var proposal = conflict ? comparable.FirstOrDefault(candidate => candidate.Value != winner.Value && candidate.Source != AltinnSettingSource.TargetEnvironment)
            : savedWins ? null : winner;
        return new AltinnSettingField
        {
            Key = key, Group = group, Label = label, Requirement = requirement, Value = winner.Value,
            State = conflict ? AltinnSettingState.Conflict : winner.State, Source = winner.Source, SourceLabel = winner.SourceLabel,
            Detail = conflict ? $"Sources disagree: {string.Join("; ", comparable.Select(candidate => $"{candidate.SourceLabel}: {candidate.Value}"))}. Review and apply one value deliberately." : detail,
            Candidates = present, Proposal = proposal,
        };
    }
}
