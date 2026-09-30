using System.Text;
using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>
/// HTML export of an Integration Quality Review result, from the result and its own configuration snapshot only. Keeps "Not
/// assessed" with its reason, evidence provenance, limitations and manual follow-up; carries no secret (the catalog has none).
/// </summary>
public static class IntegrationReviewExport
{
    public static string Build(IntegrationReviewResult result, string? projectName, Func<string[], IEnumerable<string[]>, string> table, Func<string, string> badge, Func<string?, string> esc,
        Func<string, string?, string?, string, string> buildHtml)
    {
        var sb = new StringBuilder();
        var headline = IntegrationReviewResultPresentation.Headline(result);
        foreach (var source in result.SourceSnapshots)
        {
            sb.Append("<section class=\"block\"><h2>Source evidence</h2>");
            sb.Append($"<p>{esc(source.Archive.FileName)} · {source.Status} · SHA-256 {esc(source.Archive.Sha256)} · Commit {esc(source.Commit)} · Analyzer {source.AnalyzerVersion} · {source.AnalyzedAt:u}</p>");
            sb.Append($"<p>Implementation contract derived from source. Developer tests discovered, not executed. {esc(source.DeploymentCorrelation)}</p>");
            sb.Append(table(["Project", "Path", "Classification"], source.Projects.Select(p => new[] { esc(p.Name), esc(p.Path), esc(p.Classification) })));
            sb.Append(table(["Configuration file", "Keys (values excluded)"], source.Configurations.Select(c => new[] { esc(c.File), esc(string.Join(", ", c.Keys)) })));
            sb.Append(table(["Rule", "Source", "Developer test evidence", "Coverage / action"], source.Rules.Select(r =>
            {
                var coverage = source.Coverage.FirstOrDefault(c => c.RuleId == r.Id);
                return new[] { esc($"{r.Kind}: {r.Field} · {r.Requirement} · {r.Behavior} · Operation {r.Operation} · Table {r.Table ?? "Not resolved"} · Type {r.TargetType ?? "Not resolved"} · Null {r.NullBehavior} · Invalid {r.InvalidBehavior} · Fallback {r.Fallback}"),
                    esc($"{r.Confidence} · {r.Symbol} · {r.Location.File}:{r.Location.Line}"),
                    esc(string.Join("; ", source.Tests.Where(t => coverage?.DeveloperTestIds.Contains(t.Id) == true).Select(t => $"{t.Layer}: {t.Class}.{t.Method} · {t.Location.File}:{t.Location.Line} · Test exists; execution unavailable"))),
                    esc($"{string.Join(", ", coverage?.Statuses ?? [])} · {coverage?.MatchReason} · {coverage?.Action}") };
            })));
            sb.Append(table(["Developer test", "Framework/layer", "Assertion / execution", "Location"], source.Tests.Select(t => new[] {
                esc($"{t.Project}: {t.Class}.{t.Method}"), esc($"{t.Framework} / {t.Layer} / {t.Confidence}"), esc($"{t.AssertionIntent} / {t.ExecutionResult}"), esc($"{t.Location.File}:{t.Location.Line}") })));
            foreach (var flow in source.Dataflows) sb.Append($"<p>{esc(flow.Field)} · {flow.Confidence}: {esc(string.Join(" → ", flow.Steps))}. {esc(flow.Gap)}</p>");
            if (source.IntegrationPath is { } path)
            {
                // Integration path from source: field names, contracts and developer-test evidence only — no source file, value or secret.
                sb.Append($"<h3>Integration path</h3><p>{esc(IqrPathPresentation.Chain(path))}. Source-defined; delivery, subscriber processing and deployment correlation not assessed.</p>");
                sb.Append(table(["From", "To", "Mechanism", "Source evidence", "Developer tests", "Runtime evidence"], IqrPathPresentation.Hops(path).Select(h => new[]
                    { esc(h.From), esc(h.To), esc(h.Mechanism), esc(h.Source), esc(h.Developer), esc(h.Runtime) })));
                sb.Append(table(["Contract boundary", "Implementation contract", "Formal schema", "Developer contract tests", "Differences"], path.Boundaries.Select(b => new[]
                    { esc($"{b.From} → {b.To}"), esc(b.ImplementationContract), esc(b.FormalSchema), esc(b.DeveloperContractTests), esc(b.Mismatches.Count == 0 ? "None" : string.Join("; ", b.Mismatches)) })));
                sb.Append(table(["Source field", "Adapter", "Ingestion", "Domain", "Events → Service Bus", "Transformation", "Developer coverage", "Gap"], IqrPathPresentation.Fields(path).Select(f => new[]
                    { esc(f.Field + (f.Sensitive ? " (sensitive by name)" : "")), esc(f.Adapter), esc(f.Ingestion), esc(f.Domain), esc(f.Events), esc(f.Transformation), esc(f.Coverage + " · runtime delivery not assessed"), esc(f.Gap) })));
                var m = path.Minimization;
                sb.Append($"<p><strong>Data minimization (field names only):</strong> sensitive entering {esc(string.Join(", ", m.SensitiveFieldsEntering))}; retained internally {esc(string.Join(", ", m.RetainedInternally))}; emitted raw {esc(m.EmittedRaw.Count == 0 ? "none found" : string.Join(", ", m.EmittedRaw))}; reduced to metadata {esc(string.Join(", ", m.ReducedMetadata))}; raw copies kept internally {esc(m.InternalRawCopies.Count == 0 ? "none found" : string.Join(", ", m.InternalRawCopies))}.</p>");
                sb.Append(table(["Domain event contract", "Topic / subject", "Session id / priority", "Fields", "Formal schema"], path.Events.Select(e => new[]
                    { esc(e.EventType), esc($"{string.Join(", ", e.Topics)} / {string.Join(", ", e.Subjects)}"), esc($"{e.SessionId} / {e.Priority}"),
                      esc(string.Join(", ", e.Fields.Select(f => $"{f.Name} ({IqrPathPresentation.Transformation(f.Transformation)})"))), esc(e.FormalSchema) })));
                if (path.Outbox is { } o)
                    sb.Append($"<p><strong>Outbox (source):</strong> {esc(o.EntityType)} with envelope {esc(o.Envelope)} ({esc(string.Join(", ", o.EnvelopeFields))}); {esc(o.Transaction)}; {esc(o.MessageId)}; dispatcher {esc(o.Dispatcher)}; {esc(o.Retry)}; {esc(o.Ordering)}.</p>");
                if (path.ServiceBus is { } bus)
                    sb.Append($"<p><strong>Service Bus source topology:</strong> {esc(string.Join("; ", bus.Publications.Select(x => $"{x.Entity} ({string.Join(", ", x.Subjects)})")))}; authentication {esc(bus.Authentication)}; configuration keys (values excluded) {esc(string.Join(", ", bus.ConfigurationKeys))}. Source-defined ≠ configured ≠ observed ≠ processed.</p>");
                sb.Append(table(["Source rule", "Developer tests", "BirkNext action"], path.Rules.Select(r => new[]
                    { esc(r.Title), esc(r.DeveloperTestIds.Count == 0 ? "None resolved" : string.Join(", ", r.DeveloperTestIds.Select(id => IqrPathPresentation.TestName(source, id))) + " (exists; execution unavailable)"), esc(r.BirkNextAction) })));
                sb.Append(table(["Gap", "Kind", "Detail"], path.Gaps.Select(g => new[] { esc(g.Title), esc(IqrPathPresentation.GapKind(g.Kind)), esc(g.Detail) })));
                sb.Append("<h3>Source inspected</h3><ul>" + string.Concat(path.Inspected.Select(i => $"<li>{esc(i)}</li>")) + "</ul>");
            }
            sb.Append("<h3>What was not assessed / limitations</h3><ul>");
            foreach (var limitation in source.Limitations) sb.Append($"<li>{esc(limitation)}</li>");
            sb.Append("</ul></section>");
        }
        sb.Append("<section class=\"block\"><h2>Summary</h2><dl>");
        sb.Append($"<dt>Target environment</dt><dd>{esc(result.EnvironmentName)} ({esc(result.EnvironmentId)})</dd>");
        sb.Append($"<dt>Outcome</dt><dd>{esc(headline.Outcome)}</dd>");
        sb.Append($"<dt>Integration systems reviewed</dt><dd>{headline.Systems}</dd><dt>Topics reviewed</dt><dd>{headline.Topics}</dd>");
        sb.Append($"<dt>Domains assessed</dt><dd>{headline.DomainsAssessed} of {headline.DomainsTotal}</dd><dt>Findings</dt><dd>{headline.Findings}</dd>");
        sb.Append($"<dt>Checks not assessed</dt><dd>{headline.NotAssessedChecks}</dd><dt>Evidence freshness</dt><dd>{esc(headline.Freshness)}</dd>");
        sb.Append($"<dt>Evidence sources</dt><dd>{esc(headline.Sources)}</dd><dt>Review window</dt><dd>{esc(headline.Window)}</dd><dt>Completed</dt><dd>{result.CompletedAt:u}</dd></dl></section>\n");

        sb.Append("<section class=\"block\"><h2>Runtime evidence sources</h2>");
        sb.Append(result.EvidenceAdapters.Count == 0 ? "<p>No runtime evidence source was consulted.</p>" :
            table(["Source", "State", "Reason", "Captured"], result.EvidenceAdapters.Select(a => new[]
            {
                esc(a.Adapter), badge(IntegrationReviewLabels.EvidenceState(a.State)), esc(a.Reason), $"{a.CapturedAt:u}",
            })));
        sb.Append("</section>\n");

        // What the run executed and what it did not assess — explicit, from the run itself.
        if (IntegrationReviewResultPresentation.ScopeRecorded(result))
        {
            sb.Append($"<section class=\"block\"><h2>{(result.SourceSnapshots.Count > 0 ? "What was inspected or observed" : "What was tested")}</h2>");
            sb.Append(result.WhatWasTested.Count == 0 ? "<p>No check was executed with runtime evidence in this run; configuration was reviewed.</p>"
                : "<ul>" + string.Concat(result.WhatWasTested.Select(t => $"<li>{esc(t)}</li>")) + "</ul>");
            sb.Append("</section>\n<section class=\"block\"><h2>What was not assessed</h2><ul>");
            foreach (var item in result.WhatWasNotAssessed) sb.Append($"<li>{esc(item)}</li>");
            sb.Append("</ul></section>\n");
        }

        foreach (var eventHub in result.EventHubSnapshot)
        {
            // Event Hub transport: configured vs observed as this run read it (identifiers and counts only — no token, key, SAS or payload).
            sb.Append($"<section class=\"block\"><h2>Event Hubs · configured vs observed · {esc(eventHub.PlatformName)}</h2><dl>");
            sb.Append($"<dt>Captured</dt><dd>{eventHub.CapturedAt:u}</dd>");
            sb.Append($"<dt>Azure runtime</dt><dd>{(eventHub.AzureRuntimeEnabled ? "Enabled" : "Not configured — IntegrationReview:Azure:Enabled is not true (not an integration misconfiguration)")}</dd>");
            sb.Append($"<dt>Namespace</dt><dd>{esc(eventHub.Namespace)} — {esc(IntegrationReviewResultPresentation.NamespaceLine(eventHub))}</dd>");
            sb.Append($"<dt>Consumer groups</dt><dd>{string.Join("<br>", IntegrationReviewResultPresentation.GroupLines(eventHub).Select(esc))}</dd>");
            sb.Append($"<dt>Checkpoints</dt><dd>{string.Join("<br>", IntegrationReviewResultPresentation.CheckpointLines(eventHub).Select(esc))}</dd>");
            foreach (var (label, value) in IntegrationReviewResultPresentation.MonitoringRows(eventHub)) sb.Append($"<dt>{esc(label)}</dt><dd>{esc(value)}</dd>");
            sb.Append($"<dt>Namespace metrics</dt><dd>{esc(IntegrationReviewResultPresentation.MetricsLine(eventHub))}</dd></dl>");
            var hubs = IntegrationReviewResultPresentation.HubRows(eventHub);
            if (hubs.Count > 0)
                sb.Append(table(["Event Hub", "Type", "Configured", "Observed", "Comparison"], hubs.Select(h => new[]
                {
                    esc(h.Hub), esc(h.Kind), esc(h.Configured), esc(h.Observed), badge(EventHubComparisonLabels.State(h.State)),
                })));
            if (eventHub.ConsumerGroups.Count > 0)
                sb.Append(table(["Event Hub", "Expected group", "Provenance", "Observed groups", "Comparison", "Application mapping"], eventHub.ConsumerGroups.Select(g => new[]
                {
                    esc(g.Hub), esc(g.Expected ?? "Not configured"), esc(g.ExpectedProvenance), esc(g.Observed.Count == 0 ? "—" : string.Join(", ", g.Observed)),
                    badge(EventHubComparisonLabels.State(g.State)), esc(g.Mapping),
                })));
            sb.Append("<p>Observed values carry no verdict without a threshold. An observed match is configuration agreeing with Azure, not message flow; an observed consumer group never confirms the application mapping.</p></section>\n");
        }

        if (result.ApplicationMessagingSnapshot is { } messaging && messaging.Applications.Any(a => a.BoundConsumer is not null))
        {
            // Application messaging (Wolverine): the source evidence and runtime reads this run used. Facts and provenance only — no source code,
            // no configuration value, no message body.
            sb.Append("<section class=\"block\"><h2>Application messaging</h2>");
            sb.Append($"<p>Source analyzed {messaging.AnalyzedAt:u} from {esc(string.Join(", ", messaging.Archives.Select(a => $"{a.FileName} (sha256 {a.Sha256[..12]})")))}. Configuration evidence is not runtime evidence.</p>");
            sb.Append(table(["Application", "Bound consumer", "Technology", "Detection", "Handler mapping", "Retry policy", "Outbox", "Error handling", "Runtime processing"],
                messaging.Applications.Where(a => a.BoundConsumer is not null).Select(a =>
                {
                    var runtime = result.ApplicationMessagingRuntime.FirstOrDefault(r => r.ApplicationId == a.ApplicationId);
                    return new[]
                    {
                        esc(a.ApplicationId), esc(a.BoundConsumer), esc(a.Technology.ToString()), badge(ApplicationMessagingLabels.Detection(a.Detection)),
                        badge(ApplicationMessagingLabels.Fact(a.HandlerMapping)), badge(ApplicationMessagingLabels.Fact(a.RetryPolicy)), badge(ApplicationMessagingLabels.Fact(a.Outbox)),
                        badge(ApplicationMessagingLabels.Fact(a.ErrorHandling)),
                        runtime is { State: BirkNext.Integrations.IntegrationEvidenceState.Available } ? badge("Observed") : $"{badge("Not assessed")} {esc(runtime?.Reason ?? "No runtime read in this run.")}",
                    };
                })));
            sb.Append(table(["Application", "Fact", "State", "Detail", "Provenance"], messaging.Applications.Where(a => a.BoundConsumer is not null)
                .SelectMany(a => a.Facts.Select(f => new[]
                {
                    esc(a.ApplicationId), esc(f.Label), badge(ApplicationMessagingLabels.Fact(f.State)), esc(f.Detail),
                    esc($"{IntegrationReviewLabels.Source(f.Source)}{(f.Locations.Count == 0 ? "" : ": " + string.Join(", ", f.Locations.Take(3).Select(l => $"{l.File}:{l.Line}")))}"),
                }))));
            sb.Append("</section>\n");
        }

        foreach (var serviceBus in result.ServiceBusSnapshot)
        {
            // Service Bus transport: configured topology, runtime metadata and route correlation as recorded by this run. No payload, no secret.
            sb.Append($"<section class=\"block\"><h2>Service Bus · {esc(serviceBus.Namespace)}</h2>");
            sb.Append($"<p>{badge(ServiceBusLabels.Overall(serviceBus.OverallState))} Configured topology: {serviceBus.Queues} queue(s), {serviceBus.Topics} topic(s), {serviceBus.Subscriptions} subscription(s). Runtime metadata: {esc(IntegrationReviewLabels.EvidenceState(serviceBus.Runtime?.State ?? IntegrationEvidenceState.NotConfigured))}{(serviceBus.Runtime?.State == IntegrationEvidenceState.Available ? $", captured {serviceBus.Runtime.CapturedAt:u}" : $" — {esc(serviceBus.Runtime?.Reason)}")}.</p>");
            sb.Append($"<p>Code routes: {esc(ServiceBusPresentation.RouteStatus(serviceBus).Label)}. Azure Monitor metrics: {esc(IntegrationReviewLabels.EvidenceState(serviceBus.Metrics?.State ?? IntegrationEvidenceState.NotSupported))}"
                + (serviceBus.Metrics is { State: IntegrationEvidenceState.Available } m
                    ? $" (last {m.WindowHours} h, captured {m.CapturedAt:u}): " + string.Join(", ", ServiceBusPresentation.MetricRows.Select(r => $"{esc(r.Label)} {esc(ServiceBusPresentation.Metric(m, r.Metric, r.Aggregation))}")) + ". Observed with no threshold."
                    : $" — {esc(serviceBus.Metrics?.Reason ?? "not read in this run")}.") + " Oldest-message age: not available.</p>");
            sb.Append(table(["Check", "Expected", "Observed", "State", "Detail"], serviceBus.Configuration.Concat(serviceBus.RuntimeChecks).Select(c => new[]
            {
                esc(c.Title), esc(c.Expected ?? "—"), esc(c.Observed ?? "—"), badge(ServiceBusLabels.State(c.State)), esc(c.Detail),
            })));
            sb.Append(table(["Application", "Technology", "Direction", "Entity", "Configuration", "Access", "Runtime entity", "Handler execution"], serviceBus.Routes.Select(r => new[]
            {
                esc(r.Application), esc(r.Technology), esc(r.Direction), esc($"{r.Entity} ({r.EntitySource})"), badge(ServiceBusLabels.State(r.Configuration)),
                badge(ServiceBusLabels.State(r.Access)), badge(ServiceBusLabels.State(r.Runtime)), badge("Not assessed"),
            })));
            if (serviceBus.Missing.Count > 0) sb.Append("<ul>" + string.Concat(serviceBus.Missing.Select(m => $"<li>{esc(m)}</li>")) + "</ul>");
            sb.Append("</section>\n");
        }

        // SCIM identity provisioning: stages, checks, findings and the specification comparison as recorded by this run. No token or user data.
        foreach (var scim in result.ScimSnapshot) sb.Append(ScimExport.Section(scim, table, badge, esc));

        sb.Append("<section class=\"block\"><h2>Contract snapshot</h2>");
        sb.Append(result.ContractSnapshot.Count == 0 ? "<p>No contract artifact was configured for this run.</p>" :
            table(["Integration", "Role", "File", "Version", "Fields", "SHA-256"], result.ContractSnapshot.Select(c => new[]
            {
                esc(result.ConfigurationSnapshot.Integrations.FirstOrDefault(i => i.Id == c.IntegrationId)?.DisplayName ?? c.IntegrationId), esc(c.Role.ToString()), esc(c.FileName),
                esc(c.Version ?? "—"), c.FieldCount.ToString(), esc(c.ShortHash),
            })));
        sb.Append("</section>\n");

        foreach (var platform in result.ConfigurationSnapshot.Platforms)
        {
            sb.Append($"<section class=\"block\"><h2>Platform · {esc(platform.Name)}</h2><dl>");
            sb.Append($"<dt>Namespace</dt><dd>{esc(platform.Namespace ?? "Not configured")} ({esc(platform.NamespaceFqdn ?? "—")})</dd>");
            sb.Append($"<dt>Resource group</dt><dd>{esc(platform.ResourceGroup ?? "Not configured")}</dd>");
            sb.Append($"<dt>Producer</dt><dd>{esc(platform.ProducerTechnology ?? "Not configured")} · auth {esc(IntegrationConfigurationRules.AuthLabel(platform.ProducerAuthentication))}</dd>");
            sb.Append($"<dt>Default consumer auth</dt><dd>{esc(IntegrationConfigurationRules.AuthLabel(platform.DefaultConsumerAuthentication))}</dd>");
            sb.Append($"<dt>Monitoring</dt><dd>{esc(platform.MonitoringProvider ?? "Not configured")} · dashboard {esc(platform.MonitoringUrl ?? "Not configured")}</dd>");
            sb.Append($"<dt>Technical topics (not reviewed)</dt><dd>{esc(string.Join(", ", platform.TechnicalTopics.Select(t => t.Name)))}</dd></dl></section>\n");
        }

        sb.Append("<section class=\"block\"><h2>Selected topics</h2>");
        sb.Append(table(["Integration", "Topic", "Consumer", "Mapping", "Consumer group", "Configuration"], result.ConfigurationSnapshot.Integrations.Where(i => i.Enabled).Select(i => new[]
        {
            esc(i.DisplayName), esc(i.EndpointOrTopic ?? "Not configured"), esc(i.Consumer.DisplayName ?? "Needs confirmation"),
            esc(IntegrationConfigurationRules.MappingLabel(i.Consumer.MappingState)), esc(i.ConsumerGroup ?? "Unknown / not configured"),
            esc(IntegrationConfigurationRules.Label(IntegrationConfigurationRules.Evaluate(i, result.ConfigurationSnapshot.Platforms.FirstOrDefault(p => p.Id == i.PlatformId)).State)),
        })));
        sb.Append("</section>\n");

        sb.Append("<section class=\"block\"><h2>Domain coverage</h2>");
        sb.Append(table(["Domain", "State", "Coverage", "Findings", "Observed", "Missing"], result.Domains.Select(d => new[]
        {
            esc(IntegrationReviewLabels.Domain(d.Domain)), esc(d.StateLabel), esc(IntegrationReviewResultPresentation.Coverage(d)), d.Findings.ToString(),
            esc(string.Join("; ", d.Observed)), esc(d.Missing.Count > 0 ? string.Join("; ", d.Missing) : d.KeyLimitation ?? ""),
        })));
        sb.Append("</section>\n");

        foreach (var domain in result.Domains.Select(d => d.Domain))
        {
            var rows = IntegrationReviewResultPresentation.Rows(result, domain);
            if (rows.Count == 0) continue;
            sb.Append($"<section class=\"block\"><h2>{esc(IntegrationReviewLabels.Domain(domain))} checks</h2>");
            sb.Append(table(["Subject", "Check", "Status", "Evidence", "Explanation", "Provenance", "Freshness"], rows.Select(r => new[]
            {
                esc(r.Subject), esc(r.Check.Title), badge(IntegrationReviewLabels.Status(r.Check.Status)), esc(r.Check.Evidence), esc(r.Check.Explanation),
                esc($"{IntegrationReviewLabels.Source(r.Check.Provenance)} · {r.Check.CapturedAt:u}"), esc(IntegrationReviewResultPresentation.Freshness(r.Check)),
            })));
            sb.Append("</section>\n");
        }

        sb.Append("<section class=\"block\"><h2>Findings</h2>");
        sb.Append(result.Findings.Count == 0 ? "<p>No findings on the assessed checks. Not-assessed checks are listed above, never as passes.</p>" :
            table(["Severity", "Domain", "Subject", "Finding", "Evidence", "Recommendation", "Affected"], result.Findings.Select(f => new[]
            {
                badge(f.Severity.ToString()), esc(IntegrationReviewLabels.Domain(f.Domain)), esc(f.Subject), esc(f.Title), esc(string.Join("; ", f.Evidence)), esc(f.Recommendation),
                f.AffectedIntegrations.Count.ToString(),
            })));
        sb.Append("</section>\n");

        sb.Append("<section class=\"block\"><h2>Manual follow-up</h2><ul>");
        foreach (var item in result.ManualFollowUp) sb.Append($"<li><strong>{esc(item.Title)}</strong> ({item.AffectedCount}) — {esc(item.Detail)}</li>");
        sb.Append("</ul></section>\n<section class=\"block\"><h2>Limitations</h2><ul>");
        foreach (var limitation in result.Limitations) sb.Append($"<li>{esc(limitation)}</li>");
        sb.Append("</ul></section>\n");

        return buildHtml("Integration Quality Review", projectName, $"Environment: {result.EnvironmentName}  Completed: {result.CompletedAt:yyyy-MM-dd HH:mm} UTC", sb.ToString());
    }
}
