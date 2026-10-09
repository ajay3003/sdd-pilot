using System.Text;
using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>
/// HTML export of one Active Event run, for every provider, from the stored run only: provider, scenario, environment, integration,
/// operation, correlation, transport result, consumer activity, continuity, downstream result and limitations — each stage on its own.
/// Provider-specific values appear only in a clearly named provider section. No payload body, credential or token exists in the run.
/// </summary>
public static class ActiveEventRunExport
{
    public static string Build(ActiveEventRunResult run, string? projectName, Func<string[], IEnumerable<string[]>, string> table, Func<string, string> badge,
        Func<string?, string> esc, Func<string, string?, string?, string, string> buildHtml)
    {
        var sb = new StringBuilder();
        sb.Append($"<section class=\"block\"><h2>Active event test — {esc(run.Scenario.DisplayName)} ({esc(run.Scenario.ScenarioId)} v{esc(run.Scenario.ScenarioVersion)})</h2>");
        sb.Append($"<p>Status: {badge(ActiveEventPresentation.Status(run.Status))} — {esc(ActiveEventPresentation.Headline(run))}.</p>");
        string[][] facts =
        [
            ["Provider", $"{run.Scenario.ProviderDisplayName} ({run.Scenario.ExtensionId} v{run.Scenario.ExtensionVersion})"],
            ["Scenario", run.Scenario.DisplayName],
            ["Resource", run.Scenario.ResourceLabel],
            ["Operation", string.Join(", ", run.Events.Select(e => ActiveEventPresentation.Operation(e.Operation)).Distinct())],
            ["Environment", $"{run.Target.EnvironmentDisplayName} ({run.Target.EnvironmentType}, backend-owned)"],
            ["Integration", run.Target.IntegrationDisplayName],
            ["Transport", $"{run.Target.TransportType}: {run.Target.Resource} on {run.Target.Endpoint}"],
            ["Source contract", $"{run.SourceContract.ContractStatus} {run.SourceContract.ContractFingerprint}".Trim()],
            ["Started / completed", $"{run.StartedAt:yyyy-MM-dd HH:mm:ss} UTC / {(run.CompletedAt is { } c ? $"{c:yyyy-MM-dd HH:mm:ss} UTC" : "in progress")}"],
        ];
        sb.Append(table(["Fact", "Value"], facts.Select(row => row.Select(esc).ToArray())));
        sb.Append("<p>Generated ≠ sent ≠ transport accepted ≠ consumer activity ≠ consumer continuity ≠ downstream verified. Not verified, unavailable and not observed are not failures.</p></section>\n");

        sb.Append("<section class=\"block\"><h2>Evidence stages</h2>");
        sb.Append(table(["Stage", "Event", "State", "Correlation", "Evidence", "Source"], run.Evidence.Select(stage => new[]
        {
            esc(ActiveEventPresentation.Stage(stage.Stage)), esc(stage.EventId ?? "Run"), badge(ActiveEventPresentation.Evidence(stage.Status)),
            esc(stage.CorrelationQuality.ToString()), esc(stage.Detail), esc(stage.EvidenceSource),
        })));
        var downstream = run.Evidence.Where(e => e.Downstream is not null).ToList();
        if (downstream.Count > 0)
            sb.Append(table(["Event", "Downstream outcome", "Expected identity", "Observed identity", "Reason"], downstream.Select(stage => new[]
            {
                esc(stage.EventId), esc(stage.Downstream!.Outcome.ToString()), esc(stage.Downstream.ExpectedIdentity), esc(stage.Downstream.ObservedIdentity), esc(stage.Downstream.Reason),
            })));
        sb.Append("</section>\n");

        if (run.Events.Count > 0)
        {
            sb.Append("<section class=\"block\"><h2>Generated events (payload not stored)</h2>");
            sb.Append(table(["Event", "Operation", "Synthetic identity", "Correlation", "Payload"], run.Events.Select(item => new[]
            {
                esc(item.SafeMetadata.GetValueOrDefault("sequenceLabel") is { Length: > 0 } label ? label : item.EventId), esc(ActiveEventPresentation.Operation(item.Operation)),
                esc(ActiveEventPresentation.SyntheticIdentity(item)), esc($"run {item.Correlation.RunId:N} · event {item.Correlation.EventId}{(item.Correlation.ReplaysEventId is { } r ? $" · replays {r}" : "")}"),
                esc($"{item.BodyBytes} bytes · SHA-256 {item.BodySha256}"),
            })));
            sb.Append($"<h3>Provider details — {esc(run.Scenario.ProviderDisplayName)}</h3>");
            sb.Append(table(["Event", "Key", "Value"], run.Events.SelectMany(item => ActiveEventPresentation.ProviderDetails(item).Select(pair => new[]
            {
                esc(item.SafeMetadata.GetValueOrDefault("sequenceLabel") is { Length: > 0 } label ? label : item.EventId), esc(pair.Key), esc(pair.Value),
            }))));
            sb.Append("</section>\n");
        }

        sb.Append("<section class=\"block\"><h2>Meaning and limitations</h2>");
        if (run.Status == ActiveEventRunStatus.Completed && run.Scenario.ResultMeaning.Length > 0) sb.Append($"<p>{esc(run.Scenario.ResultMeaning)}</p>");
        if (run.Scenario.ResultDoesNotMean.Length > 0) sb.Append($"<p><strong>{esc(run.Scenario.ResultDoesNotMean)}</strong></p>");
        if (run.Limitations.Count > 0) sb.Append("<ul>" + string.Concat(run.Limitations.Select(l => $"<li>{esc(l)}</li>")) + "</ul>");
        sb.Append("</section>\n");
        return buildHtml("Active Event Test", projectName, $"Run {run.RunId:N} · {run.Scenario.ProviderDisplayName}", sb.ToString());
    }
}
