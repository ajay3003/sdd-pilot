using System.Text.RegularExpressions;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public static class ArchitectureExtractor
{
    private static readonly Regex HeadingRe = new(@"^(#{1,6})\s+(.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex BulletRe = new(@"^\s*(?:[-*+]|\d+[.)])\s+(.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex BoldNameRe = new(@"\*\*(?<name>[^*]{2,80})\*\*", RegexOptions.Compiled);
    private static readonly Regex TermBeforeColonRe = new(@"^\s*(?:[-*+]\s*)?(?:\*\*)?(?<name>[A-Z][\p{L}\p{N}./ _-]{1,80}?)(?:\*\*)?\s*:\s*(?<desc>.+)$", RegexOptions.Compiled);
    private static readonly Regex LeadingNamedConceptRe = new(@"^\s*(?:[-*+]\s*)?(?<name>[A-Z][\p{L}\p{N}./_-]*(?:\s+[A-Z][\p{L}\p{N}./_-]*){0,4})\s+(?:is|are|provides|offers|uses|used|integrates|connects|sends|receives|publishes|consumes)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FrIdRe = new(@"\bFR-\d{3,4}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex UsIdRe = new(@"\bUS\d+\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DottedTopicRe = new(@"\b(?<name>[a-z][a-z0-9_-]*(?:\.[a-z0-9_-]+)+)\s+(?:topic|queue|stream)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex EventNameRe = new(@"\b(?<name>[A-Z][A-Za-z0-9]*(?:Created|Updated|Deleted|Changed|Registered|Published|Submitted|Completed|Failed|Received|Sent))\b", RegexOptions.Compiled);
    private static readonly Regex NamedKeywordRe = new(
        @"\b(?<name>[A-Z][\p{L}\p{N}./_-]*(?:\s+[A-Z][\p{L}\p{N}./_-]*){0,4}\s+(?:module|service|adapter|gateway|worker|processor|handler|manager|layer|frontend|backend|api|client|consumer|producer|entity|table|database|datastore|repository|store|cache|model|record|aggregate|integration|queue|topic|message bus|service bus|event bus|event hub|webhook|connector|endpoint|authorisation|authorization|audit|log|logger))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BareApiRe = new(@"\b(?<name>GraphQL|REST|RESTful|gRPC|SOAP)(?:\s+(?:API|endpoint|interface|ingestion))?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RelationshipRe = new(
        @"(?<source>[A-Z][\p{L}\p{N}/_-]*(?:\s+[A-Z][\p{L}\p{N}/_-]*){0,4})\s+(?<verb>calls|consumes|exposes|publishes|subscribes(?:\s+to)?|sends|receives|stores|persists|reads|writes|registers|authenticates|authorises|authorizes|validates|depends\s+on|integrates\s+with|communicates\s+with)\s+(?:(?:records|events|messages|data)\s+)?(?:(?:[A-Z][A-Za-z0-9]*(?:Created|Updated|Deleted|Changed|Registered|Published|Submitted|Completed|Failed|Received|Sent)(?:\s+event)?\s+)?(?:to\s+)?)?(?:(?:to|from|in|into|with|via|on)\s+)?(?:(?:a|an|the)\s+)?(?<target>[a-z][a-z0-9_-]*(?:\.[a-z0-9_-]+)+\s+topic|Event\s+Bus|Service\s+Bus|Message\s+Bus|[A-Z][\p{L}\p{N}/_-]*(?:\s+[A-Z][\p{L}\p{N}/_-]*){0,4}|access)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BusNameRe = new(@"\b(?<name>(?:Event|Service|Message)\s+Bus)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] ArchitectureHeadingTerms =
    [
        "api", "surface", "architecture", "system overview", "component", "entity", "entities", "domain model",
        "data model", "integration", "external system", "event", "messaging", "interface", "dependency",
        "infrastructure", "persistence", "storage", "security", "audit"
    ];

    private static readonly string[] ComponentTerms =
    [
        "module", "service", "adapter", "gateway", "worker", "processor", "handler", "manager",
        "layer", "frontend", "backend", "client", "consumer", "producer"
    ];

    private static readonly string[] DataTerms =
    [
        "entity", "entities", "table", "database", "datastore", "repository", "store", "cache", "model", "record", "aggregate"
    ];

    private static readonly string[] IntegrationTerms =
    [
        "integration", "external system", "rest", "graphql", "queue", "topic", "message bus", "service bus",
        "event hub", "webhook", "connector", "endpoint"
    ];

    private static readonly string[] EventTerms =
    [
        "event", "published", "publishes", "consumed", "consumes", "message", "topic", "payload", "mutation"
    ];

    private static readonly string[] InfrastructureTerms =
    [
        "infrastructure", "hosting", "container", "cluster", "region", "network", "deployment"
    ];

    private static readonly string[] CandidateTerms =
    [
        "pipeline", "ingestion", "orchestration", "replication", "synchronization", "synchronisation"
    ];

    public static ArchitectureModel Extract(
        SpecTree tree,
        string? rawMarkdown = null,
        IReadOnlyList<ExtractionCandidate>? candidates = null)
    {
        var elements = new List<ArchElement>();
        var relationships = new List<ArchitectureRelationship>();
        var architectureCandidates = new List<ArchitectureCandidate>();

        var treeText = string.Join("\n", Flatten(tree.Roots).Select(GetNodeText));
        var candidateText = candidates is { Count: > 0 }
            ? string.Join("\n", candidates.Select(c => $"{c.ContextHeading ?? ""}\n{c.Title}"))
            : string.Empty;
        var fullText = string.Join("\n", new[] { rawMarkdown, treeText, candidateText }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

        if (string.IsNullOrWhiteSpace(fullText))
            return new ArchitectureModel();

        var sections = BuildSections(fullText).ToList();

        ExtractFromArchitectureSections(sections, elements, architectureCandidates);
        ExtractByKeywords(sections, elements, architectureCandidates);
        ExtractRelationships(sections, elements, relationships);
        ExtractFallbackCandidates(sections, elements, architectureCandidates);

        return new ArchitectureModel
        {
            Elements = elements
                .Where(e => e.Confidence != ArchitectureConfidence.Low)
                .OrderBy(e => e.ElementType)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Relationships = DeduplicateRelationships(relationships),
            Candidates = DeduplicateCandidates(architectureCandidates, elements),
        };
    }

    private static void ExtractFromArchitectureSections(
        IEnumerable<SectionBlock> sections,
        List<ArchElement> elements,
        List<ArchitectureCandidate> candidates)
    {
        foreach (var section in sections.Where(s => IsArchitectureSection(s.Heading)))
        {
            foreach (var line in section.Lines)
            {
                var item = ExtractSectionItemName(line);
                if (item is null) continue;

                var type = Classify(item.Value.Name, line, section.Heading);
                var confidence = type is null ? ArchitectureConfidence.Medium : ArchitectureConfidence.High;
                if (type is null)
                {
                    AddCandidate(candidates, item.Value.Name, line, ArchElementType.Pattern, confidence,
                        "Architecture-oriented section contains a named item.", section.Heading);
                    continue;
                }

                AddOrMerge(elements, new ArchElement
                {
                    Name = CanonicalName(item.Value.Name, type.Value),
                    ElementType = type.Value,
                    Confidence = confidence,
                    Description = TrimDescription(item.Value.Description ?? line),
                    SourceText = line.Trim(),
                    SourceSections = [section.Heading],
                    RelatedFrIds = ExtractFrIds(line),
                    RelatedUsIds = ExtractUsIds(line),
                });
            }
        }
    }

    private static void ExtractByKeywords(
        IEnumerable<SectionBlock> sections,
        List<ArchElement> elements,
        List<ArchitectureCandidate> candidates)
    {
        foreach (var section in sections)
        {
            foreach (var line in section.Lines.Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                foreach (Match match in DottedTopicRe.Matches(line))
                {
                    AddOrMerge(elements, new ArchElement
                    {
                        Name = match.Groups["name"].Value + " topic",
                        ElementType = ArchElementType.Messaging,
                        Confidence = ArchitectureConfidence.High,
                        Description = TrimDescription(line),
                        SourceText = line.Trim(),
                        SourceSections = [section.Heading],
                        RelatedFrIds = ExtractFrIds(line),
                        RelatedUsIds = ExtractUsIds(line),
                    });
                }

                foreach (Match match in BusNameRe.Matches(line))
                {
                    AddOrMerge(elements, new ArchElement
                    {
                        Name = CleanName(match.Groups["name"].Value),
                        ElementType = ArchElementType.Messaging,
                        Confidence = ArchitectureConfidence.High,
                        Description = TrimDescription(line),
                        SourceText = line.Trim(),
                        SourceSections = [section.Heading],
                        RelatedFrIds = ExtractFrIds(line),
                        RelatedUsIds = ExtractUsIds(line),
                    });
                }

                foreach (Match match in EventNameRe.Matches(line))
                {
                    AddOrMerge(elements, new ArchElement
                    {
                        Name = CanonicalName(match.Groups["name"].Value, ArchElementType.DomainEvent),
                        ElementType = ArchElementType.DomainEvent,
                        Confidence = ArchitectureConfidence.High,
                        Description = TrimDescription(line),
                        SourceText = line.Trim(),
                        SourceSections = [section.Heading],
                        RelatedFrIds = ExtractFrIds(line),
                        RelatedUsIds = ExtractUsIds(line),
                    });
                }

                if (IsArchitectureSection(section.Heading) && ExtractSectionItemName(line) is not null)
                    continue;

                foreach (Match match in NamedKeywordRe.Matches(line))
                {
                    var name = CleanName(match.Groups["name"].Value);
                    if (!LooksNamed(name)) continue;
                    var type = Classify(name, line, section.Heading);
                    if (type is null) continue;

                    AddOrMerge(elements, new ArchElement
                    {
                        Name = CanonicalName(name, type.Value),
                        ElementType = type.Value,
                        Confidence = IsArchitectureSection(section.Heading) ? ArchitectureConfidence.High : ArchitectureConfidence.Medium,
                        Description = TrimDescription(line),
                        SourceText = line.Trim(),
                        SourceSections = [section.Heading],
                        RelatedFrIds = ExtractFrIds(line),
                        RelatedUsIds = ExtractUsIds(line),
                    });
                }

                foreach (Match match in BareApiRe.Matches(line))
                {
                    var name = CanonicalName(match.Groups["name"].Value, ArchElementType.Api);
                    AddOrMerge(elements, new ArchElement
                    {
                        Name = name,
                        ElementType = ArchElementType.Api,
                        Confidence = IsArchitectureSection(section.Heading) ? ArchitectureConfidence.High : ArchitectureConfidence.Medium,
                        Description = TrimDescription(line),
                        SourceText = line.Trim(),
                        SourceSections = [section.Heading],
                        RelatedFrIds = ExtractFrIds(line),
                        RelatedUsIds = ExtractUsIds(line),
                    });
                }

                if (typeFromWeakLine(line) is { } candidateType)
                {
                    var candidateName = ExtractWeakCandidateName(line);
                    if (!string.IsNullOrWhiteSpace(candidateName))
                    {
                        AddCandidate(candidates, candidateName, line, candidateType, ArchitectureConfidence.Medium,
                            "Contains generic architecture or integration wording.", section.Heading);
                    }
                }
            }
        }
    }

    private static void ExtractRelationships(
        IEnumerable<SectionBlock> sections,
        List<ArchElement> elements,
        List<ArchitectureRelationship> relationships)
    {
        foreach (var section in sections)
        {
            foreach (var line in section.Lines)
            {
                foreach (Match match in RelationshipRe.Matches(line))
                {
                    var source = CleanName(match.Groups["source"].Value);
                    var target = CleanName(match.Groups["target"].Value);
                    var verb = Regex.Replace(match.Groups["verb"].Value.Trim(), @"\s+", " ").ToLowerInvariant();

                    if (!LooksNamed(source) || !LooksNamed(target) || source.Equals(target, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var sourceType = Classify(source, line, section.Heading) ?? ArchElementType.Service;
                    var targetType = Classify(target, line, section.Heading) ?? InferTargetTypeFromVerb(verb);
                    var confidence = ContainsExplicitArchitectureKeyword(line) || IsArchitectureSection(section.Heading)
                        ? ArchitectureConfidence.High
                        : ArchitectureConfidence.Medium;

                    AddOrMerge(elements, new ArchElement
                    {
                        Name = CanonicalName(source, sourceType),
                        ElementType = sourceType,
                        Confidence = confidence,
                        Description = TrimDescription(line),
                        SourceText = line.Trim(),
                        SourceSections = [section.Heading],
                        RelatedFrIds = ExtractFrIds(line),
                        RelatedUsIds = ExtractUsIds(line),
                    });
                    AddOrMerge(elements, new ArchElement
                    {
                        Name = CanonicalName(target, targetType),
                        ElementType = targetType,
                        Confidence = confidence,
                        Description = TrimDescription(line),
                        SourceText = line.Trim(),
                        SourceSections = [section.Heading],
                        RelatedFrIds = ExtractFrIds(line),
                        RelatedUsIds = ExtractUsIds(line),
                    });
                    AddRelationship(relationships, new ArchitectureRelationship
                    {
                        SourceName = CanonicalName(source, sourceType),
                        TargetName = CanonicalName(target, targetType),
                        Verb = verb,
                        Confidence = confidence,
                        SourceText = line.Trim(),
                        SourceSection = section.Heading,
                        RelatedFrIds = ExtractFrIds(line),
                    });
                }
            }
        }
    }

    private static void ExtractFallbackCandidates(
        IEnumerable<SectionBlock> sections,
        List<ArchElement> elements,
        List<ArchitectureCandidate> candidates)
    {
        foreach (var section in sections)
        {
            foreach (var line in section.Lines)
            {
                if (!ContainsAny(line, CandidateTerms)) continue;
                var name = ExtractWeakCandidateName(line);
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (elements.Any(e => SameNormalizedName(e.Name, name))) continue;

                AddCandidate(candidates, name, line, ArchElementType.IntegrationPoint, ArchitectureConfidence.Medium,
                    "Contains pipeline or ingestion wording.", section.Heading);
            }
        }
    }

    private static IEnumerable<SpecNode> Flatten(IEnumerable<SpecNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children))
                yield return child;
        }
    }

    private static string GetNodeText(SpecNode node)
    {
        var parts = new[]
        {
            node.Title,
            node.Excerpt,
            node.FullContent,
            node.QuestionText,
            node.AnswerText,
            node.BddGiven,
            node.BddWhen,
            node.BddThen,
        };
        return string.Join("\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private static IEnumerable<SectionBlock> BuildSections(string markdown)
    {
        var currentHeading = "Specification";
        var currentLines = new List<string>();

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            var heading = HeadingRe.Match(line);
            if (heading.Success)
            {
                yield return new SectionBlock(currentHeading, currentLines);
                currentHeading = CleanName(heading.Groups[2].Value);
                currentLines = [];
                continue;
            }

            currentLines.Add(line);
        }

        yield return new SectionBlock(currentHeading, currentLines);
    }

    private static bool IsArchitectureSection(string heading)
    {
        var normalized = NormalizeText(heading);
        return ArchitectureHeadingTerms.Any(term => normalized.Contains(NormalizeText(term)));
    }

    private static (string Name, string? Description)? ExtractSectionItemName(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        var bullet = BulletRe.Match(line);
        var text = bullet.Success ? bullet.Groups[1].Value.Trim() : line.Trim();

        var bold = BoldNameRe.Match(text);
        if (bold.Success)
            return (CleanName(bold.Groups["name"].Value), text);

        var colon = TermBeforeColonRe.Match(text);
        if (colon.Success)
            return (CleanName(colon.Groups["name"].Value), colon.Groups["desc"].Value.Trim());

        var dashIndex = text.IndexOf(" - ", StringComparison.Ordinal);
        if (dashIndex < 0) dashIndex = text.IndexOf(" -- ", StringComparison.Ordinal);
        if (dashIndex < 0) dashIndex = text.IndexOf(" — ", StringComparison.Ordinal);
        if (dashIndex > 0)
            return (CleanName(text[..dashIndex]), text[(dashIndex + 3)..].Trim());

        var keywordMatch = NamedKeywordRe.Match(text);
        if (keywordMatch.Success)
            return (CleanName(keywordMatch.Groups["name"].Value), text);

        var apiMatch = BareApiRe.Match(text);
        if (apiMatch.Success)
            return (CleanName(apiMatch.Groups["name"].Value), text);

        var leadingConcept = LeadingNamedConceptRe.Match(text);
        if (leadingConcept.Success)
            return (CleanName(leadingConcept.Groups["name"].Value), text);

        return null;
    }

    private static ArchElementType? Classify(string name, string context, string heading)
    {
        var combined = NormalizeText($"{heading} {name} {context}");
        var normalizedName = NormalizeText(name);

        if (NormalizeText(heading).Contains("external system"))
            return ArchElementType.ExternalSystem;
        if (ContainsAny(normalizedName, ["graphql", "rest", "grpc", "soap", "api", "endpoint", "interface"]))
            return ArchElementType.Api;
        if (ContainsAny(normalizedName, ["topic", "queue", "message bus", "service bus", "event bus", "event hub", "webhook"]))
            return ArchElementType.Messaging;
        if (ContainsAny(normalizedName, ["adapter", "gateway", "worker", "processor", "handler", "manager", "module", "service", "layer", "frontend", "backend", "client", "consumer", "producer"]))
            return ArchElementType.Service;
        if (normalizedName.EndsWith("historikk", StringComparison.OrdinalIgnoreCase) || normalizedName.EndsWith("history", StringComparison.OrdinalIgnoreCase))
            return ArchElementType.DataStore;

        if (ContainsAny(combined, ["authorisation", "authorization", "authenticate", "security", "permission", "access control"]))
            return ArchElementType.Security;
        if (ContainsAny(combined, ["audit", "logging", "logger", "log service", "audit trail"]))
            return ArchElementType.Security;
        if (ContainsAny(combined, InfrastructureTerms))
            return ArchElementType.InfrastructureComponent;
        if (ContainsAny(combined, ["graphql", "rest", "grpc", "soap", "api", "endpoint", "interface", "mutation"]))
            return ArchElementType.Api;
        if (ContainsAny(combined, ["topic", "queue", "message bus", "service bus", "event bus", "event hub", "payload", "webhook"]))
            return ArchElementType.Messaging;
        if (ContainsAny(combined, ["integration", "external system", "connector"]))
            return ArchElementType.IntegrationPoint;
        if (ContainsAny(combined, EventTerms) || EventNameRe.IsMatch(name))
            return ArchElementType.DomainEvent;
        if (ContainsAny(combined, DataTerms) || normalizedName.EndsWith("historikk", StringComparison.OrdinalIgnoreCase) || normalizedName.EndsWith("history", StringComparison.OrdinalIgnoreCase))
            return normalizedName.Contains("database") || normalizedName.Contains("store") || normalizedName.Contains("table") || normalizedName.Contains("repository") || normalizedName.EndsWith("historikk")
                ? ArchElementType.DataStore
                : ArchElementType.DomainEntity;
        if (ContainsAny(combined, ComponentTerms))
            return ArchElementType.Service;

        if (IsArchitectureSection(heading) && LooksNamed(name))
            return ArchElementType.DomainEntity;

        return null;
    }

    private static ArchElementType InferTargetTypeFromVerb(string verb) => verb switch
    {
        var v when v.Contains("publish") || v.Contains("subscribe") || v.Contains("send") || v.Contains("receive") => ArchElementType.Messaging,
        var v when v.Contains("store") || v.Contains("persist") || v.Contains("read") || v.Contains("write") => ArchElementType.DataStore,
        var v when v.Contains("auth") || v.Contains("validate") => ArchElementType.Security,
        var v when v.Contains("integrate") || v.Contains("communicate") => ArchElementType.IntegrationPoint,
        _ => ArchElementType.Api,
    };

    private static ArchElementType? typeFromWeakLine(string line)
    {
        var normalized = NormalizeText(line);
        if (ContainsAny(normalized, CandidateTerms.Concat(IntegrationTerms))) return ArchElementType.IntegrationPoint;
        if (ContainsAny(normalized, DataTerms)) return ArchElementType.DataStore;
        if (ContainsAny(normalized, ComponentTerms)) return ArchElementType.Service;
        return null;
    }

    private static string ExtractWeakCandidateName(string line)
    {
        var text = BulletRe.Match(line) is { Success: true } bullet ? bullet.Groups[1].Value : line;
        text = Regex.Replace(text, @"\b(?:FR-\d{3,4}|US\d+|the system must|shall|should|will)\b", "", RegexOptions.IgnoreCase).Trim();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(4).ToArray();
        return CleanName(string.Join(' ', words));
    }

    private static bool ContainsExplicitArchitectureKeyword(string line)
    {
        var text = NormalizeText(line);
        return ContainsAny(text, ComponentTerms)
            || ContainsAny(text, DataTerms)
            || ContainsAny(text, IntegrationTerms)
            || ContainsAny(text, EventTerms)
            || ContainsAny(text, InfrastructureTerms);
    }

    private static string CanonicalName(string name, ArchElementType type)
    {
        var cleaned = CleanName(name);
        if (type == ArchElementType.Api)
        {
            if (cleaned.Equals("RESTful", StringComparison.OrdinalIgnoreCase)) return "REST";
            if (cleaned.StartsWith("REST ", StringComparison.OrdinalIgnoreCase)) return cleaned;
            if (cleaned.Equals("REST", StringComparison.OrdinalIgnoreCase)) return "REST";
            if (cleaned.Equals("GraphQL API", StringComparison.OrdinalIgnoreCase)) return "GraphQL";
        }

        if (type == ArchElementType.Messaging && DottedTopicRe.IsMatch(cleaned))
            return cleaned.EndsWith(" topic", StringComparison.OrdinalIgnoreCase) ? cleaned : cleaned + " topic";
        if (type == ArchElementType.DomainEvent)
            return Regex.Replace(cleaned, @"\s+event$", "", RegexOptions.IgnoreCase);

        return cleaned;
    }

    private static string CleanName(string value)
    {
        var cleaned = value
            .Replace("**", "")
            .Replace("`", "")
            .Trim();
        cleaned = Regex.Replace(cleaned, @"\s+", " ");
        cleaned = Regex.Replace(cleaned, @"^(?:a|an|the)\s+", "", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"[\s,.;:]+$", "");
        return cleaned.Trim();
    }

    private static string NormalizeKey(string value)
    {
        var key = NormalizeText(value);
        key = Regex.Replace(key, @"\b(api|endpoint|interface|service|module)\b", "", RegexOptions.IgnoreCase);
        key = Regex.Replace(key, @"\s+", " ").Trim();
        return key;
    }

    private static string NormalizeText(string value) =>
        Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9.]+", " ").Trim();

    private static bool SameNormalizedName(string left, string right) =>
        string.Equals(NormalizeKey(left), NormalizeKey(right), StringComparison.OrdinalIgnoreCase);

    private static bool ContainsAny(string text, IEnumerable<string> terms)
    {
        var normalized = NormalizeText(text);
        return terms.Any(term => normalized.Contains(NormalizeText(term), StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksNamed(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length < 2 || name.Length > 90) return false;
        if (FrIdRe.IsMatch(name) || UsIdRe.IsMatch(name)) return false;
        return name.Any(char.IsLetter);
    }

    private static List<string> ExtractFrIds(string text) =>
        FrIdRe.Matches(text).Select(m => m.Value.ToUpperInvariant()).Distinct().ToList();

    private static List<string> ExtractUsIds(string text) =>
        UsIdRe.Matches(text).Select(m => m.Value.ToUpperInvariant()).Distinct().ToList();

    private static string TrimDescription(string value)
    {
        var cleaned = CleanName(value);
        if (cleaned.Length <= 240) return cleaned;
        var wordBreak = cleaned.LastIndexOf(' ', 237);
        return (wordBreak > 40 ? cleaned[..wordBreak] : cleaned[..237]) + "...";
    }

    private static void AddOrMerge(List<ArchElement> elements, ArchElement element)
    {
        if (!LooksNamed(element.Name)) return;
        var key = NormalizeKey(element.Name);
        if (string.IsNullOrWhiteSpace(key)) return;

        var existing = elements.FirstOrDefault(e => SameNormalizedName(e.Name, element.Name));
        if (existing is not null)
        {
            if (element.Confidence > existing.Confidence)
                existing.Confidence = element.Confidence;
            foreach (var section in element.SourceSections.Where(s => !existing.SourceSections.Contains(s)))
                existing.SourceSections.Add(section);
            foreach (var id in element.RelatedFrIds.Where(id => !existing.RelatedFrIds.Contains(id)))
                existing.RelatedFrIds.Add(id);
            foreach (var id in element.RelatedUsIds.Where(id => !existing.RelatedUsIds.Contains(id)))
                existing.RelatedUsIds.Add(id);
            foreach (var dep in element.DependsOn.Where(dep => !existing.DependsOn.Contains(dep)))
                existing.DependsOn.Add(dep);
            foreach (var usedBy in element.UsedBy.Where(usedBy => !existing.UsedBy.Contains(usedBy)))
                existing.UsedBy.Add(usedBy);
            return;
        }

        elements.Add(element);
    }

    private static void AddRelationship(List<ArchitectureRelationship> relationships, ArchitectureRelationship relationship)
    {
        if (relationships.Any(r =>
                SameNormalizedName(r.SourceName, relationship.SourceName)
                && SameNormalizedName(r.TargetName, relationship.TargetName)
                && string.Equals(r.Verb, relationship.Verb, StringComparison.OrdinalIgnoreCase)))
            return;

        relationships.Add(relationship);
    }

    private static void AddCandidate(
        List<ArchitectureCandidate> candidates,
        string name,
        string sourceText,
        ArchElementType type,
        ArchitectureConfidence confidence,
        string reason,
        string section)
    {
        if (!LooksNamed(name)) return;
        if (candidates.Any(c => SameNormalizedName(c.Name, name))) return;
        candidates.Add(new ArchitectureCandidate
        {
            Name = CleanName(name),
            SourceText = sourceText.Trim(),
            SuggestedType = type,
            Confidence = confidence,
            Reason = reason,
            SourceSection = section,
        });
    }

    private static List<ArchitectureRelationship> DeduplicateRelationships(IEnumerable<ArchitectureRelationship> relationships) =>
        relationships
            .GroupBy(r => $"{NormalizeKey(r.SourceName)}->{NormalizeKey(r.TargetName)}:{r.Verb}")
            .Select(g => g.OrderByDescending(r => r.Confidence).First())
            .ToList();

    private static List<ArchitectureCandidate> DeduplicateCandidates(
        IEnumerable<ArchitectureCandidate> candidates,
        IReadOnlyList<ArchElement> elements) =>
        candidates
            .Where(c => !elements.Any(e => SameNormalizedName(e.Name, c.Name)))
            .GroupBy(c => NormalizeKey(c.Name))
            .Select(g => g.OrderByDescending(c => c.Confidence).First())
            .Where(c => c.Confidence != ArchitectureConfidence.Low)
            .ToList();

    private sealed record SectionBlock(string Heading, List<string> Lines);
}
