using System.Text;
using BirkNext.SourceArchitecture;

namespace BirkNext.Web.Services;

/// <summary>
/// HTML report of one source-architecture snapshot: summary, components, dependencies, messaging, datastores, external systems, evidence and
/// confidence, unresolved items and limitations. Only what the snapshot already holds — keys and safe entity names, never configuration values.
/// </summary>
public static class SourceArchitectureExport
{
    public static string Build(ArchitectureSnapshot a, Func<string[], IEnumerable<string[]>, string> table, Func<string?, string> esc, Func<string, string?, string?, string, string> buildHtml)
    {
        string Name(string? id) => ArchitecturePresentation.Name(a, id);
        var o = ArchitecturePresentation.Overview(a);
        var sb = new StringBuilder();
        sb.Append($"<section class=\"block\"><h2>Source architecture</h2><p><strong>{esc(ArchitectureSnapshot.SourceLimitation)}</strong></p>");
        sb.Append($"<p>Source snapshot {esc(a.SourceSnapshotId.ToString())} · fingerprint {esc(a.SourceFingerprint)} · analyzer v{a.AnalyzerVersion} · status {esc(a.Status.ToString())} (Complete is not a claim of correctness)</p>");
        sb.Append(table(["Components", "APIs", "Workers", "Adapters", "Frontends", "Messaging channels", "Datastores", "External systems", "Resolved", "Unresolved", "Inferred", "Conflicts"],
            [[o.Components.ToString(), o.Apis.ToString(), o.Workers.ToString(), o.Adapters.ToString(), o.Frontends.ToString(), o.Channels.ToString(), o.DataStores.ToString(), o.ExternalSystems.ToString(),
              o.Resolved.ToString(), o.Unresolved.ToString(), o.Inferred.ToString(), o.Conflicts.ToString()]]));
        sb.Append($"<p>Technologies: {esc(string.Join(", ", a.Technologies.Select(t => t.Name)))}</p></section>\n");

        sb.Append("<section class=\"block\"><h2>Components</h2>");
        sb.Append(table(["Component", "Type", "Runtime", "Project", "Technologies", "Evidence"], a.Components.Select(c => new[]
            { esc(c.Name), esc($"{ArchitecturePresentation.Label(c.ComponentType)} ({c.Confidence})"), esc(c.RuntimeType), esc(c.SourceProject), esc(string.Join(", ", c.Technologies)),
              esc(string.Join("; ", c.Evidence.Select(e => $"{e.File}:{e.Line} {e.Explanation}"))) })));
        sb.Append("</section>\n<section class=\"block\"><h2>Dependencies</h2>");
        sb.Append(table(["From", "To / target reference", "Type", "Protocol", "Evidence state", "Confidence", "Evidence"], a.Dependencies.Where(d => d.DependencyType != ArchitectureDependencyType.PackageDependency).Select(d => new[]
            { esc(Name(d.FromComponentId)), esc(d.ToId is null ? $"Unresolved: {d.TargetReference}" : Name(d.ToId)), esc(d.DependencyType + (d.Framework is { } f ? $" · {f}" : "")), esc(d.Protocol),
              esc(ArchitecturePresentation.StateLabel(d.EvidenceState)), esc(d.Confidence), esc(string.Join("; ", d.Evidence.Take(4).Select(e => $"{e.Kind} {e.File}:{e.Line}"))) })));
        sb.Append("</section>\n<section class=\"block\"><h2>Messaging</h2>");
        sb.Append(table(["Channel", "Type", "Producers", "Consumers", "Subscription / consumer group", "Per environment", "Confidence"], a.MessagingChannels.Select(c => new[]
            { esc(c.Name), esc(ArchitecturePresentation.ChannelLabel(c.Type)), esc(c.Producers.Count == 0 ? "None found in source" : string.Join(", ", c.Producers.Select(p => Name(p.ComponentId)))),
              esc(c.Consumers.Count == 0 ? "None found in source" : string.Join(", ", c.Consumers.Select(p => Name(p.ComponentId)))), esc(c.Subscription ?? c.ConsumerGroup ?? "—"),
              esc(string.Join("; ", c.EnvironmentVariants.Select(v => $"{v.Key}: {v.Value}"))), esc(c.NameUnresolved ? "Name unresolved — not paired" : ArchitecturePresentation.StateLabel(c.Confidence)) })));
        sb.Append("</section>\n<section class=\"block\"><h2>Datastores (no tables — see the Database report)</h2>");
        sb.Append(table(["Datastore", "Type", "Usage", "Used by", "Connection reference", "Database analysis"], a.DataStores.Select(s => new[]
            { esc(s.LogicalName), esc(ArchitecturePresentation.StoreLabel(s.StoreType)), esc(s.Usage), esc(string.Join(", ", s.ReferencedByComponents.Select(Name))), esc(s.ConnectionReference ?? "—"),
              esc(s.DatabaseModelId is null ? "Not linked" : "Available") })));
        sb.Append("</section>\n<section class=\"block\"><h2>External systems</h2>");
        sb.Append(table(["External system", "Type", "Protocol", "Configuration"], a.ExternalSystems.Select(e => new[] { esc(e.Name), esc(e.Type), esc(e.Protocol), esc(e.ConfigurationReference ?? "—") })));
        sb.Append("</section>\n<section class=\"block\"><h2>Unresolved items</h2><ul>");
        foreach (var d in a.UnresolvedItems) sb.Append($"<li>{esc(Name(d.FromComponentId))} · {esc(d.DependencyType.ToString())} · {esc(d.TargetReference)}</li>");
        sb.Append("</ul></section>\n<section class=\"block\"><h2>Diagnostics and limitations</h2><ul>");
        foreach (var d in a.Diagnostics) sb.Append($"<li>{esc(d.Kind)} — {esc(d.Message)}</li>");
        foreach (var l in a.Limitations) sb.Append($"<li>{esc(l)}</li>");
        sb.Append("</ul></section>\n");
        return buildHtml("Source architecture", null, $"Fingerprint {a.SourceFingerprint}  Extracted {a.ExtractedAt:yyyy-MM-dd HH:mm} UTC", sb.ToString());
    }
}
