using BirkNext.Integrations;

namespace BirkNext.Api.Services.AltinnTesting;

/// <summary>
/// Authentication per interaction for an Altinn app test context. Three hops, never one "Authentication":
/// <list type="bullet">
/// <item>Reporter → Altinn app: the end user's browser sign-in (ID-porten / TestID). Interactive; nothing stored.</item>
/// <item>Altinn → the integration's consumer (e.g. MU): the backend integration's own mechanism, from the Integration Catalog only.
/// It is Unknown until configured or reviewed — a browser login never says anything about it.</item>
/// <item>BirkNext → Altinn automated execution: BirkNext's own registered client; only automated execution needs it.</item>
/// </list>
/// Each hop keeps requirement, configuration and runtime evidence apart.
/// </summary>
public static class AltinnInteractionAuthentication
{
    public const string ReporterApp = "reporter-app", AppIntegration = "app-integration", Execution = "execution";

    public static IReadOnlyList<InteractionAuthentication> Build(bool production, AltinnBrowserAuthenticationObservation? recordedBrowserLogin,
        IntegrationDefinition? integration, string? integrationLabel, AltinnReadinessState executionState)
    {
        var provider = production ? "ID-porten" : "ID-porten TEST / TestID";
        var reporter = new InteractionAuthentication
        {
            Key = ReporterApp, Title = "Reporter access", From = "Reporter", To = "Altinn app", Purpose = "The reporter signs in and submits the report in the Altinn app.",
            Requirement = InteractionAuthRules.Resolve(
            [
                new(InteractionAuthRequirement.BrowserOidc, InteractionAuthSource.TestPackProposal, "Altinn app submission: end users sign in through ID-porten before submitting"),
                recordedBrowserLogin is null ? null
                    : new(InteractionAuthRequirement.BrowserOidc, InteractionAuthSource.RuntimeSuggestion, $"Recorded browser login · {recordedBrowserLogin.ObservedAt:yyyy-MM-dd}"),
            ]),
            Provider = provider,
            Configuration = "Interactive browser sign-in by the reporter; no credential is stored by BirkNext.",
            Runtime = recordedBrowserLogin is null ? "Not observed" : $"Recorded manually · {recordedBrowserLogin.ObservedAt:yyyy-MM-dd HH:mm} UTC",
            RequiredFor = "Manual preparation and submission",
            ActionLabel = "View browser evidence", ActionTarget = "browser-discovery",
        };

        // The integration's own mechanism only. Producer and consumer authentication are both explicit configuration: if they disagree, that is a
        // conflict to review. The reporter's browser provider is never a candidate here.
        var producer = integration is null ? null : InteractionAuthRules.FromMechanism(integration.ProducerAuthentication);
        var consumer = integration is null ? null : InteractionAuthRules.FromMechanism(integration.ConsumerAuthentication);
        var resolution = InteractionAuthRules.Resolve(
        [
            producer is { } p ? new InteractionAuthCandidate(p, InteractionAuthSource.ExplicitConfiguration, "Integration Catalog · producer authentication") : null,
            consumer is { } c ? new InteractionAuthCandidate(c, InteractionAuthSource.ExplicitConfiguration, "Integration Catalog · consumer authentication") : null,
        ]);
        var from = integration?.SourceSystem ?? integration?.Producer ?? "Altinn app";
        var to = integration?.DestinationSystem ?? integration?.Consumer?.DisplayName ?? "integration consumer";
        var app = new InteractionAuthentication
        {
            Key = AppIntegration, Title = "Integration authentication", From = from, To = to,
            Purpose = $"How {from} authenticates when it delivers the report to {to}. Independent of the reporter's browser sign-in.",
            Requirement = resolution,
            Configuration = integration is null ? "No integration selected"
                : producer is null && consumer is null ? $"Not configured in Integration Catalog ({integrationLabel ?? integration.DisplayName})"
                : $"Configured in Integration Catalog ({integrationLabel ?? integration.DisplayName})",
            Runtime = "Not verified",
            RequiredFor = "End-to-end delivery verification (not needed to prepare a manual submission)",
            ActionLabel = "Review integration authentication", ActionTarget = "integration-catalog",
        };

        var execution = new InteractionAuthentication
        {
            Key = Execution, Title = "BirkNext execution credentials", From = "BirkNext", To = "Altinn app",
            Purpose = "BirkNext's own registered client for unattended (automated) submission. Never filled from a browser login.",
            Requirement = InteractionAuthRules.Resolve(
            [
                new(InteractionAuthRequirement.BearerOAuthApi, InteractionAuthSource.SourceDeclaration, "BirkNext automated execution: registered ID-porten TEST client"),
            ]),
            Provider = production ? null : "ID-porten TEST (BirkNext's registered client)",
            Configuration = executionState switch
            {
                AltinnReadinessState.Ready => "Configured",
                AltinnReadinessState.Partial => "Partly configured",
                _ => "Not configured",
            },
            Runtime = "Not applicable until automated execution",
            RequiredFor = "Automated execution only",
            ActionLabel = "Configure execution credentials", ActionTarget = "automated",
        };
        return [reporter, app, execution];
    }

    /// <summary>Summary items for the three hops (keys reporter-access, integration-auth and the existing authentication = execution credentials).</summary>
    public static IReadOnlyList<AltinnReadinessItem> Summary(IReadOnlyList<InteractionAuthentication> interactions, AltinnReadinessState executionState)
    {
        var reporter = interactions.First(i => i.Key == ReporterApp);
        var app = interactions.First(i => i.Key == AppIntegration);
        var observed = reporter.Runtime != "Not observed";
        return
        [
            new("reporter-access", "Reporter access", observed ? AltinnReadinessState.Ready : AltinnReadinessState.Partial,
                $"{InteractionAuthRules.Label(reporter.Requirement.Requirement)} · {reporter.Provider} · Runtime: {reporter.Runtime}"),
            new("integration-auth", $"{app.From} → {app.To} authentication",
                app.Requirement.Conflict ? AltinnReadinessState.Partial
                : app.Requirement.Requirement == InteractionAuthRequirement.Unknown ? AltinnReadinessState.NotAssessed : AltinnReadinessState.Ready,
                app.Requirement.Conflict ? $"Conflict: {string.Join(" vs ", app.Requirement.Candidates.Select(c => $"{InteractionAuthRules.Label(c.Requirement)} ({c.SourceLabel})"))} — review authentication"
                : app.Requirement.Requirement == InteractionAuthRequirement.Unknown ? "Unknown — not configured or reviewed for this integration (independent of the reporter's browser sign-in)"
                : $"{InteractionAuthRules.Label(app.Requirement.Requirement)} · {app.Configuration} · Runtime: {app.Runtime}"),
            new("authentication", "BirkNext execution credentials", executionState,
                executionState == AltinnReadinessState.Ready ? "Unattended client complete." : "Unattended client incomplete — only automated execution needs it."),
        ];
    }
}
