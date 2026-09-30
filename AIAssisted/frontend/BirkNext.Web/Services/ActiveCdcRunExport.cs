using System.Text;
using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>
/// HTML export of one active CDC run, from the stored run only: status and reason, every stage with its own state and source, the
/// synthetic fixture summary (identifiers, field names and hash — never the payload), the source-contract binding, what was and was not
/// assessed, and limitations. No credential exists in the run to export.
/// </summary>
public static class ActiveCdcRunExport
{
    public static string Build(ActiveCdcRun run, string? projectName, Func<string[], IEnumerable<string[]>, string> table, Func<string, string> badge, Func<string?, string> esc,
        Func<string, string?, string?, string, string> buildHtml)
    {
        var sb = new StringBuilder();
        sb.Append($"<section class=\"block\"><h2>Active test — {esc(run.Scenario.Name)} ({esc(run.Scenario.Id)} v{esc(run.Scenario.Version)})</h2>");
        sb.Append($"<p>{esc(ActiveCdcLabels.ActiveTestNotice)}</p>");
        sb.Append($"<p>Status: {badge(ActiveCdcLabels.Status(run.Status))} — {esc(ActiveCdcPresentation.Headline(run))}. {esc(run.StatusReason)}</p>");
        if (run.Scenario.Category.Length > 0) sb.Append($"<p>Category: {esc(run.Scenario.Category)}. Purpose: {esc(run.Scenario.Description)}</p>");
        if (run.Scenario.Limitation.Length > 0) sb.Append($"<p><strong>{esc(run.Scenario.Limitation)}</strong></p>");
        if (run.Scenario.PassMeaning.Length > 0) sb.Append($"<p>Pass meaning: {esc(run.Scenario.PassMeaning)} <strong>{esc(run.Scenario.PassDoesNotMean)}</strong></p>");
        sb.Append(table(["Environment", "Integration", "Destination", "Consumer group", "Started", "Completed", "Send attempted"], [[
            esc($"{run.EnvironmentName} ({run.EnvironmentType})"), esc(run.IntegrationName), esc($"{run.Destination.EventHub} on {run.Destination.NamespaceFqdn}"),
            esc(run.Destination.ConsumerGroup is null ? "Not configured" : $"{run.Destination.ConsumerGroup}{(run.Destination.ConsumerGroupAssumed ? " (configured assumption)" : "")}"),
            esc($"{run.StartedAt:u}"), esc(run.CompletedAt is { } c ? $"{c:u}" : "—"), run.SendAttempted ? "Yes" : "No"]]));
        sb.Append("</section>\n<section class=\"block\"><h2>Stages (each is separate evidence)</h2>");
        sb.Append(table(["Stage", "State", "Evidence", "Source"], ActiveCdcPresentation.Stages(run).Select(s => new[] { esc(s.Label), esc(s.State), esc(s.Detail), esc(s.Source) })));
        sb.Append("<p>Generated ≠ sent ≠ accepted ≠ consumed ≠ Person persisted ≠ outbox ≠ Service Bus delivered ≠ subscriber processed. Not observed, unavailable and not assessed are not failures.</p></section>\n");
        if (run.Messages.Count > 0)
        {
            sb.Append("<section class=\"block\"><h2>Messages (payloads not stored)</h2>");
            sb.Append(table(["Message", "Role", "Synthetic PersonPK", "Send", "Send evidence", "Partition (post-send position)", "Consumer checkpoint", "Payload"], run.Messages.Select(m => new[]
            {
                esc(m.Label), esc(m.Role), m.SyntheticPersonPk.ToString(System.Globalization.CultureInfo.InvariantCulture), esc(ActiveCdcLabels.State(m.SendState)), esc(m.SendDetail),
                esc(m.AdvancedPartitions.Count == 0 ? "Not located" : string.Join(", ", m.AdvancedPartitions.Select(a => $"{a.Key} → {a.Value}"))),
                esc($"{ActiveCdcLabels.State(m.CheckpointState)} — {m.CheckpointDetail}"), esc($"{m.PayloadBytes} bytes, SHA-256 {m.PayloadSha256}"),
            })));
            sb.Append(table(["Snapshot", "State", "Partitions (last enqueued / checkpoint)"], run.CheckpointSnapshots.Select(c => new[]
            {
                esc(c.Label), esc(ActiveCdcLabels.State(c.State)),
                esc(c.Partitions.Count == 0 ? c.Detail : string.Join("; ", c.Partitions.Select(p => $"{p.PartitionId}: {p.LastEnqueued?.ToString() ?? "unknown"} / {p.Checkpointed?.ToString() ?? "unknown"}"))),
            })));
            sb.Append($"<p>Developer coverage (discovered in the bound source, not executed): {esc(run.Manifest.DeveloperCoverage.Count == 0 ? "none found" : string.Join("; ", run.Manifest.DeveloperCoverage))}. " +
                "Active CDC coverage: the deployed Event Hub → Person Adapter replay-resilience path.</p></section>\n");
        }
        if (run.Messages.Count == 0 && run.Fixture is { } f)
        {
            sb.Append("<section class=\"block\"><h2>Synthetic fixture (payload not stored)</h2>");
            sb.Append(table(["Synthetic PersonPK", "Expected PersonId", "Marker", "Synthetic birth date", "Fields", "Payload"], [[
                f.SyntheticPersonPk.ToString(System.Globalization.CultureInfo.InvariantCulture), esc(f.ExpectedPersonId.ToString()), esc(f.Marker), esc($"{f.SyntheticBirthDate:yyyy-MM-dd} (age {f.AgeYears})"),
                esc(string.Join(", ", f.Fields)), esc($"{f.PayloadBytes} bytes, SHA-256 {f.PayloadSha256}")]]));
            sb.Append("<ul>" + string.Concat(f.Notes.Select(n => $"<li>{esc(n)}</li>")) + "</ul></section>\n");
        }
        var m = run.Manifest;
        sb.Append("<section class=\"block\"><h2>Source contract binding</h2>");
        sb.Append(table(["Status", "Snapshot", "Archive SHA-256", "Commit", "Analyzer", "Fields confirmed", "Missing", "Fingerprint"], [[
            esc(ActiveCdcLabels.Contract(m.Status)), esc(m.SourceSnapshotId?.ToString() ?? "None"), esc(m.ArchiveSha256 ?? "—"), esc(m.SourceCommit ?? "—"), esc(m.AnalyzerVersion?.ToString() ?? "—"),
            esc(string.Join(", ", m.ConfirmedFields)), esc(m.MissingFields.Count == 0 ? "None" : string.Join(", ", m.MissingFields)), esc(m.Fingerprint)]]));
        sb.Append($"<p>{esc(m.Detail)}</p></section>\n");
        sb.Append("<section class=\"block\"><h2>What was tested</h2><ul>" + string.Concat(run.WhatWasTested.Select(t => $"<li>{esc(t)}</li>")) + "</ul></section>\n");
        sb.Append("<section class=\"block\"><h2>What was not assessed</h2><ul>" + string.Concat(run.WhatWasNotAssessed.Select(t => $"<li>{esc(t)}</li>")) + "</ul></section>\n");
        sb.Append("<section class=\"block\"><h2>Limitations</h2><ul>" + string.Concat(run.Limitations.Select(t => $"<li>{esc(t)}</li>")) + $"<li>{esc(run.Scenario.PassCriterion)}</li></ul></section>\n");
        return buildHtml("Active CDC test", projectName, $"Environment: {run.EnvironmentName}  Run: {run.RunId:N}  Started: {run.StartedAt:yyyy-MM-dd HH:mm} UTC", sb.ToString());
    }
}
