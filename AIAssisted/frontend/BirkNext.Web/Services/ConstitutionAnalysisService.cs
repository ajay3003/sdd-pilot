using System.Text;
using System.Text.RegularExpressions;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public sealed class ConstitutionAnalysisService : IConstitutionAnalysisService
{
    // ── Regex patterns ─────────────────────────────────────────────────────

    private static readonly Regex MetaVersionRe = new(
        @"^\s*[Vv]ersion\s*[:=]\s*(.+)$", RegexOptions.Compiled);

    private static readonly Regex MetaRatifiedRe = new(
        @"^\s*[Rr]atified\s*[:=]\s*(.+)$", RegexOptions.Compiled);

    private static readonly Regex MetaAmendedRe = new(
        @"^\s*[Ll]ast[\s_][Aa]mended\s*[:=]\s*(.+)$", RegexOptions.Compiled);

    // Matches PP-NN at start of heading (Platform Principles)
    private static readonly Regex PrincipleIdRe = new(
        @"^(PP-\d+)\b\s*[:\-–]?\s*(.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches PS-NN at start of heading (Platform Standards)
    private static readonly Regex StandardIdRe = new(
        @"^(PS-\d+)\b\s*[:\-–]?\s*(.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches module/service principle IDs: MP-NN, H-NN, P-NN, FP-NN
    private static readonly Regex ModulePrincipleIdRe = new(
        @"^([MH]P|FP|P)-(\d+)\b\s*[:\-–]?\s*(.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches constraint prefix IDs at start of heading
    private static readonly Regex ConstraintIdRe = new(
        @"^(MC-\d+|AC-\d+|FC-\d+|SC-C\d+)\b\s*[:\-–]?\s*(.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches GV-NN at start of heading (Governance)
    private static readonly Regex GovernanceIdRe = new(
        @"^(GV-\d+)\b\s*[:\-–]?\s*(.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches roman numeral principles: "I.", "II.", "III.", etc.
    private static readonly Regex RomanNumeralPrincipleRe = new(
        @"^([IVX]+)\.\s+(.+)$", RegexOptions.Compiled);

    // Matches any recognized rule ID anywhere in text
    private static readonly Regex AnyRuleIdRe = new(
        @"\b(PP-\d+|PS-\d+|GL-\d+|MP-\d+|HP-\d+|FP-\d+|MC-\d+|AC-\d+|FC-\d+|GV-\d+|SC-C\d+|H-\d+|P-\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VersionEntryRe = new(
        @"^v?(\d+[\.\d]*)\s*[-–:]\s*(.+)$", RegexOptions.Compiled);

    // Strips parenthetical content for clean title (used when IDs are inside parens)
    private static readonly Regex ParenGroupRe = new(
        @"\s*\([^)]*\)", RegexOptions.Compiled);

    // ── Section keyword classifiers ────────────────────────────────────────

    private static readonly HashSet<string> PrincipleKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "core principles", "principles", "design principles", "architectural principles",
        "fundamental principles", "guiding principles", "platform principles", "module principles",
        "service principles", "authorization module principles",
    };

    private static readonly HashSet<string> StandardKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "platform standards", "standards", "technical standards", "development standards",
        "coding standards", "api standards", "service standards",
        "security", "authentication", "managed identity", "network isolation",
        "repository", "code structure", "internal structure", "repository convention",
        "inherited platform rules", "platform rules",
    };

    private static readonly HashSet<string> ConstraintKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "module constraints", "constraints", "rules", "module rules",
        "platform constraints", "authorization constraints", "frontend constraints",
        "service constraints", "security constraints", "regulatory constraints",
        "technology stack", "domain boundary", "operational standards",
    };

    private static readonly HashSet<string> GovernanceKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "governance", "amendment process", "compliance", "compliance rules",
        "versioning policy", "ratification", "enforcement",
    };

    private static readonly HashSet<string> ChangelogKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "changelog", "change log", "version history", "history", "revisions", "amendments",
    };

    // ── Public API ─────────────────────────────────────────────────────────

    public ConstitutionDocument Parse(string markdown)
    {
        var tokens = MarkdownTokenizer.Tokenize(markdown);

        string title = string.Empty;
        string version = string.Empty;
        string? ratifiedDate = null;
        string? lastAmendedDate = null;

        var principles = new List<ConstitutionPrinciple>();
        var standards = new List<ConstitutionStandard>();
        var constraints = new List<ConstitutionConstraint>();
        var governanceItems = new List<ConstitutionGovernanceItem>();
        var changelog = new List<ConstitutionVersion>();

        ConstitutionSectionType currentSection = ConstitutionSectionType.Other;
        var itemLines = new List<string>();
        string? currentItemHeading = null;
        string? implicitSectionHeading = null; // For sections with content but no level-3 headings

        void FlushItem()
        {
            if (currentItemHeading is null || itemLines.Count == 0) return;
            var raw = string.Join("\n", itemLines);

            switch (currentSection)
            {
                case ConstitutionSectionType.CorePrinciples:
                    var p = ParsePrinciple(currentItemHeading, raw);
                    if (p is not null) principles.Add(p);
                    break;
                case ConstitutionSectionType.PlatformStandards:
                case ConstitutionSectionType.DevelopmentStandards:
                    var s = ParseStandard(currentItemHeading, raw, currentSection);
                    if (s is not null) standards.Add(s);
                    break;
                case ConstitutionSectionType.ModuleConstraints:
                case ConstitutionSectionType.SecurityCompliance:
                    var c = ParseConstraint(currentItemHeading, raw, currentSection);
                    if (c is not null) constraints.Add(c);
                    break;
                case ConstitutionSectionType.Governance:
                    var g = ParseGovernanceItem(currentItemHeading, raw);
                    if (g is not null) governanceItems.Add(g);
                    break;
                case ConstitutionSectionType.Changelog:
                    var v = ParseVersionEntry(currentItemHeading, raw);
                    if (v is not null) changelog.Add(v);
                    break;
            }

            itemLines.Clear();
            currentItemHeading = null;
        }

        bool inMetaBlock = false;
        bool titleFound = false;

        foreach (var tok in tokens)
        {
            var line = tok.RawLine;

            if (tok.Kind == MarkdownTokenKind.Heading)
            {
                var level = tok.HeadingLevel;
                var rawTitle = tok.Content;

                if (level == 1 && !titleFound)
                {
                    title = StripMarkdown(rawTitle);
                    titleFound = true;
                    inMetaBlock = true;
                    continue;
                }

                if (level == 2)
                {
                    FlushItem();
                    inMetaBlock = false;
                    currentSection = ClassifySection(rawTitle);
                    currentItemHeading = null;
                    implicitSectionHeading = currentSection != ConstitutionSectionType.Other ? rawTitle : null;
                    continue;
                }

                if (level >= 3 && currentSection != ConstitutionSectionType.Other)
                {
                    FlushItem();
                    currentItemHeading = rawTitle;
                    implicitSectionHeading = null; // We have explicit level-3 heading, don't use implicit
                    continue;
                }
            }

            if (inMetaBlock || (tok.LineIndex < 20 && !string.IsNullOrWhiteSpace(title)))
            {
                var vm = MetaVersionRe.Match(line);
                if (vm.Success && string.IsNullOrEmpty(version))
                { version = vm.Groups[1].Value.Trim(); continue; }

                var rm = MetaRatifiedRe.Match(line);
                if (rm.Success && ratifiedDate is null)
                { ratifiedDate = rm.Groups[1].Value.Trim(); continue; }

                var am = MetaAmendedRe.Match(line);
                if (am.Success && lastAmendedDate is null)
                { lastAmendedDate = am.Groups[1].Value.Trim(); continue; }
            }

            // Handle table rows in Changelog section specially
            if (currentSection == ConstitutionSectionType.Changelog && tok.Kind == MarkdownTokenKind.TableRow)
            {
                var changelogEntry = ParseChangelogTableRow(tok.TableCells);
                if (changelogEntry is not null)
                    changelog.Add(changelogEntry);
                continue;
            }

            // If we have content but no explicit item heading, use the implicit section heading
            // This handles sections like "## Governance" that have no level-3 subsections
            // BUT: Skip this for Changelog and ModuleConstraints sections — only parse explicit level-3 headings
            // (SecurityCompliance can have implicit headings since it's typically platform-wide rules)
            var shouldUseImplicitHeading = currentSection != ConstitutionSectionType.Changelog
                && currentSection != ConstitutionSectionType.ModuleConstraints;

            if (currentItemHeading is null && implicitSectionHeading is not null && shouldUseImplicitHeading &&
                (tok.Kind == MarkdownTokenKind.BulletItem ||
                 (tok.Kind != MarkdownTokenKind.Blank && tok.Kind != MarkdownTokenKind.Heading)))
            {
                currentItemHeading = implicitSectionHeading;
                implicitSectionHeading = null;
            }

            if (currentItemHeading is not null)
                itemLines.Add(line);
        }

        FlushItem();

        if (string.IsNullOrEmpty(version))
        {
            var vm = Regex.Match(title, @"\bv?(\d+[\.\d]+)\s*$");
            if (vm.Success)
            {
                version = vm.Groups[1].Value;
                title = title[..vm.Index].Trim();
            }
        }

        var type = InferConstitutionType(title);
        var catalog = BuildRuleCatalog(principles, standards, constraints, governanceItems);
        var health = BuildHealth(principles, standards, constraints, governanceItems, changelog, catalog);

        return new ConstitutionDocument
        {
            Title = string.IsNullOrEmpty(title) ? "Constitution" : title,
            Version = version,
            RatifiedDate = ratifiedDate,
            LastAmendedDate = lastAmendedDate,
            Type = type,
            Principles = principles,
            Standards = standards,
            Constraints = constraints,
            GovernanceItems = governanceItems,
            Changelog = changelog,
            RuleCatalog = catalog,
            Health = health,
        };
    }

    // ── Search & filter ────────────────────────────────────────────────────

    public IEnumerable<ConstitutionPrinciple> SearchPrinciples(
        IEnumerable<ConstitutionPrinciple> principles, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return principles;
        return principles.Where(p =>
            MatchesSearch(query, p.Id, p.Title, p.Description)
            || p.Guidelines.Any(g => MatchesSearch(query, g))
            || p.ReferencedStandards.Any(r => MatchesSearch(query, r)));
    }

    public IEnumerable<ConstitutionStandard> SearchStandards(
        IEnumerable<ConstitutionStandard> standards, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return standards;
        return standards.Where(s =>
            MatchesSearch(query, s.Id, s.Title, s.Category, s.Description)
            || s.Rules.Any(r => MatchesSearch(query, r)));
    }

    public IEnumerable<ConstitutionConstraint> SearchConstraints(
        IEnumerable<ConstitutionConstraint> constraints, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return constraints;
        return constraints.Where(c =>
            MatchesSearch(query, c.Id, c.Title, c.Scope, c.Description)
            || c.Rules.Any(r => MatchesSearch(query, r)));
    }

    public IEnumerable<ConstitutionGovernanceItem> SearchGovernance(
        IEnumerable<ConstitutionGovernanceItem> items, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return items;
        return items.Where(g =>
            MatchesSearch(query, g.Title, g.Description)
            || g.Points.Any(pt => MatchesSearch(query, pt)));
    }

    public IEnumerable<ConstitutionRule> SearchRules(
        IEnumerable<ConstitutionRule> rules, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return rules;
        return rules.Where(r =>
            MatchesSearch(query, r.RuleId, r.Title, r.Description, r.RuleType.ToString())
            || r.Aliases.Any(a => MatchesSearch(query, a))
            || r.References.Any(rf => MatchesSearch(query, rf))
            || r.ReferencedBy.Any(rb => MatchesSearch(query, rb)));
    }

    public IEnumerable<ConstitutionRule> FilterRulesByType(
        IEnumerable<ConstitutionRule> rules, ConstitutionRuleType? type)
    {
        if (type is null) return rules;
        return rules.Where(r => r.RuleType == type);
    }

    // ── Map tree construction ──────────────────────────────────────────────

    public List<ConstitutionMapNode> BuildMapTree(IEnumerable<ConstitutionRule> catalog)
    {
        var allRules = catalog.ToList();

        // Build ID lookup including aliases so references resolve across alias boundaries
        var byId = new Dictionary<string, ConstitutionRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in allRules)
        {
            if (!string.IsNullOrEmpty(r.RuleId))
                byId.TryAdd(r.RuleId, r);
            foreach (var alias in r.Aliases)
                byId.TryAdd(alias, r);
        }

        // Rules that are not referenced by any other rule are map roots
        var referencedIds = new HashSet<string>(
            allRules.SelectMany(r => r.References),
            StringComparer.OrdinalIgnoreCase);

        var roots = allRules
            .Where(r => !string.IsNullOrEmpty(r.RuleId) && !referencedIds.Contains(r.RuleId)
                        && !r.Aliases.Any(a => referencedIds.Contains(a)))
            .OrderBy(r => r.RuleType)
            .ThenBy(r => r.RuleId)
            .ToList();

        // If no explicit hierarchy: group roots by RuleType so map has structure
        if (roots.Count == 0 || allRules.All(r => r.References.Count == 0))
        {
            roots = allRules
                .OrderBy(r => r.RuleType)
                .ThenBy(r => r.RuleId)
                .ToList();
        }

        // If still no roots found (all rules reference each other), fall back to Principles
        if (roots.Count == 0)
            roots = allRules.Where(r => r.RuleType == ConstitutionRuleType.Principle).ToList();

        var result = new List<ConstitutionMapNode>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
            result.Add(BuildMapNode(root, byId, visited, 0));

        return result;
    }

    public bool MatchesSearch(string query, params string?[] fields)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var ci = StringComparison.OrdinalIgnoreCase;
        return fields.Any(f => f?.Contains(query, ci) == true);
    }

    // ── Private: rule catalog ─────────────────────────────────────────────

    private static List<ConstitutionRule> BuildRuleCatalog(
        List<ConstitutionPrinciple> principles,
        List<ConstitutionStandard> standards,
        List<ConstitutionConstraint> constraints,
        List<ConstitutionGovernanceItem> governance)
    {
        // Tuple: (PrimaryId, Title, Desc, RuleType, RawText, TitleAliases)
        var mutableRules = new List<(string Id, string Title, string Desc,
            ConstitutionRuleType Type, string Raw, List<string> Aliases)>();

        int principleSeq = 0, standardSeq = 0, constraintSeq = 0, govSeq = 0;

        foreach (var p in principles)
        {
            var (primaryId, aliases) = ResolveItemId(p.Id, p.Title, "PP-",
                ref principleSeq, "PRINCIPLE");
            mutableRules.Add((primaryId, p.Title, p.Description,
                ConstitutionRuleType.Principle, p.RawText, aliases));
        }

        foreach (var s in standards)
        {
            var (primaryId, aliases) = ResolveItemId(s.Id, s.Title, "PS-",
                ref standardSeq, "STANDARD");
            mutableRules.Add((primaryId, s.Title, s.Description,
                ConstitutionRuleType.Standard, s.RawText, aliases));
        }

        foreach (var c in constraints)
        {
            var (primaryId, aliases) = ResolveConstraintId(c.Id, c.Title,
                ref constraintSeq);
            var ctype = InferConstraintRuleType(primaryId);
            mutableRules.Add((primaryId, c.Title, c.Description,
                ctype, c.RawText, aliases));
        }

        foreach (var g in governance)
        {
            var govIdFromTitle = FindFirstIdOfType(g.Title, "GV-");
            var govId = !string.IsNullOrEmpty(govIdFromTitle) ? govIdFromTitle
                      : $"GOV-{++govSeq:D3}";
            var aliases = ExtractRuleIds(g.Title)
                .Where(id => !id.Equals(govId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            mutableRules.Add((govId, g.Title, g.Description,
                ConstitutionRuleType.Governance, g.RawText, aliases));
        }

        // Build set of all explicitly known IDs (primaries + aliases)
        var knownIds = new HashSet<string>(
            mutableRules.SelectMany(r => new[] { r.Id }.Concat(r.Aliases)),
            StringComparer.OrdinalIgnoreCase);

        // Extract forward refs from BOTH title (contains embedded IDs) AND raw body
        var forwardRefs = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, title, _, _, raw, _) in mutableRules)
        {
            forwardRefs[id] = ExtractRuleIds(title + "\n" + raw)
                .Where(refId => !refId.Equals(id, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // Collect all referenced IDs and add implied rules for unknown ones
        var allReferencedIds = forwardRefs.Values
            .SelectMany(r => r)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var refId in allReferencedIds)
        {
            if (!knownIds.Contains(refId))
            {
                var impliedType = InferRuleTypeFromId(refId);
                mutableRules.Add((refId, refId, string.Empty, impliedType, string.Empty, []));
                knownIds.Add(refId);
            }
        }

        // Build bidirectional ReferencedBy map (resolve via primary + aliases)
        var referencedBy = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in knownIds) referencedBy[id] = [];

        // Also register aliases in referencedBy for resolution
        var aliasToId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, _, _, _, _, aliasList) in mutableRules)
        {
            foreach (var alias in aliasList)
                aliasToId.TryAdd(alias, id);
        }

        foreach (var (srcId, refs) in forwardRefs)
        {
            foreach (var targetId in refs)
            {
                // Resolve alias → primary ID if needed
                var resolved = aliasToId.TryGetValue(targetId, out var prim) ? prim : targetId;
                if (referencedBy.TryGetValue(resolved, out var list))
                    list.Add(srcId);
                else if (referencedBy.TryGetValue(targetId, out var list2))
                    list2.Add(srcId);
            }
        }

        return mutableRules.Select(r => new ConstitutionRule
        {
            RuleId = r.Id.ToUpperInvariant(),
            Title = r.Title,
            Description = r.Desc,
            RuleType = r.Type,
            Aliases = r.Aliases.Select(a => a.ToUpperInvariant()).ToList(),
            References = forwardRefs.TryGetValue(r.Id, out var fr) ? fr : [],
            ReferencedBy = referencedBy.TryGetValue(r.Id, out var rb) ? rb : [],
        })
        .OrderBy(r => r.RuleType)
        .ThenBy(r => r.RuleId)
        .ToList();
    }

    // Resolve primary ID + aliases for a principle/standard item
    private static (string PrimaryId, List<string> Aliases) ResolveItemId(
        string explicitId, string title, string preferredPrefix,
        ref int seq, string syntheticPrefix)
    {
        var primaryId = !string.IsNullOrEmpty(explicitId) ? explicitId.ToUpperInvariant()
            : FindFirstIdOfType(title, preferredPrefix) ?? string.Empty;

        if (string.IsNullOrEmpty(primaryId))
            primaryId = $"{syntheticPrefix}-{++seq:D3}";

        var aliases = ExtractRuleIds(title)
            .Where(id => !id.Equals(primaryId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return (primaryId, aliases);
    }

    // Resolve primary ID + aliases for a constraint item (handles MC/AC/FC/SC-C prefixes)
    private static (string PrimaryId, List<string> Aliases) ResolveConstraintId(
        string explicitId, string title, ref int seq)
    {
        var primaryId = !string.IsNullOrEmpty(explicitId) ? explicitId.ToUpperInvariant()
            : FindFirstConstraintId(title) ?? string.Empty;

        if (string.IsNullOrEmpty(primaryId))
            primaryId = $"CONSTRAINT-{++seq:D3}";

        var aliases = ExtractRuleIds(title)
            .Where(id => !id.Equals(primaryId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return (primaryId, aliases);
    }

    private static ConstitutionMapNode BuildMapNode(
        ConstitutionRule rule,
        Dictionary<string, ConstitutionRule> byId,
        HashSet<string> visited,
        int depth)
    {
        visited.Add(rule.RuleId);
        foreach (var alias in rule.Aliases) visited.Add(alias);

        var children = new List<ConstitutionMapNode>();
        if (depth < 5)
        {
            foreach (var refId in rule.References)
            {
                if (!visited.Contains(refId) && byId.TryGetValue(refId, out var child))
                    children.Add(BuildMapNode(child, byId, visited, depth + 1));
            }
        }

        return new ConstitutionMapNode
        {
            Rule = rule,
            Children = children,
            Depth = depth,
        };
    }

    // ── Private: section parsers ──────────────────────────────────────────

    private static ConstitutionPrinciple? ParsePrinciple(string heading, string body)
    {
        if (string.IsNullOrWhiteSpace(heading)) return null;

        string id;
        string title;

        var idMatch = PrincipleIdRe.Match(heading);
        if (idMatch.Success)
        {
            id = idMatch.Groups[1].Value.ToUpperInvariant();
            title = StripMarkdown(idMatch.Groups[2].Value.Trim());
        }
        else
        {
            // Look for PP-NN anywhere in the heading (e.g. in parentheses)
            id = FindFirstIdOfType(heading, "PP-") ?? string.Empty;
            title = StripMarkdown(heading);
        }

        var description = new StringBuilder();
        var guidelines = new List<string>();
        var referencedStandards = new List<string>();
        bool inGuidelines = false;
        bool inStandards = false;

        foreach (var tok in MarkdownTokenizer.Tokenize(body))
        {
            var trimmed = tok.RawLine.Trim();
            if (tok.Kind == MarkdownTokenKind.Blank) { inGuidelines = false; inStandards = false; continue; }

            if (trimmed.StartsWith("**Related Guidelines", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Related Guidelines", StringComparison.OrdinalIgnoreCase))
            { inGuidelines = true; inStandards = false; continue; }

            if (trimmed.StartsWith("**Referenced Standards", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Referenced Standards", StringComparison.OrdinalIgnoreCase))
            { inStandards = true; inGuidelines = false; continue; }

            if (tok.Kind == MarkdownTokenKind.BulletItem)
            {
                var content = StripMarkdown(tok.Content);
                if (inGuidelines) guidelines.Add(content);
                else if (inStandards) referencedStandards.Add(content);
                else description.AppendLine(content);
                continue;
            }

            if (!inGuidelines && !inStandards && tok.Kind != MarkdownTokenKind.Heading)
                description.AppendLine(StripMarkdown(trimmed));
        }

        return new ConstitutionPrinciple
        {
            Id = id,
            Title = string.IsNullOrEmpty(title) ? StripMarkdown(heading) : title,
            Description = description.ToString().Trim(),
            Guidelines = guidelines,
            ReferencedStandards = referencedStandards,
            RawText = body.Trim(),
        };
    }

    private static ConstitutionStandard? ParseStandard(string heading, string body, ConstitutionSectionType section)
    {
        if (string.IsNullOrWhiteSpace(heading)) return null;

        string id;
        string title;

        var idMatch = StandardIdRe.Match(heading);
        if (idMatch.Success)
        {
            id = idMatch.Groups[1].Value.ToUpperInvariant();
            title = StripMarkdown(idMatch.Groups[2].Value.Trim());
        }
        else
        {
            id = FindFirstIdOfType(heading, "PS-") ?? string.Empty;
            title = StripMarkdown(heading);
        }

        var category = section == ConstitutionSectionType.DevelopmentStandards
            ? "Development"
            : InferStandardCategory(heading);

        var description = new StringBuilder();
        var rules = new List<string>();

        foreach (var tok in MarkdownTokenizer.Tokenize(body))
        {
            if (tok.Kind == MarkdownTokenKind.Blank) continue;

            if (tok.Kind == MarkdownTokenKind.BulletItem)
            { rules.Add(StripMarkdown(tok.Content)); continue; }

            if (tok.Kind != MarkdownTokenKind.Heading)
                description.AppendLine(StripMarkdown(tok.RawLine.Trim()));
        }

        return new ConstitutionStandard
        {
            Id = id,
            Title = string.IsNullOrEmpty(title) ? StripMarkdown(heading) : title,
            Category = category,
            Description = description.ToString().Trim(),
            Rules = rules,
            RawText = body.Trim(),
        };
    }

    private static ConstitutionConstraint? ParseConstraint(string heading, string body, ConstitutionSectionType section)
    {
        if (string.IsNullOrWhiteSpace(heading)) return null;

        string id;
        string title;

        var idMatch = ConstraintIdRe.Match(heading);
        if (idMatch.Success)
        {
            id = idMatch.Groups[1].Value.ToUpperInvariant();
            title = StripMarkdown(idMatch.Groups[2].Value.Trim());
        }
        else
        {
            id = FindFirstConstraintId(heading) ?? string.Empty;
            title = StripMarkdown(heading);
        }

        // Determine scope: platform-wide OR module-specific
        // A constraint is platform-wide if:
        // 1. It's in the SecurityCompliance section (default), OR
        // 2. It explicitly contains "Platform" in its title (override)
        // Otherwise, if in ModuleConstraints section, it's module-specific
        var isPlatformWide = section == ConstitutionSectionType.SecurityCompliance
            || title.Contains("Platform", StringComparison.OrdinalIgnoreCase);

        var scope = InferConstraintScope(heading);

        var description = new StringBuilder();
        var rules = new List<string>();

        foreach (var tok in MarkdownTokenizer.Tokenize(body))
        {
            if (tok.Kind == MarkdownTokenKind.Blank) continue;

            if (tok.Kind == MarkdownTokenKind.BulletItem)
            { rules.Add(StripMarkdown(tok.Content)); continue; }

            if (tok.Kind != MarkdownTokenKind.Heading)
                description.AppendLine(StripMarkdown(tok.RawLine.Trim()));
        }

        return new ConstitutionConstraint
        {
            Id = id,
            Title = string.IsNullOrEmpty(title) ? StripMarkdown(heading) : title,
            Scope = scope,
            Description = description.ToString().Trim(),
            Rules = rules,
            IsPlatformWide = isPlatformWide,
            RawText = body.Trim(),
        };
    }

    private static ConstitutionGovernanceItem? ParseGovernanceItem(string heading, string body)
    {
        if (string.IsNullOrWhiteSpace(heading)) return null;

        var title = StripMarkdown(heading);
        var type = ClassifyGovernanceType(heading);

        var description = new StringBuilder();
        var points = new List<string>();

        foreach (var tok in MarkdownTokenizer.Tokenize(body))
        {
            if (tok.Kind == MarkdownTokenKind.Blank) continue;

            if (tok.Kind == MarkdownTokenKind.BulletItem)
            { points.Add(StripMarkdown(tok.Content)); continue; }

            if (tok.Kind != MarkdownTokenKind.Heading)
                description.AppendLine(StripMarkdown(tok.RawLine.Trim()));
        }

        return new ConstitutionGovernanceItem
        {
            Title = title,
            Description = description.ToString().Trim(),
            Type = type,
            Points = points,
            RawText = body.Trim(),
        };
    }

    private static ConstitutionVersion? ParseVersionEntry(string heading, string body)
    {
        if (string.IsNullOrWhiteSpace(heading)) return null;

        var em = VersionEntryRe.Match(heading);
        var ver = em.Success ? em.Groups[1].Value : heading.Trim().TrimStart('v', 'V');
        var dateRaw = em.Success ? em.Groups[2].Value.Trim() : string.Empty;

        string date = dateRaw;
        string author = string.Empty;
        var byIdx = dateRaw.IndexOf(" by ", StringComparison.OrdinalIgnoreCase);
        if (byIdx > 0) { date = dateRaw[..byIdx].Trim(); author = dateRaw[(byIdx + 4)..].Trim(); }

        var changes = new List<string>();
        foreach (var tok in MarkdownTokenizer.Tokenize(body))
        {
            if (tok.Kind == MarkdownTokenKind.BulletItem)
                changes.Add(StripMarkdown(tok.Content));
        }

        return new ConstitutionVersion { Version = ver, Date = date, Author = author, Changes = changes };
    }

    private static ConstitutionVersion? ParseChangelogTableRow(IReadOnlyList<string>? cells)
    {
        if (cells is null || cells.Count < 2) return null;

        // Table structure: | Version | Date | Change | Approver |
        // We expect at least: Version (cells[0]) and something else
        // Skip header and separator rows
        var version = cells.Count > 0 ? cells[0].Trim() : string.Empty;
        var date = cells.Count > 1 ? cells[1].Trim() : string.Empty;
        var change = cells.Count > 2 ? cells[2].Trim() : string.Empty;
        var approver = cells.Count > 3 ? cells[3].Trim() : string.Empty;

        // Skip if version cell is empty or is a header marker (Version, version, etc.)
        if (string.IsNullOrEmpty(version) ||
            version.Equals("Version", StringComparison.OrdinalIgnoreCase) ||
            version.Equals("---", StringComparison.OrdinalIgnoreCase))
            return null;

        // Parse version: remove leading 'v' or 'V' if present
        version = version.TrimStart('v', 'V').Trim();

        var changes = new List<string>();
        if (!string.IsNullOrWhiteSpace(change))
            changes.Add(change);

        return new ConstitutionVersion { Version = version, Date = date, Author = approver, Changes = changes };
    }

    // ── Health builder ─────────────────────────────────────────────────────

    private static ConstitutionHealth BuildHealth(
        List<ConstitutionPrinciple> principles,
        List<ConstitutionStandard> standards,
        List<ConstitutionConstraint> constraints,
        List<ConstitutionGovernanceItem> governance,
        List<ConstitutionVersion> changelog,
        List<ConstitutionRule> catalog)
    {
        var platformWide = constraints.Count(c => c.IsPlatformWide);
        var moduleLevel  = constraints.Count - platformWide;
        var totalRefs    = catalog.Sum(r => r.References.Count);

        // Only count rules that came from real sections (not implied-only rules)
        var sectionTotal = principles.Count + standards.Count + constraints.Count + governance.Count;

        // Orphan: no References AND no ReferencedBy
        var orphans   = catalog.Count(r => r.References.Count == 0 && r.ReferencedBy.Count == 0);
        var noOutbound = catalog.Count(r => r.References.Count == 0);

        // Broken: a referenced ID that doesn't appear in catalog as primary or alias
        var knownIds = new HashSet<string>(catalog.Select(r => r.RuleId), StringComparer.OrdinalIgnoreCase);
        foreach (var r in catalog)
            foreach (var a in r.Aliases) knownIds.Add(a);
        var broken = catalog.Sum(r => r.References.Count(refId => !knownIds.Contains(refId)));

        var indicators = new List<ConstitutionHealthIndicator>();

        var parseTotal = catalog.Count(r => !r.RuleId.StartsWith("PRINCIPLE-", StringComparison.Ordinal)
            && !r.RuleId.StartsWith("STANDARD-", StringComparison.Ordinal)
            && !r.RuleId.StartsWith("CONSTRAINT-", StringComparison.Ordinal)
            && !r.RuleId.StartsWith("GOV-", StringComparison.Ordinal));

        var totalRulesForDisplay = catalog.Count > 0 ? catalog.Count : sectionTotal;

        indicators.Add(new ConstitutionHealthIndicator
        {
            Icon = totalRulesForDisplay > 0 ? "✓" : "⚠",
            Message = totalRulesForDisplay > 0
                ? $"{totalRulesForDisplay} rules parsed — {principles.Count} principles, {standards.Count} standards, {constraints.Count} constraints"
                : "No structured rules found. Ensure section headings follow PP-NN / PS-NN conventions.",
            Level = totalRulesForDisplay > 0 ? HealthIndicatorLevel.Good : HealthIndicatorLevel.Warning,
        });

        if (totalRefs > 0)
            indicators.Add(new ConstitutionHealthIndicator
            {
                Icon = "✓", Message = $"{totalRefs} cross-references extracted",
                Level = HealthIndicatorLevel.Good,
            });

        if (orphans > 0)
            indicators.Add(new ConstitutionHealthIndicator
            {
                Icon = "⚠",
                Message = $"{orphans} orphan rule{(orphans != 1 ? "s" : "")} — not connected to any other rule",
                Level = HealthIndicatorLevel.Warning,
            });

        if (broken > 0)
            indicators.Add(new ConstitutionHealthIndicator
            {
                Icon = "⚠",
                Message = $"{broken} broken reference{(broken != 1 ? "s" : "")} — target rule IDs not found in catalog",
                Level = HealthIndicatorLevel.Warning,
            });

        // Only warn if governance section is truly missing (not just if items are unparsed)
        if (governance.Count == 0 && standards.Count > 2) // Only warn if we have substantial other sections
            indicators.Add(new ConstitutionHealthIndicator
            {
                Icon = "⚠", Message = "No governance section found",
                Level = HealthIndicatorLevel.Warning,
            });

        // Warn about missing changelog unless explicitly found
        if (changelog.Count == 0)
            indicators.Add(new ConstitutionHealthIndicator
            {
                Icon = "⚠", Message = "No changelog found",
                Level = HealthIndicatorLevel.Warning,
            });

        var summary = totalRulesForDisplay == 0
            ? "No structured content detected. Ensure headings follow PP-NN / PS-NN conventions."
            : $"{totalRulesForDisplay} rules, {totalRefs} references across {principles.Count} principles, {standards.Count} standards, {constraints.Count} constraints.";

        return new ConstitutionHealth
        {
            TotalPrinciples   = principles.Count,
            TotalStandards    = standards.Count,
            TotalConstraints  = constraints.Count,
            TotalGovernanceItems = governance.Count,
            TotalVersions     = changelog.Count,
            PlatformWideConstraints = platformWide,
            ModuleConstraints = moduleLevel,
            TotalRules        = totalRulesForDisplay,
            TotalReferences   = totalRefs,
            OrphanRules       = orphans,
            RulesWithoutReferences = noOutbound,
            BrokenReferences  = broken,
            HealthSummary     = summary,
            Indicators        = indicators,
        };
    }

    // ── Classifiers & helpers ─────────────────────────────────────────────

    private static ConstitutionSectionType ClassifySection(string heading)
    {
        var lower = heading.ToLowerInvariant();
        if (PrincipleKeywords.Any(k => lower.Contains(k.ToLowerInvariant()))) return ConstitutionSectionType.CorePrinciples;
        if (ChangelogKeywords.Any(k => lower.Contains(k.ToLowerInvariant()))) return ConstitutionSectionType.Changelog;
        if (GovernanceKeywords.Any(k => lower.Contains(k.ToLowerInvariant()))) return ConstitutionSectionType.Governance;

        // Check StandardKeywords BEFORE generic "security" to classify "Security & Authentication" as Standards
        if (StandardKeywords.Any(k => lower.Contains(k.ToLowerInvariant()))) return ConstitutionSectionType.PlatformStandards;

        if (lower.Contains("security") || lower.Contains("compliance")) return ConstitutionSectionType.SecurityCompliance;
        if (ConstraintKeywords.Any(k => lower.Contains(k.ToLowerInvariant()))) return ConstitutionSectionType.ModuleConstraints;
        if (lower.Contains("development")) return ConstitutionSectionType.DevelopmentStandards;
        return ConstitutionSectionType.Other;
    }

    private static GovernanceItemType ClassifyGovernanceType(string heading)
    {
        var lower = heading.ToLowerInvariant();
        if (lower.Contains("amendment") || lower.Contains("amend")) return GovernanceItemType.AmendmentProcess;
        if (lower.Contains("compliance") || lower.Contains("enforcement")) return GovernanceItemType.ComplianceRules;
        if (lower.Contains("version") || lower.Contains("semver")) return GovernanceItemType.VersioningPolicy;
        return GovernanceItemType.Other;
    }

    private static ConstitutionType InferConstitutionType(string title)
    {
        var lower = title.ToLowerInvariant();
        if (lower.Contains("module")) return ConstitutionType.Module;
        if (lower.Contains("platform")) return ConstitutionType.Platform;
        if (lower.Contains("frontend") || lower.Contains("ui") || lower.Contains("client")) return ConstitutionType.Frontend;
        if (lower.Contains("service") || lower.Contains("api") || lower.Contains("backend")) return ConstitutionType.Service;
        return ConstitutionType.Generic;
    }

    private static string InferStandardCategory(string heading)
    {
        var lower = heading.ToLowerInvariant();
        if (lower.Contains("identity") || lower.Contains("auth")) return "Identity & Auth";
        if (lower.Contains("api") || lower.Contains("versioning")) return "API";
        if (lower.Contains("security")) return "Security";
        if (lower.Contains("data") || lower.Contains("storage") || lower.Contains("database")) return "Data";
        if (lower.Contains("logging") || lower.Contains("observ") || lower.Contains("monitor")) return "Observability";
        if (lower.Contains("deploy") || lower.Contains("pipeline") || lower.Contains("ci")) return "Deployment";
        return "General";
    }

    private static string InferConstraintScope(string heading)
    {
        var lower = heading.ToLowerInvariant();
        if (lower.Contains("person")) return "Person Module";
        if (lower.Contains("authoriz") || lower.Contains("permission")) return "Authorization";
        if (lower.Contains("frontend") || lower.Contains("ui")) return "Frontend";
        if (lower.Contains("service")) return "Service";
        if (lower.Contains("platform")) return "Platform";
        return StripMarkdown(heading);
    }

    private static ConstitutionRuleType InferRuleTypeFromId(string id)
    {
        var upper = id.ToUpperInvariant();
        if (upper.StartsWith("PP-")) return ConstitutionRuleType.Principle;
        if (upper.StartsWith("PS-")) return ConstitutionRuleType.Standard;
        if (upper.StartsWith("GL-") || upper.StartsWith("FP-")) return ConstitutionRuleType.Guideline;
        if (upper.StartsWith("MC-") || upper.StartsWith("AC-") || upper.StartsWith("FC-")) return ConstitutionRuleType.Constraint;
        if (upper.StartsWith("GV-")) return ConstitutionRuleType.Governance;
        return ConstitutionRuleType.Guideline;
    }

    private static ConstitutionRuleType InferConstraintRuleType(string id)
    {
        // All constraint-family IDs are Constraint type
        _ = id;
        return ConstitutionRuleType.Constraint;
    }

    // Find the first ID of a given prefix within text (anywhere, not just at start)
    private static string? FindFirstIdOfType(string text, string prefix)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return AnyRuleIdRe.Matches(text)
            .Select(m => m.Groups[1].Value.ToUpperInvariant())
            .FirstOrDefault(id => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    // Find the first constraint-family ID anywhere in text
    private static string? FindFirstConstraintId(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return AnyRuleIdRe.Matches(text)
            .Select(m => m.Groups[1].Value.ToUpperInvariant())
            .FirstOrDefault(id =>
                id.StartsWith("MC-", StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith("AC-", StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith("FC-", StringComparison.OrdinalIgnoreCase) ||
                id.StartsWith("SC-C", StringComparison.OrdinalIgnoreCase));
    }

    // Extract all recognized rule IDs from a block of text
    private static List<string> ExtractRuleIds(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var allIds = new List<string>();

        // First, try to extract ranges and expand them
        var expandedRanges = ExpandRangeReferences(text);
        allIds.AddRange(expandedRanges);

        // Then extract individual IDs (that aren't part of a range)
        var individualIds = AnyRuleIdRe.Matches(text)
            .Select(m => m.Groups[1].Value.ToUpperInvariant())
            .ToList();

        // Remove IDs that are already covered by ranges
        foreach (var id in individualIds)
        {
            if (!allIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                allIds.Add(id);
        }

        return allIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> ExpandRangeReferences(string text)
    {
        var expandedIds = new List<string>();

        // Match patterns like "PP-01 through PP-09", "PP-01–PP-09", "GL-01–GL-29", etc.
        var rangePatterns = new[]
        {
            @"(PP|PS|GL|MP|HP|FP|MC|AC|FC|GV|SC-C|H|P)-(\d+)\s+through\s+(PP|PS|GL|MP|HP|FP|MC|AC|FC|GV|SC-C|H|P)-(\d+)",  // "PP-01 through PP-09"
            @"(PP|PS|GL|MP|HP|FP|MC|AC|FC|GV|SC-C|H|P)-(\d+)\s+to\s+(PP|PS|GL|MP|HP|FP|MC|AC|FC|GV|SC-C|H|P)-(\d+)",       // "PP-01 to PP-09"
            @"(PP|PS|GL|MP|HP|FP|MC|AC|FC|GV|SC-C|H|P)-(\d+)–(PP|PS|GL|MP|HP|FP|MC|AC|FC|GV|SC-C|H|P)-(\d+)",               // "PP-01–PP-09" (en dash)
            @"(PP|PS|GL|MP|HP|FP|MC|AC|FC|GV|SC-C|H|P)-(\d+)-(PP|PS|GL|MP|HP|FP|MC|AC|FC|GV|SC-C|H|P)-(\d+)",                // "PP-01-PP-09" (hyphen)
        };

        foreach (var pattern in rangePatterns)
        {
            var regex = new Regex(pattern, RegexOptions.IgnoreCase);
            foreach (Match match in regex.Matches(text))
            {
                var startPrefix = match.Groups[1].Value.ToUpperInvariant();
                if (!int.TryParse(match.Groups[2].Value, out var startNum)) continue;

                var endPrefix = match.Groups[3].Value.ToUpperInvariant();
                if (!int.TryParse(match.Groups[4].Value, out var endNum)) continue;

                // Only expand if prefixes match (e.g., PP-01 through PP-09, not PP-01 through GL-09)
                if (!startPrefix.Equals(endPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Expand the range
                var start = Math.Min(startNum, endNum);
                var end = Math.Max(startNum, endNum);

                // Determine zero-padding from the original numbers
                var padding = match.Groups[2].Value.Length;

                for (int i = start; i <= end; i++)
                {
                    var paddedNum = i.ToString().PadLeft(padding, '0');
                    var id = $"{startPrefix}-{paddedNum}";
                    if (!expandedIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                        expandedIds.Add(id);
                }
            }
        }

        return expandedIds;
    }

    private static string StripMarkdown(string s) =>
        Regex.Replace(s, @"[*_`#\[\]]", "").Trim();

    // Extract version from HTML metadata comment at the beginning (Sync Impact Report)
    private static string? ExtractVersionFromMetadata(string markdown)
    {
        var commentMatch = Regex.Match(markdown, @"<!--\s*Sync Impact Report.*?Version change:\s*([^→\n]+)\s*→\s*([^<\n]+)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (commentMatch.Success)
        {
            var newVersion = commentMatch.Groups[2].Value.Trim();
            if (!string.IsNullOrEmpty(newVersion))
                return newVersion;
        }
        return null;
    }

    // ── Build Semantic Model ───────────────────────────────────────────────

    public static ConstitutionSemanticModel BuildSemanticModel(ConstitutionDocument document)
    {
        var principles = document.Principles
            .Select(p => new SemanticConstitutionPrinciple
            {
                Id = p.Id,
                Title = p.Title,
                Description = p.Description,
                RelatedRuleIds = ExtractRuleIds(p.Description ?? "")
                    .Where(id => !id.StartsWith("PP-", StringComparison.OrdinalIgnoreCase))
                    .ToList(),
            })
            .ToList();

        var rules = document.RuleCatalog
            .Select(r => new SemanticConstitutionRule
            {
                Id = r.RuleId,
                Title = r.Title,
                Description = r.Description,
                Category = r.RuleType.ToString(),
                RelatedPrincipleIds = document.Principles
                    .Where(p => p.Guidelines.Any(g => g.Contains(r.RuleId, StringComparison.OrdinalIgnoreCase)))
                    .Select(p => p.Id)
                    .ToList(),
                ApplicableRequirementIds = [],
            })
            .ToList();

        var gates = document.RuleCatalog
            .Where(r => r.RuleType == ConstitutionRuleType.Governance)
            .Select(r => new SemanticConstitutionGate
            {
                Id = r.RuleId,
                Title = r.Title,
                Status = "NotApplicable",
                LinkedRuleIds = [r.RuleId],
            })
            .ToList();

        var complianceChecks = document.RuleCatalog
            .Select(r => new SemanticConstitutionComplianceCheckItem
            {
                RuleId = r.RuleId,
                RuleTitle = r.Title,
                Status = "NeedsReview",
            })
            .ToList();

        return new ConstitutionSemanticModel
        {
            Title = document.Title,
            Version = document.Version,
            CreatedDate = document.RatifiedDate,
            LastUpdated = document.LastAmendedDate,
            Principles = principles,
            Rules = rules,
            Gates = gates,
            ComplianceChecks = complianceChecks,
            RuleToRequirements = [],
            GateToRequirements = [],
        };
    }
}
