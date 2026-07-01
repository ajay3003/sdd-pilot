using System.Text.RegularExpressions;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public interface ITargetEnvironmentHintExtractor
{
    DetectedTargetEnvironmentHints Extract(SampleProjectDto project, IReadOnlyList<SampleProjectArtifactText> files);
}

public sealed class TargetEnvironmentHintExtractor : ITargetEnvironmentHintExtractor
{
    private static readonly Regex UrlRegex = new("""https?://[^\s\]\)\}"'<>`]+""", RegexOptions.IgnoreCase);
    private static readonly Regex PathRegex = new("""(?<![\w.-])/(?:api|v\d+|health|healthz|ready|live|swagger|openapi|api-docs|graphql)[A-Za-z0-9._~:/?#\[\]@!$&'()*+,;=%-]*""", RegexOptions.IgnoreCase);
    private static readonly Regex LabeledValueRegex = new("\"?(?<label>service\\s*bus\\s*namespace|namespace|resource|topic|queue|consumer\\s+group|subscription|endpoint)\"?\\s*[:=]\\s*\"?<?(?<value>[A-Za-z0-9._:/?#@-]+)>?", RegexOptions.IgnoreCase);
    private static readonly Regex TopicQueueInlineRegex = new("""\b(?<label>topic|queue)\b\s+[`"']?(?<value>[A-Za-z0-9._-]+)[`"']?""", RegexOptions.IgnoreCase);

    public DetectedTargetEnvironmentHints Extract(SampleProjectDto project, IReadOnlyList<SampleProjectArtifactText> files)
    {
        var hints = new DetectedTargetEnvironmentHints
        {
            ProjectSlug = project.Slug,
            ProjectName = project.Name
        };

        var allLines = files
            .SelectMany(file => SplitLines(file.Content).Select(line => (file.FileName, Line: line.Trim())))
            .Where(x => !string.IsNullOrWhiteSpace(x.Line))
            .ToList();

        var endpointCandidates = new List<(string Value, string FileName, string Line)>();
        foreach (var item in allLines)
        {
            foreach (Match match in UrlRegex.Matches(item.Line))
                endpointCandidates.Add((TrimCandidate(match.Value), item.FileName, item.Line));
            foreach (Match match in PathRegex.Matches(item.Line))
                endpointCandidates.Add((TrimCandidate(match.Value), item.FileName, item.Line));
        }

        hints.FrontendUrl = PickFirst(endpointCandidates, IsLikelyFrontendUrl);
        hints.RestBaseUrl = PickRestBaseUrl(endpointCandidates);
        hints.HealthEndpoint = PickFirst(endpointCandidates, IsLikelyHealthEndpoint);
        hints.SwaggerUrl = PickFirst(endpointCandidates, IsLikelySwaggerEndpoint);
        hints.GraphQlEndpoint = PickFirst(endpointCandidates, IsLikelyGraphQlEndpoint);
        hints.EnvironmentType = DetectEnvironment(allLines.Select(x => x.Line));
        hints.AuthType = DetectAuthType(allLines.Select(x => x.Line));

        foreach (var integration in DetectIntegrations(project, allLines, endpointCandidates, hints))
            hints.Integrations.Add(integration);

        hints.Evidence = endpointCandidates
            .Select(x => $"{x.FileName}: {x.Value}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();

        return hints;
    }

    private static IEnumerable<IntegrationTargetHint> DetectIntegrations(
        SampleProjectDto project,
        IReadOnlyList<(string FileName, string Line)> lines,
        IReadOnlyList<(string Value, string FileName, string Line)> endpointCandidates,
        DetectedTargetEnvironmentHints hints)
    {
        var providers = new (string Provider, string[] Terms)[]
        {
            ("REST", ["rest", "http api", "api endpoint"]),
            ("GraphQL", ["graphql"]),
            ("Event Hub", ["event hub", "eventhub", "event hubs"]),
            ("Service Bus", ["service bus", "servicebus"]),
            ("Kafka", ["kafka"]),
            ("RabbitMQ", ["rabbitmq", "rabbit mq"])
        };

        foreach (var provider in providers)
        {
            var matchedLines = lines
                .Where(x => provider.Terms.Any(term => x.Line.Contains(term, StringComparison.OrdinalIgnoreCase)))
                .Take(8)
                .ToList();

            if (matchedLines.Count == 0 && provider.Provider is "REST" or "GraphQL")
            {
                var endpoint = provider.Provider == "REST" ? hints.RestBaseUrl : hints.GraphQlEndpoint;
                if (!string.IsNullOrWhiteSpace(endpoint))
                    matchedLines.Add((project.Name, endpoint));
            }

            if (matchedLines.Count == 0)
                continue;

            var name = DetectIntegrationName(project.Name, provider.Provider, matchedLines.Select(x => x.Line));
            var entry = new IntegrationTargetHint
            {
                Name = name,
                ProviderType = provider.Provider,
                AuthType = hints.AuthType,
                EnvironmentHint = hints.EnvironmentType?.ToString(),
                Source = matchedLines.First().FileName,
                Endpoint = provider.Provider switch
                {
                    "REST" => hints.RestBaseUrl,
                    "GraphQL" => hints.GraphQlEndpoint,
                    _ => PickFirst(endpointCandidates, x => matchedLines.Any(line => line.FileName == x.FileName && line.Line.Contains(x.Value, StringComparison.OrdinalIgnoreCase)))
                }
            };

            ApplyLabeledValues(entry, matchedLines.Select(x => x.Line));
            yield return entry;
        }
    }

    private static void ApplyLabeledValues(IntegrationTargetHint entry, IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            foreach (Match match in LabeledValueRegex.Matches(line))
            {
                var label = match.Groups["label"].Value.Replace(" ", "", StringComparison.OrdinalIgnoreCase);
                var value = TrimCandidate(match.Groups["value"].Value);
                if (string.IsNullOrWhiteSpace(value)) continue;

                switch (label.ToLowerInvariant())
                {
                    case "namespace":
                    case "servicebusnamespace":
                        entry.Namespace ??= value;
                        break;
                    case "resource":
                        entry.Resource ??= value;
                        break;
                    case "topic":
                        entry.Topic ??= value;
                        break;
                    case "queue":
                        entry.Queue ??= value;
                        break;
                    case "consumergroup":
                        entry.ConsumerGroup ??= value;
                        break;
                    case "subscription":
                        entry.Subscription ??= value;
                        break;
                    case "endpoint":
                        entry.Endpoint ??= value;
                        break;
                }
            }

            foreach (Match match in TopicQueueInlineRegex.Matches(line))
            {
                var value = TrimCandidate(match.Groups["value"].Value);
                if (string.IsNullOrWhiteSpace(value)) continue;

                if (match.Groups["label"].Value.Equals("topic", StringComparison.OrdinalIgnoreCase))
                    entry.Topic ??= value;
                else
                    entry.Queue ??= value;
            }
        }
    }

    private static string DetectIntegrationName(string projectName, string provider, IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var colon = line.IndexOf(':');
            if (colon > 0 && colon < 80)
            {
                var prefix = line[..colon].Trim(' ', '-', '*', '#');
                if (prefix.Length is >= 3 and <= 60)
                    return prefix;
            }
        }

        return $"{projectName} {provider}";
    }

    private static FrontendEnvironmentType? DetectEnvironment(IEnumerable<string> lines)
    {
        var text = string.Join('\n', lines);
        if (Regex.IsMatch(text, """\b(localhost|local)\b""", RegexOptions.IgnoreCase)) return FrontendEnvironmentType.Local;
        if (Regex.IsMatch(text, """\b(dev|development)\b""", RegexOptions.IgnoreCase)) return FrontendEnvironmentType.Development;
        if (Regex.IsMatch(text, """\b(qa|quality assurance)\b""", RegexOptions.IgnoreCase)) return FrontendEnvironmentType.QA;
        if (Regex.IsMatch(text, """\b(test|testing)\b""", RegexOptions.IgnoreCase)) return FrontendEnvironmentType.Test;
        if (Regex.IsMatch(text, """\b(prod|production)\b""", RegexOptions.IgnoreCase)) return FrontendEnvironmentType.Production;
        return null;
    }

    private static TargetApiAuthType DetectAuthType(IEnumerable<string> lines)
    {
        var text = string.Join('\n', lines);
        if (Regex.IsMatch(text, """\b(api[- ]?key|x-api-key|subscription key)\b""", RegexOptions.IgnoreCase)) return TargetApiAuthType.ApiKey;
        if (Regex.IsMatch(text, """\b(basic auth|basic authentication)\b""", RegexOptions.IgnoreCase)) return TargetApiAuthType.BasicAuth;
        if (Regex.IsMatch(text, """\b(bearer|oauth2?|openid|oidc|entra|jwt)\b""", RegexOptions.IgnoreCase)) return TargetApiAuthType.BearerToken;
        return TargetApiAuthType.None;
    }

    private static string? PickFirst(
        IEnumerable<(string Value, string FileName, string Line)> candidates,
        Func<(string Value, string FileName, string Line), bool> predicate) =>
        candidates
            .Where(predicate)
            .Select(x => x.Value)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    private static string? PickRestBaseUrl(IReadOnlyList<(string Value, string FileName, string Line)> candidates) =>
        PickFirst(candidates, x =>
            IsLikelyRestBaseUrl(x) &&
            ContainsAny(x.Line, "base path", "base url", "route prefix")) ??
        PickFirst(candidates, x =>
            IsLikelyRestBaseUrl(x) &&
            !ContainsAny(x.Value, "autorisasjon", "authorization", "auth/")) ??
        PickFirst(candidates, IsLikelyRestBaseUrl);

    private static bool IsLikelyFrontendUrl((string Value, string FileName, string Line) candidate) =>
        IsAbsoluteHttp(candidate.Value) &&
        ContainsAny(candidate.Line, "frontend", "ui", "web", "client", "blazor", "portal") &&
        !ContainsAny(candidate.Value, "/api", "/swagger", "/openapi", "/graphql", "/health");

    private static bool IsLikelyRestBaseUrl((string Value, string FileName, string Line) candidate) =>
        ContainsAny(candidate.Value, "/api", "/v1", "/v2") ||
        (IsAbsoluteHttp(candidate.Value) && ContainsAny(candidate.Line, "rest", "base url", "api"));

    private static bool IsLikelyHealthEndpoint((string Value, string FileName, string Line) candidate) =>
        ContainsAny(candidate.Value, "health", "healthz", "ready", "live");

    private static bool IsLikelySwaggerEndpoint((string Value, string FileName, string Line) candidate) =>
        ContainsAny(candidate.Value, "swagger", "openapi", "api-docs");

    private static bool IsLikelyGraphQlEndpoint((string Value, string FileName, string Line) candidate) =>
        ContainsAny(candidate.Value, "graphql");

    private static bool IsAbsoluteHttp(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> SplitLines(string content) =>
        content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static string TrimCandidate(string value) =>
        value.Trim().TrimEnd('.', ',', ';', ':', ')', ']', '}', '"', '\'');
}
