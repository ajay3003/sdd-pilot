using System.Text.RegularExpressions;
using BirkNext.Api.Services.SourceAnalysis.Evidence;
using BirkNext.SourceArchitecture;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.AltinnTesting;

/// <summary>
/// Altinn provider → configuration values from application configuration in source (for example a message hub's Altinn event subscriptions):
/// an Altinn 3 app URL <c>https://{org}.apps.{domain}/{org}/{app}</c> declares the app route, its base URL and — from the Altinn domain alone —
/// whether it is the test (TT02) or production platform; the Endpoint of the same subscription entry is the webhook that receives the app's
/// events; an "...AppId" key holding "{org}/{app}" declares the route too. Each value is scoped to its app, so two subscribed apps never
/// conflict. Only display-safe, non-sensitive entries are read; nothing is contacted. Declared in source: not configured, not verified.
/// </summary>
internal sealed class AltinnSourceConfigurationProducer : ISourceConfigurationValueProducer
{
    private static readonly Regex AppUrl = new(@"^https://(?<org>[a-z0-9-]+)\.apps\.(?<domain>(tt02\.)?altinn\.no)/(?<org2>[a-z0-9-]+)/(?<app>[a-z0-9-]+)/?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AppId = new(@"^(?<org>[a-z0-9-]+)/(?<app>[a-z0-9-]+)$", RegexOptions.CultureInvariant);

    public string Name => "Altinn 3 app references in application configuration (app URLs, event-subscription webhooks, app ids)";

    public IEnumerable<SourceConfigurationValue> Produce(ConfigurationEvidence? configuration)
    {
        var entries = (configuration?.Entries ?? []).Where(entry => entry.Sensitivity == ConfigurationSensitivity.None && entry.ValuePreviewSafe is { Length: > 0 }).ToList();
        foreach (var entry in entries)
        {
            var value = entry.ValuePreviewSafe!.Trim();
            SourceConfigurationValue Value(string key, string text, string scope, SourceConfigurationGroup group, string basis, SourceEnvironmentRole environment = SourceEnvironmentRole.Any,
                Dictionary<string, string>? attributes = null) => new()
            {
                SemanticKey = key, Value = text, Group = group, Environment = environment, EvidenceKind = SourceConfigurationEvidenceKind.ApplicationConfig,
                Artifact = entry.File, Location = $"{entry.Key} (line {entry.Line})", Basis = basis, LogicalSource = entry.Component, Scope = scope,
                Confidence = ArchitectureEvidenceState.StronglySupported, Attributes = attributes ?? [],
            };

            var url = AppUrl.Match(value);
            if (url.Success && url.Groups["org"].Value.Equals(url.Groups["org2"].Value, StringComparison.OrdinalIgnoreCase))
            {
                var route = $"{url.Groups["org"].Value}/{url.Groups["app"].Value}".ToLowerInvariant();
                var test = url.Groups["domain"].Value.StartsWith("tt02.", StringComparison.OrdinalIgnoreCase);
                var role = test ? SourceEnvironmentRole.Test : SourceEnvironmentRole.Production;
                var host = $"https://{url.Groups["org"].Value}.apps.{url.Groups["domain"].Value}".ToLowerInvariant();
                var basis = $"Altinn app URL in {entry.Key}";
                yield return Value(SourceConfigurationKeys.ApplicationRoute, route, route, SourceConfigurationGroup.Application, basis);
                yield return Value(SourceConfigurationKeys.ApplicationBaseUrl, $"{host}/{route}", route, SourceConfigurationGroup.Application, basis, role);
                yield return Value(SourceConfigurationKeys.ApplicationEnvironment, role.ToString(), route, SourceConfigurationGroup.Environment,
                    $"{basis}: {url.Groups["domain"].Value} is the Altinn {(test ? "test (TT02)" : "production")} platform", role);
                yield return Value(SourceConfigurationKeys.Server(role), host, route, SourceConfigurationGroup.Environment, basis, role);

                // The webhook of the same subscription entry: the sibling "Endpoint" under the same parent key.
                var parent = entry.NormalizedKey.Contains(':') ? entry.NormalizedKey[..entry.NormalizedKey.LastIndexOf(':')] : "";
                foreach (var sibling in entries.Where(other => other.File == entry.File && other.Environment == entry.Environment
                    && other.NormalizedKey == $"{parent}:endpoint" && Uri.TryCreate(other.ValuePreviewSafe, UriKind.Absolute, out var endpoint) && endpoint.Scheme == "https"))
                {
                    var trigger = entries.FirstOrDefault(other => other.File == entry.File && other.NormalizedKey == $"{parent}:trigger")?.ValuePreviewSafe;
                    yield return new SourceConfigurationValue
                    {
                        SemanticKey = SourceConfigurationKeys.EndpointEventWebhook, Value = sibling.ValuePreviewSafe!.Trim(), Group = SourceConfigurationGroup.ApiEndpoints,
                        EvidenceKind = SourceConfigurationEvidenceKind.ApplicationConfig, Artifact = sibling.File, Location = $"{sibling.Key} (line {sibling.Line})",
                        Basis = $"endpoint of the event subscription whose source is {route}", LogicalSource = sibling.Component, Scope = route,
                        Confidence = ArchitectureEvidenceState.StronglySupported,
                        Attributes = trigger is null ? new() { ["SourceFilter"] = value } : new() { ["SourceFilter"] = value, ["Trigger"] = trigger },
                    };
                }
                continue;
            }

            if (entry.NormalizedKey.EndsWith("appid", StringComparison.Ordinal) && AppId.Match(value) is { Success: true } id)
            {
                var route = $"{id.Groups["org"].Value}/{id.Groups["app"].Value}";
                yield return Value(SourceConfigurationKeys.ApplicationRoute, route, route, SourceConfigurationGroup.Application, $"Altinn app id in {entry.Key}");
            }
        }
    }
}
