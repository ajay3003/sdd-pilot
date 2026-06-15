using System.Text.RegularExpressions;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Extracts an architecture model from a parsed SpecTree.
/// Operates on spec structure (ApiSurfaceItem, Entity, Assumption, etc.)
/// and full-text regex patterns — NOT on extraction candidates.
/// </summary>
public static class ArchitectureExtractor
{
    // ── Regexes ──────────────────────────────────────────────────────────────

    private static readonly Regex ServiceRe = new(
        @"\b(\p{Lu}[\p{L}\p{N}]+(?:\s\p{Lu}[\p{L}\p{N}]+)*\s+(?:Module|Service|Adapter|Gateway|Worker|Processor|Handler|Manager))\b",
        RegexOptions.Compiled);

    private static readonly Regex DomainEventRe = new(
        @"\b(\p{Lu}\p{Ll}+(?:\p{Lu}[\p{L}]+)*(?:Opprettet|Oppdatert|Registrert|Endret|Slettet|Created|Updated|Deleted|Changed|Published))\b",
        RegexOptions.Compiled);

    private static readonly Regex PermissionRe = new(
        @"\bPerson:([A-Za-zÀ-ɏ]+)\b",
        RegexOptions.Compiled);

    private static readonly Regex TopicRe = new(
        @"\b([\w]+\.[\w]+)\s+topic\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex QueueRe = new(
        @"\b([\w]+(?:\s[\w]+)?)\s+queue\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PersistenceNameRe = new(
        @"\b([A-Z]\w+(?:Table|Repository|Store|Historikk|History|Cache|Message|Messages|Outbox))\b",
        RegexOptions.Compiled);

    private static readonly Regex FrIdRe = new(@"\bFR-\d{3,4}\b", RegexOptions.Compiled);
    private static readonly Regex UsIdRe = new(@"\bUS\d+\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── False-positive filters ───────────────────────────────────────────────

    private static readonly HashSet<string> FalsePersistence = new(StringComparer.OrdinalIgnoreCase)
    {
        "Database", "Repository", "Store", "Cache", "Table", "History", "Message", "Messages",
        "Outbox", "Datastore",
    };

    private static readonly HashSet<string> FalseServices = new(StringComparer.OrdinalIgnoreCase)
    {
        "Service Bus", "Azure Service Bus",              // handled as messaging
        "Authorisation Module", "Authorization Module",  // handled as external system
        "Audit Service", "Audit Log Service",            // handled as external system
    };

    // ── External systems ─────────────────────────────────────────────────────

    private static readonly (string Name, string[] SearchTerms, string Description)[] ExternalSystems =
    [
        ("BiRK",
         ["birk"],
         "National child welfare data source; drives the ingestion pipeline via CDC"),
        ("FREG",
         ["freg"],
         "National population registry"),
        ("DSAM",
         ["dsam"],
         "Child welfare case management system"),
        ("Folkeregisteret",
         ["folkeregisteret"],
         "Civil registration authority"),
        ("Authorisation Module",
         ["authorisation module", "authorization module", "auth module"],
         "Central authorisation service; fail-closed — denies all access when unavailable"),
        ("Audit Service",
         ["audit service", "audit log service", "auditservice"],
         "Records all data access operations for compliance and audit trail"),
    ];

    // ── Architecture patterns ────────────────────────────────────────────────

    private static readonly (string Name, string[] Keywords, string Description)[] ArchPatterns =
    [
        ("Outbox Pattern",
         ["outbox pattern", "outboxmessage", "outbox message"],
         "Reliable event publishing via transactional outbox — write event to DB in same transaction, publish later"),
        ("Session-based Ordering",
         ["session-based ordering", "session based ordering", "session ordering"],
         "Azure Service Bus session keys guarantee ordered processing per entity"),
        ("Idempotent Ingestion",
         ["idempotent", "duplicate detection", "deduplication"],
         "BiRK data ingestion is idempotent; retries and replays are safe"),
        ("CDC Pipeline",
         ["change data capture", " cdc ", "cdc adapter", "cdc pipeline"],
         "BiRK → CDC Adapter → Person Module ingestion pipeline"),
    ];

    // ── Risk keywords ────────────────────────────────────────────────────────

    private static readonly string[] RiskKeywords =
    [
        "unavailable", "unavailability", "fail ", "failure", "latency",
        "bottleneck", "risk", "outage", "timeout", "degraded",
    ];

    // ── Main entry point ─────────────────────────────────────────────────────

    public static ArchitectureModel Extract(SpecTree tree)
    {
        var elements = new List<ArchElement>();
        var nodesWithSection = FlattenWithSections(tree.Roots).ToList();
        var allNodes = nodesWithSection.Select(x => x.Node).ToList();

        var fullText = string.Join("\n", allNodes.Select(GetNodeText));

        // Pass 1 — Structured spec nodes
        ExtractFromStructuredNodes(nodesWithSection, elements);

        // Pass 2 — Regex scan over section-grouped text
        ExtractViaRegex(nodesWithSection, elements);

        // Pass 3 — Named full-text lookups
        ExtractNamedElements(fullText, nodesWithSection, elements);

        return new ArchitectureModel { Elements = elements };
    }

    // ── Pass 1: structured nodes ─────────────────────────────────────────────

    private static void ExtractFromStructuredNodes(
        List<(SpecNode Node, string Section)> nodesWithSection,
        List<ArchElement> elements)
    {
        foreach (var (node, section) in nodesWithSection)
        {
            var text = GetNodeText(node);

            if (node.NodeType == SpecNodeType.ApiSurfaceItem)
            {
                var name = ExtractApiName(node.Title);
                AddOrMerge(elements, new ArchElement
                {
                    Name = name,
                    ElementType = ArchElementType.Api,
                    Description = TrimDescription(node.FullContent ?? node.Excerpt),
                    SourceSections = [section],
                    RelatedFrIds = ExtractFrIds(text),
                    RelatedUsIds = ExtractUsIds(text),
                });
                continue;
            }

            if (node.NodeType == SpecNodeType.Entity)
            {
                var type = ClassifyEntityNode(node.Title, text);
                if (type.HasValue)
                {
                    AddOrMerge(elements, new ArchElement
                    {
                        Name = node.SpecItemId ?? node.Title,
                        ElementType = type.Value,
                        Description = TrimDescription(node.FullContent ?? node.Excerpt),
                        SourceSections = [section],
                        RelatedFrIds = ExtractFrIds(text),
                        RelatedUsIds = ExtractUsIds(text),
                    });
                }
                continue;
            }

            if (node.NodeType is SpecNodeType.Assumption or SpecNodeType.EdgeCase)
            {
                if (RiskKeywords.Any(kw => text.Contains(kw, StringComparison.OrdinalIgnoreCase)))
                {
                    AddOrMerge(elements, new ArchElement
                    {
                        Name = TruncateName(node.Title, 90),
                        ElementType = ArchElementType.Risk,
                        Description = TrimDescription(node.FullContent ?? node.Excerpt),
                        SourceSections = [section],
                        RelatedFrIds = ExtractFrIds(text),
                    });
                }
            }
        }
    }

    // ── Pass 2: regex scan ───────────────────────────────────────────────────

    private static void ExtractViaRegex(
        List<(SpecNode Node, string Section)> nodesWithSection,
        List<ArchElement> elements)
    {
        // Build section-grouped text blocks for efficient scanning
        var sections = BuildSectionBlocks(nodesWithSection);

        foreach (var (sectionName, sectionText, frIds, usIds) in sections)
        {
            // Services
            foreach (Match m in ServiceRe.Matches(sectionText))
            {
                var name = NormalizeName(m.Value);
                if (FalseServices.Contains(name)) continue;
                AddOrMerge(elements, new ArchElement
                {
                    Name = name,
                    ElementType = ArchElementType.Service,
                    SourceSections = [sectionName],
                    RelatedFrIds = frIds,
                    RelatedUsIds = usIds,
                });
            }

            // Domain events
            foreach (Match m in DomainEventRe.Matches(sectionText))
            {
                AddOrMerge(elements, new ArchElement
                {
                    Name = m.Value,
                    ElementType = ArchElementType.DomainEvent,
                    SourceSections = [sectionName],
                    RelatedFrIds = frIds,
                    RelatedUsIds = usIds,
                });
            }

            // Permissions
            foreach (Match m in PermissionRe.Matches(sectionText))
            {
                var permName = "Person:" + m.Groups[1].Value;
                AddOrMerge(elements, new ArchElement
                {
                    Name = permName,
                    ElementType = ArchElementType.Security,
                    Description = "Operation-based access control permission",
                    SourceSections = [sectionName],
                    RelatedFrIds = frIds,
                    RelatedUsIds = usIds,
                });
            }

            // Topic names (e.g. "person.person topic")
            foreach (Match m in TopicRe.Matches(sectionText))
            {
                var topicName = m.Groups[1].Value + " topic";
                AddOrMerge(elements, new ArchElement
                {
                    Name = topicName,
                    ElementType = ArchElementType.Messaging,
                    SourceSections = [sectionName],
                    RelatedFrIds = frIds,
                    RelatedUsIds = usIds,
                });
            }

            // Queue names (e.g. "operasjonsregistrering queue")
            foreach (Match m in QueueRe.Matches(sectionText))
            {
                var qName = m.Groups[1].Value.Trim();
                if (qName.Length > 3 && !string.Equals(qName, "the", StringComparison.OrdinalIgnoreCase))
                {
                    AddOrMerge(elements, new ArchElement
                    {
                        Name = qName + " queue",
                        ElementType = ArchElementType.Messaging,
                        SourceSections = [sectionName],
                        RelatedFrIds = frIds,
                        RelatedUsIds = usIds,
                    });
                }
            }

            // Service Bus
            if (sectionText.Contains("service bus", StringComparison.OrdinalIgnoreCase))
            {
                AddOrMerge(elements, new ArchElement
                {
                    Name = "Azure Service Bus",
                    ElementType = ArchElementType.Messaging,
                    Description = "Message broker for domain events, audit messages, and operational queues",
                    SourceSections = [sectionName],
                    RelatedFrIds = frIds,
                    RelatedUsIds = usIds,
                });
            }

            // Persistence names
            foreach (Match m in PersistenceNameRe.Matches(sectionText))
            {
                var name = m.Value;
                if (FalsePersistence.Contains(name)) continue;
                AddOrMerge(elements, new ArchElement
                {
                    Name = name,
                    ElementType = ArchElementType.Persistence,
                    SourceSections = [sectionName],
                    RelatedFrIds = frIds,
                    RelatedUsIds = usIds,
                });
            }
        }
    }

    // ── Pass 3: named full-text lookups ──────────────────────────────────────

    private static void ExtractNamedElements(
        string fullText,
        List<(SpecNode Node, string Section)> nodesWithSection,
        List<ArchElement> elements)
    {
        // External systems (multi-term search: name OR any alias must appear in fullText)
        foreach (var (name, searchTerms, desc) in ExternalSystems)
        {
            if (!searchTerms.Any(t => fullText.Contains(t, StringComparison.OrdinalIgnoreCase))) continue;
            var matchingNodes = nodesWithSection
                .Where(x => searchTerms.Any(t => GetNodeText(x.Node).Contains(t, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var sections = matchingNodes.Select(x => x.Section).Distinct().ToList();
            if (sections.Count == 0) sections = ["External Systems"];
            var combinedText = string.Join(" ", matchingNodes.Select(x => GetNodeText(x.Node)));
            AddOrMerge(elements, new ArchElement
            {
                Name = name,
                ElementType = ArchElementType.ExternalSystem,
                Description = desc,
                SourceSections = sections,
                RelatedFrIds = ExtractFrIds(combinedText),
                RelatedUsIds = ExtractUsIds(combinedText),
            });
        }

        // Architecture patterns
        foreach (var (name, keywords, desc) in ArchPatterns)
        {
            if (!keywords.Any(kw => fullText.Contains(kw, StringComparison.OrdinalIgnoreCase))) continue;
            var matchingNodes = nodesWithSection
                .Where(x => keywords.Any(kw => GetNodeText(x.Node).Contains(kw, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            var sections = matchingNodes.Select(x => x.Section).Distinct().ToList();
            AddOrMerge(elements, new ArchElement
            {
                Name = name,
                ElementType = ArchElementType.Pattern,
                Description = desc,
                SourceSections = sections,
            });
        }

        // Named API lookups — supplement Pass 1 for specs where section headings don't trigger
        // SectionSemantics.ApiSurface, so no ApiSurfaceItem nodes were created by the parser.
        (string Name, string[] Terms, string Desc)[] apiLookups =
        [
            ("GraphQL", ["graphql"], "GraphQL query/mutation API"),
            ("REST API", ["rest api", "restful api", "rest endpoint"], "RESTful HTTP API"),
        ];
        foreach (var (aName, terms, aDesc) in apiLookups)
        {
            if (!terms.Any(t => fullText.Contains(t, StringComparison.OrdinalIgnoreCase))) continue;
            if (elements.Any(e => e.ElementType == ArchElementType.Api &&
                    string.Equals(e.Name, aName, StringComparison.OrdinalIgnoreCase)))
                continue; // already captured by Pass 1 structured ApiSurfaceItem extraction
            AddOrMerge(elements, new ArchElement
            {
                Name = aName,
                ElementType = ArchElementType.Api,
                Description = aDesc,
                SourceSections = ["API Surface"],
            });
        }

        // Known Service Bus topic names using bare dotted notation in the spec ("person.person")
        // without the literal "topic" keyword that TopicRe requires.
        foreach (var knownTopic in new[] { "person.person", "person.barn" })
        {
            if (!fullText.Contains(knownTopic, StringComparison.OrdinalIgnoreCase)) continue;
            AddOrMerge(elements, new ArchElement
            {
                Name = knownTopic + " topic",
                ElementType = ArchElementType.Messaging,
                Description = "Azure Service Bus topic",
                SourceSections = ["Messaging"],
            });
        }

        // Azure deployment region
        if (fullText.Contains("norway east", StringComparison.OrdinalIgnoreCase) ||
            fullText.Contains("norwayeast", StringComparison.OrdinalIgnoreCase))
        {
            AddOrMerge(elements, new ArchElement
            {
                Name = "Azure Norway East",
                ElementType = ArchElementType.Service,
                Description = "Primary Azure deployment region; data sovereignty for Norwegian citizen data",
                SourceSections = ["Infrastructure"],
            });
        }

        // Kode 6/7 address protection
        if (fullText.Contains("Kode 6", StringComparison.OrdinalIgnoreCase) ||
            fullText.Contains("Kode 7", StringComparison.OrdinalIgnoreCase))
        {
            AddOrMerge(elements, new ArchElement
            {
                Name = "Kode 6/7 Address Protection",
                ElementType = ArchElementType.Security,
                Description = "Children under address-protection (Kode 6/7) require child-specific " +
                              "Person:SeGradertBarn permission; addresses must never be disclosed",
                SourceSections = ["Security"],
            });
        }

        // Fail closed authorisation
        if (fullText.Contains("fail closed", StringComparison.OrdinalIgnoreCase) ||
            fullText.Contains("fail-closed", StringComparison.OrdinalIgnoreCase))
        {
            AddOrMerge(elements, new ArchElement
            {
                Name = "Fail Closed Authorisation",
                ElementType = ArchElementType.Security,
                Description = "If the authorisation module is unavailable, all access is denied",
                SourceSections = ["Security"],
            });
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static IEnumerable<(SpecNode Node, string Section)> FlattenWithSections(
        IEnumerable<SpecNode> roots)
    {
        var result = new List<(SpecNode, string)>();
        FlattenInner(roots, "Specification", result);
        return result;
    }

    private static void FlattenInner(
        IEnumerable<SpecNode> nodes, string currentSection,
        List<(SpecNode, string)> result)
    {
        foreach (var node in nodes)
        {
            result.Add((node, currentSection));
            var childSection = node.HeadingLevel > 0 ? node.Title : currentSection;
            FlattenInner(node.Children, childSection, result);
        }
    }

    private static string GetNodeText(SpecNode n)
    {
        var parts = new[]
        {
            n.Title, n.Excerpt, n.FullContent,
            n.QuestionText, n.AnswerText,
            n.BddGiven, n.BddWhen, n.BddThen,
        };
        return string.Join(" ", parts.Where(p => p is not null));
    }

    private static IEnumerable<(string Section, string Text, List<string> FrIds, List<string> UsIds)>
        BuildSectionBlocks(List<(SpecNode Node, string Section)> nodesWithSection)
    {
        // Include ALL nodes: leaf spec items (HeadingLevel==0) AND heading nodes whose FullContent
        // carries prose that regex passes need to scan. Architecture terms listed as bullet-prose
        // under a heading (domain events, topic names, entity names) are stored in the heading
        // node's FullContent, NOT in leaf child nodes, so filtering to HeadingLevel==0 misses them.
        var groups = nodesWithSection
            .Where(x => x.Node.HeadingLevel == 0 ||
                        (x.Node.HeadingLevel > 0 && !string.IsNullOrWhiteSpace(x.Node.FullContent)))
            .GroupBy(x => x.Section);

        foreach (var g in groups)
        {
            var texts = g.Select(x => GetNodeText(x.Node)).ToList();
            var combined = string.Join("\n", texts);
            yield return (
                g.Key,
                combined,
                ExtractFrIds(combined),
                ExtractUsIds(combined)
            );
        }
    }

    private static string ExtractApiName(string title)
    {
        // "**GraphQL** — consumed by..." or "GraphQL — ..."
        var cleaned = title.Replace("**", "").Trim();
        var dashIdx = cleaned.IndexOf('—');
        if (dashIdx > 0) return cleaned[..dashIdx].Trim();
        var words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 0 ? words[0] : cleaned;
    }

    private static ArchElementType? ClassifyEntityNode(string name, string text)
    {
        var combined = (name + " " + text).ToLowerInvariant();
        if (combined.Contains("historikk") || combined.Contains("history") ||
            combined.Contains("database") || combined.Contains("table") ||
            combined.Contains("repository") || combined.Contains("outbox") ||
            combined.Contains("store") || combined.Contains("cache"))
            return ArchElementType.Persistence;
        if (combined.Contains("service") || combined.Contains("module") ||
            combined.Contains("adapter") || combined.Contains("system"))
            return ArchElementType.Service;
        if (combined.EndsWith("opprettet") || combined.EndsWith("oppdatert") ||
            combined.EndsWith("endret") || combined.EndsWith("registrert") ||
            combined.EndsWith("slettet"))
            return ArchElementType.DomainEvent;
        return null; // domain model entities — not architecture elements
    }

    private static List<string> ExtractFrIds(string text) =>
        FrIdRe.Matches(text).Select(m => m.Value).Distinct().ToList();

    private static List<string> ExtractUsIds(string text) =>
        UsIdRe.Matches(text).Select(m => m.Value.ToUpperInvariant()).Distinct().ToList();

    private static string NormalizeName(string s) => s.Trim();

    private static string TruncateName(string s, int maxLen)
    {
        if (s.Length <= maxLen) return s;
        var wb = s.LastIndexOf(' ', maxLen - 3);
        return wb > 0 ? s[..wb] + "…" : s[..maxLen] + "…";
    }

    private static string TrimDescription(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        if (s.Length <= 300) return s.Trim();
        var wb = s.LastIndexOf(' ', 297);
        return (wb > 50 ? s[..wb] : s[..297]) + "…";
    }

    private static void AddOrMerge(List<ArchElement> elements, ArchElement newElem)
    {
        var existing = elements.FirstOrDefault(e =>
            e.ElementType == newElem.ElementType &&
            string.Equals(e.Name, newElem.Name, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            foreach (var s in newElem.SourceSections.Where(s => !existing.SourceSections.Contains(s)))
                existing.SourceSections.Add(s);
            foreach (var id in newElem.RelatedFrIds.Where(id => !existing.RelatedFrIds.Contains(id)))
                existing.RelatedFrIds.Add(id);
            foreach (var id in newElem.RelatedUsIds.Where(id => !existing.RelatedUsIds.Contains(id)))
                existing.RelatedUsIds.Add(id);
        }
        else
        {
            elements.Add(newElem);
        }
    }
}
