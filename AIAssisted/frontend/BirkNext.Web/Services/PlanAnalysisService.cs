using System.Text;
using System.Text.RegularExpressions;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public sealed class PlanAnalysisService : IPlanAnalysisService
{
    // ── Regex ─────────────────────────────────────────────────────────────────

    private static readonly Regex HeadingRe     = new(@"^(#{1,6})\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex BulletRe       = new(@"^\s*[-*+]\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex CodeFenceRe    = new(@"^```", RegexOptions.Compiled);
    private static readonly Regex BoldPropertyRe = new(@"\*\*([^*]+)\*\*\s*:?\s*(.+)", RegexOptions.Compiled);

    // Allow leading/trailing whitespace — real plans sometimes indent table rows
    private static readonly Regex TableRowRe = new(@"^\s*\|(.+)\|\s*$", RegexOptions.Compiled);
    private static readonly Regex TableSepRe = new(@"^\s*\|[\s\-\|:]+\|\s*$", RegexOptions.Compiled);

    private static readonly Regex RuleIdRe = new(
        @"\b(PP-\d+|PS-\d+|GL-\d+|FP-\d+|MC-\d+|AC-\d+|FC-\d+|GV-\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AdrIdRe = new(
        @"^(ADR-\d+)\b\s*[:\-–]?\s*(.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex VersionRe = new(
        @"\s+(\d+[\.\d]*(?:[-+]\S*)?)\s*$", RegexOptions.Compiled);

    private static readonly Regex PhaseNumberRe = new(
        @"^(?:phase\s+)?(\d+)[:\-–.\s]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Meta regexes ────────────────────────────────────────────────────────

    private static readonly (string Key, Regex Re)[] MetaPatterns =
    [
        ("status",  new Regex(@"^\s*\*?\*?[Ss]tatus\*?\*?\s*[:=]\s*\*?\*?(.+?)\*?\*?\s*$",  RegexOptions.Compiled)),
        ("created", new Regex(@"^\s*\*?\*?[Cc]reated?\*?\*?\s*[:=]\s*(.+)$",                RegexOptions.Compiled)),
        ("updated", new Regex(@"^\s*\*?\*?(?:[Ll]ast[\s_][Uu]pdated?|[Uu]pdated?)\*?\*?\s*[:=]\s*(.+)$", RegexOptions.Compiled)),
        ("author",  new Regex(@"^\s*\*?\*?[Aa]uthor\*?\*?\s*[:=]\s*(.+)$",                  RegexOptions.Compiled)),
        ("feature", new Regex(@"^\s*\*?\*?[Ff]eature\*?\*?\s*[:=]\s*(.+)$",                 RegexOptions.Compiled)),
        ("branch",  new Regex(@"^\s*\*?\*?[Bb]ranch\*?\*?\s*[:=]\s*(.+)$",                  RegexOptions.Compiled)),
        ("date",    new Regex(@"^\s*\*?\*?[Dd]ate\*?\*?\s*[:=]\s*(.+)$",                    RegexOptions.Compiled)),
        ("spec",    new Regex(@"^\s*\*?\*?[Ss]pec(?:ification)?\s*(?:[Ll]ink)?\*?\*?\s*[:=]\s*(.+)$", RegexOptions.Compiled)),
        ("input",   new Regex(@"^\s*\*?\*?[Ii]nput(?:\s+[Ss]ource)?\*?\*?\s*[:=]\s*(.+)$", RegexOptions.Compiled)),
    ];

    // ── Section classifiers ──────────────────────────────────────────────────

    private static readonly HashSet<string> TechContextKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "technical context", "tech context", "technology", "tech stack", "background",
        "current state", "context", "technical background", "environment",
    };

    private static readonly HashSet<string> ArchitectureKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "architecture", "design", "technical design", "system design",
        "architectural decisions", "adr", "design decisions",
        "architecture notes", "architecture overview", "architecture and design",
        "technical decisions", "decision records", "decisions",
        "architecture design", "system architecture", "solution design",
    };

    private static readonly HashSet<string> ProjectStructureKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "project structure", "file structure", "directory structure", "code structure",
        "folder structure", "files", "structure", "codebase structure",
    };

    private static readonly HashSet<string> RiskKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "risks", "risk", "risk assessment", "risk analysis", "risks & mitigations",
        "risks and mitigations", "risk register",
    };

    private static readonly HashSet<string> ComplexityKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "complexity", "complexity tracking", "complexity analysis", "complexity assessment",
        "technical complexity",
    };

    private static readonly HashSet<string> DependencyKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "dependencies", "dependency", "external dependencies", "internal dependencies",
        "packages", "libraries", "integrations",
    };

    private static readonly HashSet<string> MilestoneKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "milestones", "milestone", "timeline", "schedule", "deliverables",
    };

    private static readonly HashSet<string> ConstitutionCheckKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "constitution check", "compliance check", "constitution compliance",
        "governance check", "rule compliance", "constitutional review",
        "constitution gate", "gates", "gate review",
        "gate check", "pp gates", "ps gates", "principle gates", "standard gates",
        "compliance gates", "rule gates", "constitution gates", "compliance review",
        "gate summary", "rule check",
    };

    private static readonly HashSet<string> ImplementationKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "implementation", "implementation plan", "implementation phases",
        "execution plan", "delivery plan", "phase plan",
        "implementation order", "migration steps", "rollout plan",
    };

    private static readonly HashSet<string> TestingKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "testing", "test strategy", "test plan", "quality assurance", "qa strategy",
        "testing strategy", "test approach", "testing approach",
    };

    private static readonly HashSet<string> ConstraintKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "constraints", "performance goals", "non-functional requirements", "nfr",
        "performance requirements", "scale", "scope constraints",
    };

    private static readonly HashSet<string> SummaryKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "summary", "overview", "executive summary", "plan summary",
    };

    private static readonly HashSet<string> TestingFrameworks = new(StringComparer.OrdinalIgnoreCase)
    {
        "xunit", "nunit", "mstest", "moq", "nsubstitute", "shouldly", "fluentassertions",
        "testcontainers", "specflow", "playwright", "selenium", "bunit",
        "bogus", "autofixture", "faker",
    };

    // ── Public API ───────────────────────────────────────────────────────────

    public PlanDocument Parse(string markdown)
    {
        var tokens = MarkdownTokenizer.Tokenize(markdown);

        string title = string.Empty;
        string? featureName = null;
        string? status = null;
        string? createdDate = null;
        string? lastUpdated = null;
        string? author = null;
        string? branch = null;
        string? date = null;
        string? specLink = null;
        string? inputSource = null;
        string? summary = null;

        var sections        = new List<PlanSection>();
        var risks           = new List<PlanRisk>();
        var constraints     = new List<PlanConstraint>();
        var decisions       = new List<PlanArchitectureDecision>();
        var complexityItems = new List<PlanComplexityItem>();
        var dependencies    = new List<PlanDependency>();
        var milestones      = new List<PlanMilestone>();
        var checkItems      = new List<PlanConstitutionCheckItem>();
        var gates           = new List<PlanGate>();
        var phases          = new List<PlanImplementationPhase>();
        PlanTestingInfo? testingInfo = null;

        PlanSectionType currentSection = PlanSectionType.Other;
        string sectionHeading = string.Empty;
        var sectionLines = new List<string>();

        void FlushSection()
        {
            if (sectionLines.Count == 0) return;
            var raw = string.Join("\n", sectionLines);

            switch (currentSection)
            {
                case PlanSectionType.TechnicalContext:
                case PlanSectionType.ProjectStructure:
                    var sec = ParseFreeFormSection(sectionHeading, raw, currentSection);
                    if (sec is not null) sections.Add(sec);
                    break;
                case PlanSectionType.Architecture:
                    ParseArchitectureSection(raw, decisions, sections, sectionHeading);
                    break;
                case PlanSectionType.Risks:
                    ParseRisksSection(raw, risks, constraints);
                    break;
                case PlanSectionType.Constraints:
                    ParseConstraintsSection(raw, constraints);
                    break;
                case PlanSectionType.Complexity:
                    ParseComplexitySection(raw, complexityItems);
                    break;
                case PlanSectionType.Dependencies:
                    ParseDependenciesSection(raw, dependencies);
                    break;
                case PlanSectionType.Milestones:
                    ParseMilestonesSection(raw, milestones);
                    break;
                case PlanSectionType.ConstitutionCheck:
                    ParseConstitutionCheckSection(raw, checkItems, gates);
                    break;
                case PlanSectionType.ImplementationPhases:
                    ParseImplementationPhasesSection(raw, phases);
                    break;
                case PlanSectionType.Testing:
                    testingInfo = ParseTestingSection(raw);
                    break;
                case PlanSectionType.Other:
                    var otherSec = ParseFreeFormSection(sectionHeading, raw, PlanSectionType.Other);
                    if (otherSec is not null) sections.Add(otherSec);
                    break;
            }

            sectionLines.Clear();
        }

        bool inMetaBlock = false;
        bool titleFound = false;
        bool summaryExtracted = false;

        for (int i = 0; i < tokens.Count; i++)
        {
            var tok = tokens[i];

            if (tok.Kind == MarkdownTokenKind.Heading)
            {
                var level    = tok.HeadingLevel;
                var rawTitle = tok.Content;

                if (level == 1 && !titleFound)
                {
                    title = StripMarkdown(rawTitle);
                    var colonIdx = title.IndexOf(':');
                    if (colonIdx > 0 && colonIdx < title.Length - 1)
                        featureName = title[(colonIdx + 1)..].Trim();
                    titleFound = true;
                    inMetaBlock = true;
                    continue;
                }

                if (level == 2)
                {
                    if (!summaryExtracted && sectionLines.Count > 0
                        && currentSection == PlanSectionType.Other)
                    {
                        var candidate = string.Join("\n", sectionLines).Trim();
                        if (!string.IsNullOrEmpty(candidate) && candidate.Length > 20)
                            summary = candidate;
                        summaryExtracted = true;
                        sectionLines.Clear();
                    }

                    FlushSection();
                    inMetaBlock = false;

                    if (SummaryKeywords.Any(k => rawTitle.ToLowerInvariant().Contains(k.ToLowerInvariant())))
                    {
                        currentSection = PlanSectionType.Other;
                        sectionHeading = StripMarkdown(rawTitle);
                        summaryExtracted = true;
                        continue;
                    }

                    currentSection = ClassifySection(rawTitle);
                    sectionHeading = StripMarkdown(rawTitle);
                    continue;
                }
            }

            if (inMetaBlock && tok.LineIndex < 35)
            {
                bool matched = false;
                foreach (var (key, re) in MetaPatterns)
                {
                    var m = re.Match(tok.RawLine);
                    if (!m.Success) continue;
                    var val = StripMarkdown(m.Groups[1].Value.Trim());
                    matched = true;
                    switch (key)
                    {
                        case "status":  status      = val; break;
                        case "created": createdDate = val; break;
                        case "updated": lastUpdated = val; break;
                        case "author":  author      = val; break;
                        case "feature": featureName = val; break;
                        case "branch":  branch      = val; break;
                        case "date":    date        = val; break;
                        case "spec":    specLink    = val; break;
                        case "input":   inputSource = val; break;
                    }
                    break;
                }
                if (matched) continue;
            }

            sectionLines.Add(tok.RawLine);
        }

        FlushSection();

        if (string.IsNullOrEmpty(createdDate) && !string.IsNullOrEmpty(date))
            createdDate = date;

        // Auto-generate complexity items when no dedicated Complexity section exists
        if (complexityItems.Count == 0)
            complexityItems.AddRange(AutoGenerateComplexity(constraints, dependencies, sections, risks));

        var health = BuildHealth(
            summary, risks, constraints, decisions, complexityItems,
            dependencies, milestones, checkItems, gates, phases, testingInfo, sections,
            !string.IsNullOrEmpty(branch) || !string.IsNullOrEmpty(author));

        return new PlanDocument
        {
            Title = string.IsNullOrEmpty(title) ? "Plan" : title,
            FeatureName = featureName,
            Status = status,
            CreatedDate = createdDate,
            LastUpdated = lastUpdated,
            Author = author,
            Branch = branch,
            Date = date,
            SpecLink = specLink,
            InputSource = inputSource,
            Summary = summary,
            Sections = sections,
            Risks = risks,
            Constraints = constraints,
            ArchitectureDecisions = decisions,
            ComplexityItems = complexityItems,
            Dependencies = dependencies,
            Milestones = milestones,
            ConstitutionCheckItems = checkItems,
            Gates = gates,
            Phases = phases,
            TestingInfo = testingInfo,
            Health = health,
        };
    }

    // ── Search & filter ──────────────────────────────────────────────────────

    public IEnumerable<PlanRisk> SearchRisks(IEnumerable<PlanRisk> risks, string q)
    {
        if (string.IsNullOrWhiteSpace(q)) return risks;
        return risks.Where(r => MatchesSearch(q, r.Title, r.Description, r.Mitigation, r.Area, r.Severity.ToString()));
    }

    public IEnumerable<PlanRisk> FilterRisksBySeverity(IEnumerable<PlanRisk> risks, RiskSeverity? severity)
    {
        if (severity is null) return risks;
        return risks.Where(r => r.Severity == severity);
    }

    public IEnumerable<PlanConstraint> SearchConstraints(IEnumerable<PlanConstraint> constraints, string q)
    {
        if (string.IsNullOrWhiteSpace(q)) return constraints;
        return constraints.Where(c => MatchesSearch(q, c.Title, c.Description, c.ConstraintType.ToString()));
    }

    public IEnumerable<PlanConstraint> FilterConstraintsByType(IEnumerable<PlanConstraint> constraints, ConstraintType? type)
    {
        if (type is null) return constraints;
        return constraints.Where(c => c.ConstraintType == type);
    }

    public IEnumerable<PlanArchitectureDecision> SearchDecisions(IEnumerable<PlanArchitectureDecision> decisions, string q)
    {
        if (string.IsNullOrWhiteSpace(q)) return decisions;
        return decisions.Where(d =>
            MatchesSearch(q, d.Id, d.Title, d.Context, d.Decision, d.Rationale)
            || d.Consequences.Any(c => MatchesSearch(q, c)));
    }

    public IEnumerable<PlanComplexityItem> SearchComplexity(IEnumerable<PlanComplexityItem> items, string q)
    {
        if (string.IsNullOrWhiteSpace(q)) return items;
        return items.Where(c => MatchesSearch(q, c.Area, c.Notes, c.Level.ToString())
            || c.Factors.Any(f => MatchesSearch(q, f)));
    }

    public IEnumerable<PlanComplexityItem> FilterComplexityByLevel(IEnumerable<PlanComplexityItem> items, ComplexityLevel? level)
    {
        if (level is null) return items;
        return items.Where(c => c.Level == level);
    }

    public IEnumerable<PlanConstitutionCheckItem> SearchConstitutionCheck(IEnumerable<PlanConstitutionCheckItem> items, string q)
    {
        if (string.IsNullOrWhiteSpace(q)) return items;
        return items.Where(i => MatchesSearch(q, i.RuleId, i.Title, i.Notes, i.Status.ToString()));
    }

    public IEnumerable<PlanConstitutionCheckItem> FilterCheckByStatus(IEnumerable<PlanConstitutionCheckItem> items, ConstitutionCheckStatus? status)
    {
        if (status is null) return items;
        return items.Where(i => i.Status == status);
    }

    public IEnumerable<PlanGate> SearchGates(IEnumerable<PlanGate> gates, string q)
    {
        if (string.IsNullOrWhiteSpace(q)) return gates;
        return gates.Where(g => MatchesSearch(q, g.Gate, g.RuleId, g.Principle, g.Evidence, g.Notes, g.Status.ToString()));
    }

    public IEnumerable<PlanGate> FilterGatesByStatus(IEnumerable<PlanGate> gates, PlanGateStatus? status)
    {
        if (status is null) return gates;
        return gates.Where(g => g.Status == status);
    }

    public IEnumerable<PlanImplementationPhase> SearchPhases(IEnumerable<PlanImplementationPhase> phases, string q)
    {
        if (string.IsNullOrWhiteSpace(q)) return phases;
        return phases.Where(p =>
            MatchesSearch(q, p.Title, p.Description)
            || p.Tasks.Any(t => MatchesSearch(q, t))
            || p.Checks.Any(c => MatchesSearch(q, c)));
    }

    public bool MatchesSearch(string query, params string?[] fields)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var ci = StringComparison.OrdinalIgnoreCase;
        return fields.Any(f => f?.Contains(query, ci) == true);
    }

    // ── Section parsers ──────────────────────────────────────────────────────

    private static PlanSection? ParseFreeFormSection(string heading, string raw, PlanSectionType type)
    {
        if (string.IsNullOrWhiteSpace(heading) && string.IsNullOrWhiteSpace(raw)) return null;

        var blocks = new List<PlanSectionBlock>();
        var tokens = MarkdownTokenizer.Tokenize(raw);
        string? currentHeading = null;
        var currentBullets = new List<string>();
        var paragraphSb = new StringBuilder();
        var codeSb = new StringBuilder();

        void FlushBlock()
        {
            var para = paragraphSb.ToString().Trim();
            if (!string.IsNullOrEmpty(para) || currentBullets.Count > 0)
            {
                var block = new PlanSectionBlock
                {
                    SubHeading = currentHeading,
                    Paragraph = string.IsNullOrEmpty(para) ? null : para,
                    BulletPoints = [.. currentBullets],
                };
                if (block.HasContent || currentHeading is not null) blocks.Add(block);
            }
            else if (currentHeading is not null)
            {
                blocks.Add(new PlanSectionBlock { SubHeading = currentHeading });
            }
            currentHeading = null;
            currentBullets = [];
            paragraphSb.Clear();
        }

        void FlushCode()
        {
            var code = codeSb.ToString().TrimEnd();
            if (!string.IsNullOrEmpty(code))
                blocks.Add(new PlanSectionBlock { SubHeading = currentHeading, CodeBlock = code });
            codeSb.Clear();
            currentHeading = null;
        }

        foreach (var tok in tokens)
        {
            switch (tok.Kind)
            {
                case MarkdownTokenKind.FencedCodeStart:
                    FlushBlock();
                    break;
                case MarkdownTokenKind.FencedCodeLine:
                    codeSb.AppendLine(tok.RawLine);
                    break;
                case MarkdownTokenKind.FencedCodeEnd:
                    FlushCode();
                    break;
                case MarkdownTokenKind.Heading when tok.HeadingLevel >= 3:
                    FlushBlock();
                    currentHeading = StripMarkdown(tok.Content);
                    break;
                case MarkdownTokenKind.BulletItem:
                case MarkdownTokenKind.OrderedItem:
                    currentBullets.Add(StripMarkdown(tok.Content));
                    break;
                case MarkdownTokenKind.Blank:
                    if (paragraphSb.Length > 0 || currentBullets.Count > 0) FlushBlock();
                    break;
                default:
                    var rawLine = tok.RawLine.Trim();
                    if (!string.IsNullOrEmpty(rawLine))
                        paragraphSb.AppendLine(StripMarkdown(rawLine));
                    break;
            }
        }

        if (codeSb.Length > 0) FlushCode(); else FlushBlock();

        return new PlanSection
        {
            Title = heading,
            SectionType = type,
            RawContent = raw.Trim(),
            Blocks = blocks,
        };
    }

    private static void ParseArchitectureSection(
        string raw,
        List<PlanArchitectureDecision> decisions,
        List<PlanSection> sections,
        string sectionHeading)
    {
        var tokens = MarkdownTokenizer.Tokenize(raw);
        string? currentH3 = null;
        var itemLines = new List<string>();
        var narrativeLines = new List<string>();

        void FlushItem()
        {
            if (currentH3 is null) return;
            var dec = ParseDecision(currentH3, string.Join("\n", itemLines));
            if (dec is not null) decisions.Add(dec);
            itemLines.Clear();
            currentH3 = null;
        }

        foreach (var tok in tokens)
        {
            if (tok.Kind == MarkdownTokenKind.Heading && tok.HeadingLevel >= 3)
            {
                FlushItem();
                currentH3 = tok.Content;
                continue;
            }
            if (currentH3 is not null) itemLines.Add(tok.RawLine);
            else narrativeLines.Add(tok.RawLine);
        }
        FlushItem();

        var narrative = string.Join("\n", narrativeLines).Trim();
        if (!string.IsNullOrEmpty(narrative))
        {
            var sec = ParseFreeFormSection(sectionHeading, narrative, PlanSectionType.Architecture);
            if (sec is not null) sections.Add(sec);
        }
    }

    // Extracts a PlanArchitectureDecision from any H3 heading — ADR format optional.
    private static PlanArchitectureDecision? ParseDecision(string heading, string body)
    {
        if (string.IsNullOrWhiteSpace(heading)) return null;

        var adrMatch = AdrIdRe.Match(heading);
        var id    = adrMatch.Success ? adrMatch.Groups[1].Value.ToUpperInvariant() : string.Empty;
        var title = StripMarkdown(adrMatch.Success ? adrMatch.Groups[2].Value.Trim() : heading);

        if (string.IsNullOrEmpty(title)) return null;

        var context      = new StringBuilder();
        var decision     = new StringBuilder();
        var rationale    = new StringBuilder();
        var consequences = new List<string>();
        string currentProp = string.Empty;

        foreach (var tok in MarkdownTokenizer.Tokenize(body))
        {
            switch (tok.Kind)
            {
                case MarkdownTokenKind.Blank:
                    currentProp = string.Empty;
                    break;

                case MarkdownTokenKind.BulletItem:
                {
                    var c = StripMarkdown(tok.Content);
                    if      (currentProp == "consequences") consequences.Add(c);
                    else if (currentProp == "context")      context.AppendLine(c);
                    else if (currentProp == "decision")     decision.AppendLine(c);
                    else                                    decision.AppendLine(c);
                    break;
                }

                case MarkdownTokenKind.Heading:
                    break; // sub-headings within a decision body are skipped

                default:
                {
                    var bm = BoldPropertyRe.Match(tok.RawLine);
                    if (bm.Success)
                    {
                        var prop = bm.Groups[1].Value.ToLowerInvariant();
                        var val  = bm.Groups[2].Value.Trim();
                        switch (prop)
                        {
                            case "context":   context.AppendLine(StripMarkdown(val));   currentProp = "context";   break;
                            case "decision":  decision.AppendLine(StripMarkdown(val));  currentProp = "decision";  break;
                            case "rationale": rationale.AppendLine(StripMarkdown(val)); currentProp = "rationale"; break;
                            case "consequences": case "consequence": currentProp = "consequences"; break;
                            default:
                                if (currentProp == "context") context.AppendLine(StripMarkdown(tok.Content));
                                break;
                        }
                        break;
                    }
                    if (!IsPropertyLine(tok.RawLine.TrimStart()))
                    {
                        var s = StripMarkdown(tok.Content);
                        if      (currentProp == "context")   context.AppendLine(s);
                        else if (currentProp == "decision")  decision.AppendLine(s);
                        else if (currentProp == "rationale") rationale.AppendLine(s);
                        else                                 decision.AppendLine(s);
                    }
                    break;
                }
            }
        }

        var decisionText = decision.ToString().Trim();
        var contextText  = context.ToString().Trim();

        return new PlanArchitectureDecision
        {
            Id           = id,
            Title        = title,
            Context      = contextText,
            Decision     = string.IsNullOrEmpty(decisionText) ? title : decisionText,
            Rationale    = rationale.Length > 0 ? rationale.ToString().Trim() : null,
            Consequences = consequences,
            RawText      = body.Trim(),
        };
    }

    private static void ParseRisksSection(
        string raw,
        List<PlanRisk> risks,
        List<PlanConstraint> constraints)
    {
        var tokens = MarkdownTokenizer.Tokenize(raw);
        string? currentHeading = null;
        var itemLines = new List<string>();

        void Flush()
        {
            if (currentHeading is null) return;
            var body = string.Join("\n", itemLines);
            if (IsConstraintHeading(currentHeading))
                ParseConstraintItem(currentHeading, body, constraints);
            else
            {
                var risk = ParseRisk(currentHeading, body);
                if (risk is not null) risks.Add(risk);
            }
            itemLines.Clear();
            currentHeading = null;
        }

        foreach (var tok in tokens)
        {
            if (tok.Kind == MarkdownTokenKind.Heading && tok.HeadingLevel >= 3)
            { Flush(); currentHeading = tok.Content; continue; }

            if (tok.Kind == MarkdownTokenKind.BulletItem && currentHeading is null)
            {
                var content = tok.Content;
                if (IsConstraintLine(content)) ParseInlineConstraint(content, constraints);
                else { var r = ParseInlineRisk(content); if (r is not null) risks.Add(r); }
                continue;
            }

            if (currentHeading is not null) itemLines.Add(tok.RawLine);
        }
        Flush();

        if (risks.Count == 0) ParseRisksTable(raw, risks);
    }

    private static void ParseConstraintsSection(string raw, List<PlanConstraint> constraints)
    {
        var tokens = MarkdownTokenizer.Tokenize(raw);
        string? currentHeading = null;
        var itemLines = new List<string>();

        void Flush()
        {
            if (currentHeading is null) return;
            ParseConstraintItem(currentHeading, string.Join("\n", itemLines), constraints);
            itemLines.Clear(); currentHeading = null;
        }

        foreach (var tok in tokens)
        {
            if (tok.Kind == MarkdownTokenKind.Heading && tok.HeadingLevel >= 3)
            { Flush(); currentHeading = tok.Content; continue; }

            if (tok.Kind == MarkdownTokenKind.BulletItem && currentHeading is null)
            { ParseInlineConstraint(tok.Content, constraints); continue; }

            if (currentHeading is not null) itemLines.Add(tok.RawLine);
        }
        Flush();
    }

    private static void ParseConstraintItem(string heading, string body, List<PlanConstraint> constraints)
    {
        if (string.IsNullOrWhiteSpace(heading)) return;
        var title = StripMarkdown(Regex.Replace(heading,
            @"^(?:Constraint|Performance Goal|Scale|Scope|No Violation)[:\s]*", "", RegexOptions.IgnoreCase).Trim());
        if (string.IsNullOrEmpty(title)) title = StripMarkdown(heading);

        var descSb = new StringBuilder();
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            var bm = BulletRe.Match(line);
            if (bm.Success) { descSb.AppendLine(StripMarkdown(bm.Groups[1].Value.Trim())); continue; }
            if (!trimmed.StartsWith("#")) descSb.AppendLine(StripMarkdown(trimmed));
        }

        constraints.Add(new PlanConstraint
        {
            Title = title,
            Description = descSb.ToString().Trim(),
            ConstraintType = InferConstraintType(heading),
            RawText = body.Trim(),
        });
    }

    private static void ParseInlineConstraint(string content, List<PlanConstraint> constraints)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        constraints.Add(new PlanConstraint
        {
            Title = StripMarkdown(content),
            ConstraintType = InferConstraintType(content),
        });
    }

    private static ConstraintType InferConstraintType(string text)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("performance") || lower.Contains("latency") || lower.Contains("throughput")) return ConstraintType.PerformanceGoal;
        if (lower.Contains("scale") || lower.Contains("scope") || lower.Contains("volume")) return ConstraintType.ScaleScope;
        if (lower.Contains("no violation") || lower.Contains("must not") || lower.Contains("zero")) return ConstraintType.NoViolation;
        if (lower.Contains("complexity") || lower.Contains("justif")) return ConstraintType.ComplexityJustification;
        return ConstraintType.Constraint;
    }

    private static bool IsConstraintHeading(string heading)
    {
        var lower = heading.ToLowerInvariant();
        return lower.Contains("constraint") || lower.Contains("performance goal")
            || lower.Contains("no violation") || lower.Contains("scale/scope")
            || lower.Contains("complexity justif");
    }

    private static bool IsConstraintLine(string content)
    {
        var lower = content.ToLowerInvariant();
        return lower.Contains("constraint") || lower.Contains("performance goal")
            || lower.Contains("no violation");
    }

    private static PlanRisk? ParseRisk(string heading, string body)
    {
        if (string.IsNullOrWhiteSpace(heading)) return null;
        var title = StripMarkdown(heading);
        var severity = DetectRiskSeverity(heading, body);

        var descSb = new StringBuilder();
        string? mitigation = null;
        string? area = null;

        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            var bm = BoldPropertyRe.Match(line);
            if (bm.Success)
            {
                var prop = bm.Groups[1].Value.ToLowerInvariant();
                var val  = StripMarkdown(bm.Groups[2].Value.Trim());
                switch (prop)
                {
                    case "mitigation": case "mitigation strategy": case "mitigations":
                        mitigation = val; continue;
                    case "area": case "affected area":
                        area = val; continue;
                    case "severity": continue;
                }
            }

            var bullet = BulletRe.Match(line);
            if (bullet.Success)
            {
                var c = StripMarkdown(bullet.Groups[1].Value.Trim());
                if (trimmed.ToLowerInvariant().StartsWith("- mitigation")) mitigation = c;
                else descSb.AppendLine(c);
                continue;
            }

            if (!trimmed.StartsWith("#") && !IsPropertyLine(trimmed))
                descSb.AppendLine(StripMarkdown(trimmed));
        }

        title = Regex.Replace(title, @"^(?:Critical|High|Medium|Low)\s+Risk\s*[:\-–]?\s*", "", RegexOptions.IgnoreCase).Trim();
        title = Regex.Replace(title, @"^[🔴🟠🟡🟢⚠️]\s*", "").Trim();

        return new PlanRisk
        {
            Title = string.IsNullOrEmpty(title) ? "Unnamed Risk" : title,
            Description = descSb.ToString().Trim(),
            Severity = severity,
            Mitigation = mitigation,
            Area = area,
            RawText = body.Trim(),
        };
    }

    private static PlanRisk? ParseInlineRisk(string content)
    {
        var m = Regex.Match(content,
            @"^\*?\*?(Critical|High|Medium|Low)\*?\*?\s*[:\-–]?\s*(.+)$",
            RegexOptions.IgnoreCase);
        if (!m.Success) return null;

        var rest = m.Groups[2].Value.Trim();
        var colonIdx = rest.IndexOf(':');
        var titleText = colonIdx > 0 ? rest[..colonIdx].Trim() : rest;
        var desc = colonIdx > 0 ? rest[(colonIdx + 1)..].Trim() : string.Empty;

        return new PlanRisk
        {
            Title = StripMarkdown(titleText),
            Description = StripMarkdown(desc),
            Severity = ParseSeverityFromText(m.Groups[1].Value),
        };
    }

    private static void ParseRisksTable(string raw, List<PlanRisk> risks)
    {
        var lines = raw.Split('\n');
        List<string>? headers = null;

        foreach (var line in lines)
        {
            if (!TableRowRe.IsMatch(line)) continue;
            var cells = SplitCells(line);
            if (cells.Count == 0) continue;
            if (headers is null) { headers = cells; continue; }
            if (cells.All(c => Regex.IsMatch(c, @"^[-:\s]+$"))) continue;
            if (headers.Count < 2) continue;

            var titleIdx = 0;
            var sevIdx  = headers.FindIndex(h => h.Contains("sever", StringComparison.OrdinalIgnoreCase) || h.Contains("level", StringComparison.OrdinalIgnoreCase));
            var descIdx = headers.FindIndex(h => h.Contains("desc", StringComparison.OrdinalIgnoreCase));
            var mitIdx  = headers.FindIndex(h => h.Contains("mitig", StringComparison.OrdinalIgnoreCase));

            if (cells.Count <= titleIdx) continue;
            risks.Add(new PlanRisk
            {
                Title       = StripMarkdown(cells[titleIdx]),
                Description = descIdx >= 0 && descIdx < cells.Count ? StripMarkdown(cells[descIdx]) : string.Empty,
                Severity    = sevIdx >= 0 && sevIdx < cells.Count ? ParseSeverityFromText(cells[sevIdx]) : RiskSeverity.Medium,
                Mitigation  = mitIdx >= 0 && mitIdx < cells.Count ? StripMarkdown(cells[mitIdx]) : null,
            });
        }
    }

    private static void ParseComplexitySection(string raw, List<PlanComplexityItem> items)
    {
        if (TryParseComplexityTable(raw, items)) return;

        var tokens = MarkdownTokenizer.Tokenize(raw);
        string? currentH3 = null;
        var itemLines = new List<string>();

        void Flush()
        {
            if (currentH3 is null) return;
            var item = ParseComplexityItem(currentH3, string.Join("\n", itemLines));
            if (item is not null) items.Add(item);
            itemLines.Clear(); currentH3 = null;
        }

        foreach (var tok in tokens)
        {
            if (tok.Kind == MarkdownTokenKind.Heading && tok.HeadingLevel >= 3)
            { Flush(); currentH3 = tok.Content; continue; }

            if (tok.Kind == MarkdownTokenKind.BulletItem && currentH3 is null)
            { var item = ParseInlineComplexity(tok.Content); if (item is not null) items.Add(item); continue; }

            if (currentH3 is not null) itemLines.Add(tok.RawLine);
        }
        Flush();
    }

    private static bool TryParseComplexityTable(string raw, List<PlanComplexityItem> items)
    {
        var lines = raw.Split('\n').Where(l => TableRowRe.IsMatch(l)).ToList();
        if (lines.Count < 2) return false;

        var headers = SplitCells(lines[0]);
        var areaIdx  = headers.FindIndex(h => h.Contains("area", StringComparison.OrdinalIgnoreCase) || h.Contains("component", StringComparison.OrdinalIgnoreCase));
        var levelIdx = headers.FindIndex(h => h.Contains("complex", StringComparison.OrdinalIgnoreCase) || h.Contains("level", StringComparison.OrdinalIgnoreCase));
        if (areaIdx < 0 || levelIdx < 0) return false;

        var notesIdx = headers.FindIndex(h => h.Contains("note", StringComparison.OrdinalIgnoreCase) || h.Contains("reason", StringComparison.OrdinalIgnoreCase));

        foreach (var line in lines.Skip(1))
        {
            var cells = SplitCells(line);
            if (cells.All(c => Regex.IsMatch(c, @"^[-:\s]+$"))) continue;
            if (areaIdx >= cells.Count || levelIdx >= cells.Count) continue;
            var area = StripMarkdown(cells[areaIdx]);
            if (!string.IsNullOrWhiteSpace(area))
                items.Add(new PlanComplexityItem
                {
                    Area  = area,
                    Level = ParseComplexityLevel(cells[levelIdx]),
                    Notes = notesIdx >= 0 && notesIdx < cells.Count ? StripMarkdown(cells[notesIdx]) : null,
                });
        }
        return items.Count > 0;
    }

    private static PlanComplexityItem? ParseComplexityItem(string heading, string body)
    {
        if (string.IsNullOrWhiteSpace(heading)) return null;
        var area  = StripMarkdown(heading);
        var level = DetectComplexityLevel(heading, body);

        area = Regex.Replace(area, @"\s*[—\-–]\s*(?:Very High|High|Medium|Low)(?:\s+Complexity)?$", "", RegexOptions.IgnoreCase).Trim();
        area = Regex.Replace(area, @"\s*\((?:Very High|High|Medium|Low)(?:\s+Complexity)?\)$", "", RegexOptions.IgnoreCase).Trim();

        var notesSb  = new StringBuilder();
        var factors  = new List<string>();
        bool inFacts = false;

        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            if (trimmed.StartsWith("**Factor", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Factor", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("**Reason", StringComparison.OrdinalIgnoreCase))
            { inFacts = true; continue; }

            var bm = BulletRe.Match(line);
            if (bm.Success) { factors.Add(StripMarkdown(bm.Groups[1].Value.Trim())); continue; }

            var bprop = BoldPropertyRe.Match(line);
            if (bprop.Success && bprop.Groups[1].Value.ToLowerInvariant() is "notes" or "note" or "description")
            { notesSb.AppendLine(StripMarkdown(bprop.Groups[2].Value.Trim())); continue; }

            if (!trimmed.StartsWith("#") && !IsPropertyLine(trimmed))
            {
                if (inFacts) factors.Add(StripMarkdown(trimmed));
                else notesSb.AppendLine(StripMarkdown(trimmed));
            }
        }

        return new PlanComplexityItem
        {
            Area    = string.IsNullOrEmpty(area) ? "General" : area,
            Level   = level,
            Notes   = notesSb.Length > 0 ? notesSb.ToString().Trim() : null,
            Factors = factors,
            RawText = body.Trim(),
        };
    }

    private static PlanComplexityItem? ParseInlineComplexity(string content)
    {
        var m = Regex.Match(content,
            @"^(.+?)\s*[:\-–—]\s*(Very High|High|Medium|Low)(?:\s*[:\-–—]\s*(.+))?$",
            RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        return new PlanComplexityItem
        {
            Area  = StripMarkdown(m.Groups[1].Value.Trim()),
            Level = ParseComplexityLevel(m.Groups[2].Value.Trim()),
            Notes = m.Groups[3].Success ? StripMarkdown(m.Groups[3].Value.Trim()) : null,
        };
    }

    // Derives complexity items from constraints, dependencies, and content keywords
    // when no dedicated Complexity section is present.
    private static List<PlanComplexityItem> AutoGenerateComplexity(
        List<PlanConstraint> constraints,
        List<PlanDependency> deps,
        List<PlanSection> sections,
        List<PlanRisk> risks)
    {
        var items = new List<PlanComplexityItem>();

        // Performance goals → individual complexity items
        foreach (var pg in constraints.Where(c => c.ConstraintType == ConstraintType.PerformanceGoal))
        {
            items.Add(new PlanComplexityItem
            {
                Area    = pg.Title,
                Level   = InferComplexityLevelFromText(pg.Title + " " + pg.Description),
                Notes   = string.IsNullOrEmpty(pg.Description) ? null : pg.Description,
                Factors = ["Derived from performance goal"],
            });
        }

        // Scale/scope constraints → high complexity
        foreach (var si in constraints.Where(c => c.ConstraintType == ConstraintType.ScaleScope))
        {
            items.Add(new PlanComplexityItem
            {
                Area    = si.Title,
                Level   = InferComplexityLevelFromText(si.Title + " " + si.Description),
                Notes   = string.IsNullOrEmpty(si.Description) ? null : si.Description,
                Factors = ["Derived from scale/scope constraint"],
            });
        }

        // External dependencies → medium/high complexity (grouped)
        var externalDeps = deps.Where(d => d.IsExternal).ToList();
        if (externalDeps.Count > 0)
        {
            items.Add(new PlanComplexityItem
            {
                Area    = "External Integrations",
                Level   = externalDeps.Count > 4 ? ComplexityLevel.High : ComplexityLevel.Medium,
                Notes   = $"{externalDeps.Count} external integration{(externalDeps.Count != 1 ? "s" : "")}",
                Factors = externalDeps.Select(d => d.Name).ToList(),
            });
        }

        // High/critical risks signal implementation complexity
        var highRisks = risks.Where(r => r.Severity >= RiskSeverity.High).ToList();
        if (highRisks.Count > 0)
        {
            items.Add(new PlanComplexityItem
            {
                Area    = "Risk Surface",
                Level   = highRisks.Any(r => r.Severity == RiskSeverity.Critical)
                            ? ComplexityLevel.VeryHigh : ComplexityLevel.High,
                Notes   = $"{highRisks.Count} high or critical risk{(highRisks.Count != 1 ? "s" : "")} identified",
                Factors = highRisks.Select(r => r.Title).Take(5).ToList(),
            });
        }

        // Migration / schema change / event sourcing keywords in section content
        var migrationKeywords = new[] { "migrat", "schema change", "event sour", "replay", "backfill" };
        var allText = string.Join(" ", sections.Select(s => s.Title + " " + s.RawContent));
        if (migrationKeywords.Any(kw => allText.Contains(kw, StringComparison.OrdinalIgnoreCase)))
        {
            items.Add(new PlanComplexityItem
            {
                Area    = "Data Migration / Schema Evolution",
                Level   = ComplexityLevel.High,
                Notes   = "Plan involves migration or schema evolution work",
                Factors = ["Migration/schema complexity detected from plan content"],
            });
        }

        return items;
    }

    private static ComplexityLevel InferComplexityLevelFromText(string text)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("million") || lower.Contains("billion") || lower.Contains(" tb ") || lower.Contains("very high"))
            return ComplexityLevel.VeryHigh;
        if (lower.Contains("thousand") || lower.Contains("large scale") || lower.Contains("distributed") || lower.Contains("high") || lower.Contains("complex"))
            return ComplexityLevel.High;
        if (lower.Contains("medium") || lower.Contains("moderate"))
            return ComplexityLevel.Medium;
        return ComplexityLevel.Low;
    }

    private static void ParseDependenciesSection(string raw, List<PlanDependency> deps)
    {
        var tokens = MarkdownTokenizer.Tokenize(raw);
        bool extCtx = false;

        foreach (var tok in tokens)
        {
            if (tok.Kind == MarkdownTokenKind.Heading)
            { extCtx = tok.Content.ToLowerInvariant().Contains("external"); continue; }

            if (tok.Kind != MarkdownTokenKind.BulletItem) continue;
            var dep = ParseDependencyLine(tok.Content, extCtx || !raw.Contains("Internal", StringComparison.OrdinalIgnoreCase));
            if (dep is not null) deps.Add(dep);
        }
    }

    private static PlanDependency? ParseDependencyLine(string content, bool isExternal)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        string name = content, version = null!, description = null!;

        var colonIdx = content.IndexOf(':');
        var dashIdx  = content.IndexOfAny(['-', '–', '—'], 1);

        if (colonIdx > 0) { name = content[..colonIdx].Trim(); description = StripMarkdown(content[(colonIdx + 1)..].Trim()); }
        else if (dashIdx > 1) { name = content[..dashIdx].Trim(); description = StripMarkdown(content[(dashIdx + 1)..].Trim()); }

        var vm = VersionRe.Match(name);
        if (vm.Success) { version = vm.Groups[1].Value; name = name[..vm.Index].Trim(); }

        return new PlanDependency
        {
            Name = StripMarkdown(name),
            Version = version,
            Description = description,
            IsExternal = isExternal,
        };
    }

    private static void ParseMilestonesSection(string raw, List<PlanMilestone> milestones)
    {
        var tokens = MarkdownTokenizer.Tokenize(raw);
        string? currentH3 = null;
        var itemLines = new List<string>();

        void Flush()
        {
            if (currentH3 is null) return;
            var ms = ParseMilestone(currentH3, string.Join("\n", itemLines));
            if (ms is not null) milestones.Add(ms);
            itemLines.Clear(); currentH3 = null;
        }

        foreach (var tok in tokens)
        {
            if (tok.Kind == MarkdownTokenKind.Heading && tok.HeadingLevel >= 3)
            { Flush(); currentH3 = tok.Content; continue; }
            if (currentH3 is not null) itemLines.Add(tok.RawLine);
        }
        Flush();
    }

    private static PlanMilestone? ParseMilestone(string heading, string body)
    {
        if (string.IsNullOrWhiteSpace(heading)) return null;
        string? targetDate = null;
        var descSb = new StringBuilder();
        var deliverables = new List<string>();

        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            var bm = BoldPropertyRe.Match(line);
            if (bm.Success)
            {
                var prop = bm.Groups[1].Value.ToLowerInvariant();
                var val  = StripMarkdown(bm.Groups[2].Value.Trim());
                switch (prop)
                {
                    case "target": case "date": case "target date": case "due date":
                        targetDate = val; continue;
                    case "deliverables": continue;
                }
            }

            var bullet = BulletRe.Match(line);
            if (bullet.Success) { deliverables.Add(StripMarkdown(bullet.Groups[1].Value.Trim())); continue; }

            if (!trimmed.StartsWith("#") && !IsPropertyLine(trimmed))
                descSb.AppendLine(StripMarkdown(trimmed));
        }

        return new PlanMilestone
        {
            Title = StripMarkdown(heading),
            TargetDate = targetDate,
            Description = descSb.Length > 0 ? descSb.ToString().Trim() : null,
            Deliverables = deliverables,
        };
    }

    // ── Constitution Check ───────────────────────────────────────────────────

    private static void ParseConstitutionCheckSection(
        string raw,
        List<PlanConstitutionCheckItem> checkItems,
        List<PlanGate> gates)
    {
        ParseGatesTable(raw, gates);

        var tokens = MarkdownTokenizer.Tokenize(raw);
        string? currentH3 = null;
        var itemLines = new List<string>();

        void Flush()
        {
            if (currentH3 is null) return;
            var item = ParseConstitutionCheckItem(currentH3, string.Join("\n", itemLines));
            if (item is not null) checkItems.Add(item);
            itemLines.Clear();
            currentH3 = null;
        }

        bool inTable = false;
        foreach (var tok in tokens)
        {
            if (tok.Kind is MarkdownTokenKind.TableRow or MarkdownTokenKind.TableSeparator)
            { inTable = true; continue; }
            if (inTable && tok.Kind == MarkdownTokenKind.Blank)
            { inTable = false; continue; }
            if (inTable) continue;

            if (tok.Kind == MarkdownTokenKind.Heading && tok.HeadingLevel >= 3)
            { Flush(); currentH3 = tok.Content; continue; }
            if (currentH3 is not null) itemLines.Add(tok.RawLine);
        }
        Flush();
    }

    private static void ParseGatesTable(string raw, List<PlanGate> gates)
    {
        var lines = raw.Split('\n');
        List<string>? headers = null;

        // Column indices
        int gateIdx = -1, ruleIdx = -1, statusIdx = -1, evidenceIdx = -1, notesIdx = -1;

        foreach (var line in lines)
        {
            if (!TableRowRe.IsMatch(line)) continue;
            var cells = SplitCells(line);
            if (cells.Count == 0) continue;

            if (headers is null)
            {
                headers = cells;
                DetectGateTableColumns(cells, out gateIdx, out ruleIdx, out statusIdx, out evidenceIdx, out notesIdx);
                continue;
            }

            if (TableSepRe.IsMatch(line)) continue;
            if (cells.Count <= Math.Max(gateIdx, 0)) continue;

            // Gate label: the "requirement" / "gate" / "check" column
            var gateText = gateIdx >= 0 && gateIdx < cells.Count
                ? StripMarkdown(cells[gateIdx])
                : string.Empty;

            // Rule ID: the "principle" / "rule" / "id" column
            var ruleText = ruleIdx >= 0 && ruleIdx < cells.Count
                ? cells[ruleIdx]
                : (gateIdx >= 0 && gateIdx < cells.Count ? cells[gateIdx] : string.Empty);

            if (string.IsNullOrWhiteSpace(gateText) && string.IsNullOrWhiteSpace(ruleText)) continue;

            // Extract rule ID from either column
            var ruleId = ExtractRuleId(ruleText) ?? ExtractRuleId(gateText) ?? string.Empty;

            // If gate text is the same as the rule ID, use rule text as label (or vice versa)
            if (string.IsNullOrEmpty(gateText)) gateText = ruleText;

            var statusText = statusIdx >= 0 && statusIdx < cells.Count ? cells[statusIdx] : string.Empty;
            var evidence   = evidenceIdx >= 0 && evidenceIdx < cells.Count ? StripMarkdown(cells[evidenceIdx]) : null;
            var notes      = notesIdx >= 0 && notesIdx < cells.Count ? StripMarkdown(cells[notesIdx]) : null;

            gates.Add(new PlanGate
            {
                Gate      = gateText,
                RuleId    = ruleId,
                Principle = StripMarkdown(ruleText),
                Status    = ParseGateStatus(statusText),
                Evidence  = string.IsNullOrWhiteSpace(evidence) ? null : evidence,
                Notes     = string.IsNullOrWhiteSpace(notes) ? null : notes,
            });
        }
    }

    // Detect which columns contain the gate, rule ID, status, evidence, and notes.
    // Handles both "Gate | Principle | Status" and "Principle | Requirement | Status" layouts.
    private static void DetectGateTableColumns(
        List<string> headers,
        out int gateIdx, out int ruleIdx, out int statusIdx,
        out int evidenceIdx, out int notesIdx)
    {
        gateIdx = ruleIdx = statusIdx = evidenceIdx = notesIdx = -1;

        for (int c = 0; c < headers.Count; c++)
        {
            var h = headers[c].ToLowerInvariant().Trim();

            if (h.Contains("requirement") || h == "gate" || h == "check" || h.Contains("check item"))
                gateIdx = c;
            else if (h == "principle" || h == "rule" || h == "id" || h == "ref"
                     || h.Contains("rule id") || h.Contains("principle id") || h.Contains("rule ref"))
                ruleIdx = c;
            else if (h.Contains("status") || h == "result" || h == "pass" || h == "outcome")
                statusIdx = c;
            else if (h.Contains("evidence") || h.Contains("impl") || h.Contains("justif"))
                evidenceIdx = c;
            else if (h.Contains("note") || h.Contains("comment") || h.Contains("remark"))
                notesIdx = c;
        }

        // If gate column not found but rule column IS found, remaining columns become candidates
        if (gateIdx < 0 && ruleIdx >= 0)
        {
            // Find the first column that's NOT rule, status, evidence, or notes
            for (int c = 0; c < headers.Count; c++)
            {
                if (c != ruleIdx && c != statusIdx && c != evidenceIdx && c != notesIdx)
                { gateIdx = c; break; }
            }
        }

        // Final fallbacks
        if (gateIdx < 0 && ruleIdx < 0) { gateIdx = 0; ruleIdx = 0; }
        else if (gateIdx < 0) gateIdx = ruleIdx;  // use rule column as gate label
        else if (ruleIdx < 0) ruleIdx = gateIdx;   // use gate column for rule ID extraction

        if (statusIdx < 0)
        {
            // Status is usually the last short column or column after gate/rule
            for (int c = headers.Count - 1; c >= 0; c--)
            {
                if (c != gateIdx && c != ruleIdx && c != evidenceIdx && c != notesIdx)
                { statusIdx = c; break; }
            }
        }
    }

    private static PlanConstitutionCheckItem? ParseConstitutionCheckItem(string heading, string body)
    {
        if (string.IsNullOrWhiteSpace(heading)) return null;

        var ruleId = ExtractRuleId(heading) ?? string.Empty;
        var title  = StripMarkdown(ruleId.Length > 0
            ? heading.Replace(ruleId, "").Trim().TrimStart(':', '-', '–').Trim()
            : heading);

        var status = DetectCheckStatus(heading, body);

        var notesSb = new StringBuilder();
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;
            var bm = BoldPropertyRe.Match(line);
            if (bm.Success && bm.Groups[1].Value.ToLowerInvariant() == "status") continue;
            var bullet = BulletRe.Match(line);
            if (bullet.Success) { notesSb.AppendLine(StripMarkdown(bullet.Groups[1].Value.Trim())); continue; }
            if (!trimmed.StartsWith("#") && !IsPropertyLine(trimmed))
                notesSb.AppendLine(StripMarkdown(trimmed));
        }

        return new PlanConstitutionCheckItem
        {
            RuleId  = ruleId,
            Title   = string.IsNullOrEmpty(title) ? ruleId : title,
            Status  = status,
            Notes   = notesSb.Length > 0 ? notesSb.ToString().Trim() : null,
            RawText = body.Trim(),
        };
    }

    // ── Implementation Phases ────────────────────────────────────────────────

    private static void ParseImplementationPhasesSection(string raw, List<PlanImplementationPhase> phases)
    {
        var tokens = MarkdownTokenizer.Tokenize(raw);
        string? currentH3 = null;
        var itemLines = new List<string>();

        void Flush()
        {
            if (currentH3 is null) return;
            var phase = ParsePhase(currentH3, string.Join("\n", itemLines));
            if (phase is not null) phases.Add(phase);
            itemLines.Clear(); currentH3 = null;
        }

        foreach (var tok in tokens)
        {
            // Only H3 headings start new phases; H4+ are sub-sections within a phase body
            if (tok.Kind == MarkdownTokenKind.Heading && tok.HeadingLevel == 3)
            { Flush(); currentH3 = tok.Content; continue; }
            if (currentH3 is not null) itemLines.Add(tok.RawLine);
        }
        Flush();
        phases.Sort((a, b) => a.PhaseNumber.CompareTo(b.PhaseNumber));
    }

    private static PlanImplementationPhase? ParsePhase(string heading, string body)
    {
        if (string.IsNullOrWhiteSpace(heading)) return null;

        int phaseNum = 0;
        var title = StripMarkdown(heading);

        var numMatch = PhaseNumberRe.Match(heading);
        if (numMatch.Success)
        {
            phaseNum = int.TryParse(numMatch.Groups[1].Value, out var n) ? n : 0;
            title = StripMarkdown(heading[(numMatch.Index + numMatch.Length)..].TrimStart(':', '-', '–', ' ').Trim());
            if (string.IsNullOrEmpty(title)) title = $"Phase {phaseNum}";
        }
        else if (heading.Contains("post", StringComparison.OrdinalIgnoreCase) ||
                 heading.Contains("after", StringComparison.OrdinalIgnoreCase))
        { phaseNum = 99; }
        else if (heading.Contains("pre-", StringComparison.OrdinalIgnoreCase) ||
                 heading.Contains("prerequisite", StringComparison.OrdinalIgnoreCase))
        { phaseNum = 0; }

        var blocks  = new List<PlanSectionBlock>();
        var tasks   = new List<string>();
        var checks  = new List<string>();
        var descSb  = new StringBuilder();
        bool inChecks = false;

        string? blockHeading = null;
        var blockBullets = new List<string>();

        void FlushPhaseBlock()
        {
            var para = descSb.ToString().Trim();
            if (!string.IsNullOrEmpty(para) || blockBullets.Count > 0)
            {
                blocks.Add(new PlanSectionBlock
                {
                    SubHeading = blockHeading,
                    Paragraph = string.IsNullOrEmpty(para) ? null : para,
                    BulletPoints = [.. blockBullets],
                });
            }
            blockHeading = null;
            blockBullets = [];
            descSb.Clear();
        }

        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            var hm = HeadingRe.Match(line);
            if (hm.Success && hm.Groups[1].Value.Length >= 4)
            {
                FlushPhaseBlock();
                blockHeading = StripMarkdown(hm.Groups[2].Value.Trim());
                var hl = blockHeading.ToLowerInvariant();
                inChecks = hl.Contains("check") || hl.Contains("gate") || hl.Contains("validat");
                continue;
            }

            var bm = BulletRe.Match(line);
            if (bm.Success)
            {
                var content = StripMarkdown(bm.Groups[1].Value.Trim());
                if (inChecks) checks.Add(content);
                else { tasks.Add(content); blockBullets.Add(content); }
                continue;
            }

            if (!trimmed.StartsWith("#") && !IsPropertyLine(trimmed))
                descSb.AppendLine(StripMarkdown(trimmed));
        }
        FlushPhaseBlock();

        return new PlanImplementationPhase
        {
            PhaseNumber = phaseNum,
            Title       = string.IsNullOrEmpty(title) ? heading : title,
            Description = descSb.Length > 0 ? descSb.ToString().Trim() : null,
            Tasks  = tasks,
            Checks = checks,
            Blocks = blocks,
        };
    }

    // ── Testing ──────────────────────────────────────────────────────────────

    private static PlanTestingInfo ParseTestingSection(string raw)
    {
        var frameworks  = new List<string>();
        var testFolders = new List<string>();
        var testClasses = new List<string>();
        var gateRefs    = new List<string>();
        var blocks      = new List<PlanSectionBlock>();

        var tokens    = MarkdownTokenizer.Tokenize(raw);
        string? currentH3 = null;
        var bulletAcc = new List<string>();
        var codeAcc   = new StringBuilder();

        void FlushBlock(bool flush)
        {
            if (codeAcc.Length > 0)
            {
                ExtractTestPaths(codeAcc.ToString(), testFolders, testClasses);
                blocks.Add(new PlanSectionBlock { SubHeading = currentH3, CodeBlock = codeAcc.ToString().TrimEnd() });
                codeAcc.Clear();
            }
            else if (bulletAcc.Count > 0)
            {
                blocks.Add(new PlanSectionBlock { SubHeading = currentH3, BulletPoints = [.. bulletAcc] });
                bulletAcc.Clear();
            }
            if (flush) currentH3 = null;
        }

        foreach (var tok in tokens)
        {
            switch (tok.Kind)
            {
                case MarkdownTokenKind.FencedCodeStart:
                    FlushBlock(false);
                    break;
                case MarkdownTokenKind.FencedCodeLine:
                    codeAcc.AppendLine(tok.RawLine);
                    break;
                case MarkdownTokenKind.FencedCodeEnd:
                    FlushBlock(false);
                    break;
                case MarkdownTokenKind.Heading when tok.HeadingLevel >= 3:
                    FlushBlock(true);
                    currentH3 = StripMarkdown(tok.Content);
                    break;
                case MarkdownTokenKind.BulletItem:
                {
                    var content = tok.Content;
                    foreach (var fw in TestingFrameworks)
                        if (content.Contains(fw, StringComparison.OrdinalIgnoreCase) && !frameworks.Contains(fw, StringComparer.OrdinalIgnoreCase))
                            frameworks.Add(fw);
                    foreach (Match m in RuleIdRe.Matches(content))
                    {
                        var rid = m.Groups[1].Value.ToUpperInvariant();
                        if (!gateRefs.Contains(rid)) gateRefs.Add(rid);
                    }
                    if (content.Contains("test", StringComparison.OrdinalIgnoreCase))
                        ExtractTestPaths(content, testFolders, testClasses);
                    bulletAcc.Add(StripMarkdown(content));
                    break;
                }
                default:
                    foreach (var fw in TestingFrameworks)
                        if (tok.RawLine.Contains(fw, StringComparison.OrdinalIgnoreCase) && !frameworks.Contains(fw, StringComparer.OrdinalIgnoreCase))
                            frameworks.Add(fw);
                    break;
            }
        }
        FlushBlock(true);

        return new PlanTestingInfo
        {
            Frameworks  = frameworks,
            TestFolders = testFolders.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            TestClasses = testClasses.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            GateRefs    = gateRefs,
            Blocks      = blocks,
        };
    }

    private static void ExtractTestPaths(string text, List<string> folders, List<string> classes)
    {
        foreach (Match m in Regex.Matches(text, @"(?:tests?|specs?|__tests__)[\\/][\w\\/.-]+", RegexOptions.IgnoreCase))
        {
            var path = m.Value.Replace('\\', '/');
            if (!folders.Contains(path)) folders.Add(path);
        }
        foreach (Match m in Regex.Matches(text, @"\b\w*[Tt]est\w*(?:Tests|Specs)\b"))
        {
            var cls = m.Value;
            if (!classes.Contains(cls)) classes.Add(cls);
        }
    }

    // ── Health builder ───────────────────────────────────────────────────────

    private static PlanHealth BuildHealth(
        string? summary,
        List<PlanRisk> risks,
        List<PlanConstraint> constraints,
        List<PlanArchitectureDecision> decisions,
        List<PlanComplexityItem> complexity,
        List<PlanDependency> deps,
        List<PlanMilestone> milestones,
        List<PlanConstitutionCheckItem> checkItems,
        List<PlanGate> gates,
        List<PlanImplementationPhase> phases,
        PlanTestingInfo? testing,
        List<PlanSection> sections,
        bool hasMetadata)
    {
        var critical  = risks.Count(r => r.Severity == RiskSeverity.Critical);
        var high      = risks.Count(r => r.Severity == RiskSeverity.High);
        var highC     = complexity.Count(c => c.Level is ComplexityLevel.High or ComplexityLevel.VeryHigh);
        var external  = deps.Count(d => d.IsExternal);
        var compliant = checkItems.Count(c => c.Status == ConstitutionCheckStatus.Compliant);
        var nonComp   = checkItems.Count(c => c.Status == ConstitutionCheckStatus.NonCompliant);
        var needsRev  = checkItems.Count(c => c.Status == ConstitutionCheckStatus.NeedsReview);

        var passedG  = gates.Count(g => g.Status == PlanGateStatus.Pass);
        var warnG    = gates.Count(g => g.Status == PlanGateStatus.Warning);
        var failG    = gates.Count(g => g.Status == PlanGateStatus.Fail);

        var hasTechCtx   = sections.Any(s => s.SectionType == PlanSectionType.TechnicalContext);
        var hasStructure = sections.Any(s => s.SectionType == PlanSectionType.ProjectStructure);
        var hasArch      = decisions.Count > 0 || sections.Any(s => s.SectionType == PlanSectionType.Architecture);

        var perfGoals = constraints.Count(c => c.ConstraintType == ConstraintType.PerformanceGoal);
        var testRefs  = (testing?.GateRefs.Count ?? 0) + (testing?.TestFolders.Count ?? 0);

        var indicators = new List<PlanHealthIndicator>();

        if (risks.Count > 0)
            indicators.Add(new PlanHealthIndicator
            {
                Icon    = critical > 0 || high > 0 ? "⚠" : "✓",
                Message = $"{risks.Count} risks — {critical} critical, {high} high",
                Level   = critical > 0 ? PlanHealthLevel.Error : high > 0 ? PlanHealthLevel.Warning : PlanHealthLevel.Good,
            });

        if (gates.Count > 0)
        {
            indicators.Add(new PlanHealthIndicator
            {
                Icon    = failG > 0 ? "✗" : warnG > 0 ? "⚠" : "✓",
                Message = $"{gates.Count} constitution gates — {passedG} pass, {warnG} warning, {failG} fail",
                Level   = failG > 0 ? PlanHealthLevel.Error : warnG > 0 ? PlanHealthLevel.Warning : PlanHealthLevel.Good,
            });
        }

        if (decisions.Count > 0)
            indicators.Add(new PlanHealthIndicator
            {
                Icon = "✓",
                Message = $"{decisions.Count} architecture decision{(decisions.Count != 1 ? "s" : "")} documented",
                Level = PlanHealthLevel.Good,
            });

        if (phases.Count > 0)
            indicators.Add(new PlanHealthIndicator
            {
                Icon = "✓",
                Message = $"{phases.Count} implementation phase{(phases.Count != 1 ? "s" : "")} planned",
                Level = PlanHealthLevel.Good,
            });

        if (highC > 0)
            indicators.Add(new PlanHealthIndicator
            {
                Icon = "⚠", Message = $"{highC} high-complexity area{(highC != 1 ? "s" : "")} identified",
                Level = PlanHealthLevel.Warning,
            });

        if (nonComp > 0)
            indicators.Add(new PlanHealthIndicator
            {
                Icon = "✗", Message = $"{nonComp} constitution rule{(nonComp != 1 ? "s" : "")} non-compliant",
                Level = PlanHealthLevel.Error,
            });
        else if (needsRev > 0)
            indicators.Add(new PlanHealthIndicator
            {
                Icon = "⚠", Message = $"{needsRev} constitution rule{(needsRev != 1 ? "s" : "")} need review",
                Level = PlanHealthLevel.Warning,
            });
        else if (checkItems.Count > 0)
            indicators.Add(new PlanHealthIndicator
            {
                Icon = "✓", Message = $"All {compliant} checked constitution rules are compliant",
                Level = PlanHealthLevel.Good,
            });

        if (!hasTechCtx)
            indicators.Add(new PlanHealthIndicator
            {
                Icon = "⚠", Message = "No Technical Context section found",
                Level = PlanHealthLevel.Warning,
            });

        var parts = new List<string>();
        if (risks.Count > 0)      parts.Add($"{risks.Count} risks");
        if (decisions.Count > 0)  parts.Add($"{decisions.Count} decisions");
        if (phases.Count > 0)     parts.Add($"{phases.Count} phases");
        if (gates.Count > 0)      parts.Add($"{gates.Count} gates");
        if (complexity.Count > 0) parts.Add($"{complexity.Count} complexity items");

        return new PlanHealth
        {
            TotalRisks = risks.Count,
            CriticalRisks = critical,
            HighRisks = high,
            MediumRisks = risks.Count(r => r.Severity == RiskSeverity.Medium),
            LowRisks = risks.Count(r => r.Severity == RiskSeverity.Low),
            TotalArchitectureDecisions = decisions.Count,
            TotalComplexityItems = complexity.Count,
            HighComplexityItems = highC,
            TotalDependencies = deps.Count,
            ExternalDependencies = external,
            TotalMilestones = milestones.Count,
            TotalConstitutionCheckItems = checkItems.Count,
            CompliantItems = compliant,
            NonCompliantItems = nonComp,
            NeedsReviewItems = needsRev,
            TotalConstitutionGates = gates.Count,
            PassedGates = passedG,
            WarningGates = warnG,
            FailedGates = failG,
            TotalPhases = phases.Count,
            TotalConstraints = constraints.Count,
            TotalPerformanceGoals = perfGoals,
            TotalTestReferences = testRefs,
            HasMetadata = hasMetadata || !string.IsNullOrEmpty(summary),
            HasSummary = !string.IsNullOrEmpty(summary),
            HasTechnicalContext = hasTechCtx,
            HasConstitutionCheck = checkItems.Count > 0 || gates.Count > 0,
            HasProjectStructure = hasStructure,
            HasImplementationPhases = phases.Count > 0,
            HasTestingInfo = testing is not null,
            HasArchitecture = hasArch,
            HealthSummary = parts.Count > 0 ? string.Join(", ", parts) + "." : "No structured content detected.",
            Indicators = indicators,
        };
    }

    // ── Classifiers ──────────────────────────────────────────────────────────

    private static PlanSectionType ClassifySection(string heading)
    {
        var lower = heading.ToLowerInvariant();
        // Check TechContext before Architecture (both may contain "context")
        if (TechContextKeywords.Any(k => lower.Contains(k.ToLowerInvariant())))     return PlanSectionType.TechnicalContext;
        // ConstitutionCheck before Architecture (avoid "gate" matching "design")
        if (ConstitutionCheckKeywords.Any(k => lower.Contains(k.ToLowerInvariant()))) return PlanSectionType.ConstitutionCheck;
        if (ArchitectureKeywords.Any(k => lower.Contains(k.ToLowerInvariant())))    return PlanSectionType.Architecture;
        if (ProjectStructureKeywords.Any(k => lower.Contains(k.ToLowerInvariant()))) return PlanSectionType.ProjectStructure;
        if (ImplementationKeywords.Any(k => lower.Contains(k.ToLowerInvariant())))  return PlanSectionType.ImplementationPhases;
        if (TestingKeywords.Any(k => lower.Contains(k.ToLowerInvariant())))         return PlanSectionType.Testing;
        if (RiskKeywords.Any(k => lower.Contains(k.ToLowerInvariant())))            return PlanSectionType.Risks;
        if (ConstraintKeywords.Any(k => lower.Contains(k.ToLowerInvariant())))      return PlanSectionType.Constraints;
        if (ComplexityKeywords.Any(k => lower.Contains(k.ToLowerInvariant())))      return PlanSectionType.Complexity;
        if (DependencyKeywords.Any(k => lower.Contains(k.ToLowerInvariant())))      return PlanSectionType.Dependencies;
        if (MilestoneKeywords.Any(k => lower.Contains(k.ToLowerInvariant())))       return PlanSectionType.Milestones;
        return PlanSectionType.Other;
    }

    // ── Status / severity detectors ─────────────────────────────────────────

    private static PlanGateStatus ParseGateStatus(string text)
    {
        var t = text.ToUpperInvariant().Trim();
        if (t.Contains("PASS") || t.Contains("✅") || t.Contains("✓") || t.Contains("YES") || t == "OK")
            return PlanGateStatus.Pass;
        if (t.Contains("WARN") || t.Contains("⚠") || t.Contains("PARTIAL"))
            return PlanGateStatus.Warning;
        if (t.Contains("FAIL") || t.Contains("❌") || t.Contains("✗") || t.Contains("NO") || t.Contains("BLOCKED"))
            return PlanGateStatus.Fail;
        if (t.Contains("N/A") || t.Contains("NOT APPLICABLE") || string.IsNullOrWhiteSpace(t)
            || t == "–" || t == "-")
            return PlanGateStatus.NotApplicable;
        return PlanGateStatus.NotApplicable;
    }

    private static RiskSeverity DetectRiskSeverity(string heading, string body)
    {
        var h = heading.ToLowerInvariant();
        if (h.Contains("critical") || h.Contains("🔴")) return RiskSeverity.Critical;
        if (h.Contains("high") || h.Contains("🟠"))     return RiskSeverity.High;
        if (h.Contains("low") || h.Contains("🟢"))      return RiskSeverity.Low;
        if (h.Contains("medium") || h.Contains("moderate") || h.Contains("🟡")) return RiskSeverity.Medium;

        var m = Regex.Match(body, @"\*\*[Ss]everity\*\*\s*:?\s*([^\n*]+)", RegexOptions.IgnoreCase);
        if (m.Success) return ParseSeverityFromText(m.Groups[1].Value.Trim());
        return RiskSeverity.Medium;
    }

    private static ConstitutionCheckStatus DetectCheckStatus(string heading, string body)
    {
        var m = Regex.Match(body, @"\*\*[Ss]tatus\*\*\s*:?\s*([^\n*]+)", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var s = m.Groups[1].Value.ToLowerInvariant().Trim();
            if (s.Contains("non-compli") || s.Contains("fail"))    return ConstitutionCheckStatus.NonCompliant;
            if (s.Contains("needs") || s.Contains("review"))       return ConstitutionCheckStatus.NeedsReview;
            if (s.Contains("compli") || s.Contains("pass"))        return ConstitutionCheckStatus.Compliant;
            if (s.Contains("n/a") || s.Contains("not applicable")) return ConstitutionCheckStatus.NotApplicable;
        }

        if (heading.Contains("✓") || heading.Contains("✅")) return ConstitutionCheckStatus.Compliant;
        if (heading.Contains("✗") || heading.Contains("❌")) return ConstitutionCheckStatus.NonCompliant;
        if (heading.Contains("⚠"))                           return ConstitutionCheckStatus.NeedsReview;
        if (heading.Contains("N/A", StringComparison.OrdinalIgnoreCase)) return ConstitutionCheckStatus.NotApplicable;

        return ConstitutionCheckStatus.NeedsReview;
    }

    private static ComplexityLevel DetectComplexityLevel(string heading, string body)
    {
        var h = heading.ToLowerInvariant();
        if (h.Contains("very high") || h.Contains("extreme")) return ComplexityLevel.VeryHigh;
        if (h.Contains("high"))   return ComplexityLevel.High;
        if (h.Contains("low"))    return ComplexityLevel.Low;
        if (h.Contains("medium") || h.Contains("moderate")) return ComplexityLevel.Medium;

        var m = Regex.Match(body, @"\*\*[Cc]omplexity\*\*\s*:?\s*([^\n*]+)", RegexOptions.IgnoreCase);
        if (m.Success) return ParseComplexityLevel(m.Groups[1].Value.Trim());
        return ComplexityLevel.Medium;
    }

    private static RiskSeverity ParseSeverityFromText(string text)
    {
        var t = text.ToLowerInvariant();
        if (t.Contains("crit")) return RiskSeverity.Critical;
        if (t.Contains("high")) return RiskSeverity.High;
        if (t.Contains("low"))  return RiskSeverity.Low;
        return RiskSeverity.Medium;
    }

    private static ComplexityLevel ParseComplexityLevel(string text)
    {
        var t = text.ToLowerInvariant();
        if (t.Contains("very high") || t.Contains("extreme")) return ComplexityLevel.VeryHigh;
        if (t.Contains("high"))   return ComplexityLevel.High;
        if (t.Contains("low"))    return ComplexityLevel.Low;
        return ComplexityLevel.Medium;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string? ExtractRuleId(string text)
    {
        var m = RuleIdRe.Match(text);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    private static List<string> SplitCells(string line)
    {
        var inner = line.Trim();
        if (inner.StartsWith('|')) inner = inner[1..];
        if (inner.EndsWith('|'))   inner = inner[..^1];
        return inner.Split('|').Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
    }

    private static bool IsPropertyLine(string line) =>
        Regex.IsMatch(line.TrimStart(), @"^\*\*[^*]+\*\*\s*:");

    private static string StripMarkdown(string s) =>
        Regex.Replace(s, @"[*_`#\[\]]", "").Trim();
}
