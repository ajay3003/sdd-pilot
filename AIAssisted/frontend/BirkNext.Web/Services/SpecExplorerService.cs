using System.Text;
using System.Text.RegularExpressions;
using BirkNext.Web.GraphQL;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public static class SpecExplorerService
{
    // ── Regexes ───────────────────────────────────────────────────────────────

    private static readonly Regex HeadingRe = new(
        @"^(#{1,6})\s+(.+)$", RegexOptions.Compiled);

    // Spec item anchored to line start — prevents mid-sentence reference matches.
    // Matches: "**FR-001**:", "FR-001:", "- FR-001:", "- **SC-002**:"
    private static readonly Regex SpecItemStartRe = new(
        @"^(?:[-*]\s+|>\s+)?\*{0,2}(FR|NFR|SC|US|UC|AC|TS|REQ)-?\s*(\d{1,4})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Spec references anywhere in text — used only for extracting linked IDs.
    private static readonly Regex SpecRefRe = new(
        @"\b(FR|NFR|SC|US|UC|AC|TS|REQ|TC)-?\s*(\d{1,4})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UserStoryInlineRe = new(
        @"^[-*]\s+(?:User Story|US|Story)\s*[:\-–]\s*(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UserStoryHeadingRe = new(
        @"^User\s+Stor(?:y|ies)\s*(?:#?\d+|[:\-–]|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ClarificationInlineRe = new(
        @"^[-*]\s+(?:Clarification|OPEN|TBD|Question)\s*[:\-–]?\s*(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Inline Q/A on a single line: "- Q: question text → A: answer text"
    // Supports → (U+2192), ->, –> as the Q-to-A separator.
    private static readonly Regex InlineQaRe = new(
        @"^[-*]\s+\*{0,2}Q[:\.\)]\*{0,2}\s+(.+?)\s*(?:→|->|–>)\s*\*{0,2}A[:\.\)]\*{0,2}\s+(.+)$",
        RegexOptions.Compiled);

    // Multi-line Q/A — Q on its own line: "Q: text", "**Q:** text", "1. Q: text"
    private static readonly Regex QaQuestionRe = new(
        @"^\s*(?:\d+[\.\)]\s*)?\*{0,2}Q[:\.\)]\*{0,2}\s+(.+)$",
        RegexOptions.Compiled);

    // Multi-line Q/A — A on its own line: "A: text", "**A:** text"
    private static readonly Regex QaAnswerRe = new(
        @"^\s*\*{0,2}A[:\.\)]\*{0,2}\s+(.+)$",
        RegexOptions.Compiled);

    // Traditional BDD scenario header: "**Scenario 1:** title", "Scenario: title"
    private static readonly Regex BddScenarioRe = new(
        @"^\s*(?:[-*]\s*)?\*{0,2}Scenario\s*(?:#?\d+)?[:\*\s–-]*\*{0,2}\s*(.*?)[\*]*\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Numbered inline BDD item: "1. **Given** ..." or "1. Given ..."
    private static readonly Regex NumberedBddStartRe = new(
        @"^\d+\.\s+\*{0,2}Given\*{0,2}\s+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // BDD step keywords on their own line
    private static readonly Regex BddKeywordRe = new(
        @"^\s*(?:[-*]\s*)?\*{0,2}(Given|When|Then|And|But)\*{0,2}\s+(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Entity definition in Key Entities section: "- **EntityName**: description"
    private static readonly Regex EntityDefRe = new(
        @"^[-*]\s+\*{1,2}([^\*\n]+?)\*{1,2}\s*:\s*(.*)?$",
        RegexOptions.Compiled);

    // Frontmatter metadata: "**Feature Branch**: value", "**Source**: value"
    private static readonly Regex MetadataRe = new(
        @"^\*{1,2}(Feature Branch|Feature|Created|Status|Source|Input|Document|Imported from|Branch)\*{1,2}\s*:\s*(.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // FR identifier — used to deduplicate sub-bullet fragments in BuildFromCandidates
    private static readonly Regex FrIdRe = new(@"\bFR-\d{3,4}\b", RegexOptions.Compiled);

    // ISO-date heading: "2026-03-06" — marks a Q/A decision session, not a user-story lane
    private static readonly Regex DateHeadingRe = new(@"^\d{4}-\d{2}-\d{2}\b", RegexOptions.Compiled);

    // Bullet item start
    private static readonly Regex BulletStartRe = new(
        @"^[-*]\s+(.+)$", RegexOptions.Compiled);

    // Continuation line (2+ leading spaces)
    private static readonly Regex ContinuationRe = new(
        @"^\s{2,}", RegexOptions.Compiled);

    private static readonly Regex TableRowRe = new(
        @"^\|(.+)\|$", RegexOptions.Compiled);

    private static readonly Regex TableSepRe = new(
        @"^\|[\s\-\|:]+\|$", RegexOptions.Compiled);

    // ── Public API ────────────────────────────────────────────────────────────

    public static SpecTree Parse(string markdown)
    {
        var lines = markdown.Split('\n').Select(l => l.TrimEnd()).ToArray();
        var roots = new List<SpecNode>();
        var headingStack = new List<(int Level, SpecNode Node, SectionSemantics Semantics)>();

        // Health counters
        int hHeadings = 0, hReq = 0, hUs = 0, hTest = 0, hBdd = 0,
            hClr = 0, hSc = 0, hEnt = 0, hDomain = 0, hTables = 0,
            hAssumptions = 0, hEdgeCases = 0, hDecision = 0;

        // Table buffer
        var tableBuffer = new List<string>();

        // Heading-level prose accumulator
        var contentLines = new List<string>();

        // ── Pending multi-line item ──────────────────────────────────────────
        // Used for FR/SC, Assumption, EdgeCase, Entity, ApiSurfaceItem blocks.
        // Fixed fields (Title, NodeType, SpecItemId) are set at detection time;
        // FullContent and Excerpt are set when the item is committed.
        SpecNode? pendingItem = null;
        var pendingLines = new List<string>();

        // ── Inline BDD accumulator ───────────────────────────────────────────
        // For numbered "1. **Given** ... **When** ... **Then** ..." items.
        // BddGiven/BddWhen/BddThen are init-only, so the node is created at commit.
        bool inInlineBdd = false;
        var inlineBddLines = new List<string>();

        // ── Traditional BDD state ────────────────────────────────────────────
        string? bddTitle = null;
        var bddGiven = new List<string>();
        var bddWhen = new List<string>();
        var bddThen = new List<string>();
        int bddPhase = 0; // 1=given, 2=when, 3=then

        // ── Multi-line Q/A state ─────────────────────────────────────────────
        string? qaQuestion = null;
        var qaAnswerLines = new List<string>();
        bool qaInAnswer = false;

        // ── Context helpers ───────────────────────────────────────────────────
        SectionSemantics ActiveSemantics() =>
            headingStack.Count > 0 ? headingStack[^1].Semantics : SectionSemantics.Generic;

        bool InAnyContext(SectionSemantics sem) =>
            headingStack.Any(h => h.Semantics == sem);

        SpecNode? ActiveParent() =>
            headingStack.Count > 0 ? headingStack[^1].Node : null;

        bool InClarificationsContext() => InAnyContext(SectionSemantics.Clarifications);
        bool IsInDecisionSession() => headingStack.Any(h => DateHeadingRe.IsMatch(h.Node.Title));
        bool InBddContext() => InAnyContext(SectionSemantics.UserStory) || InAnyContext(SectionSemantics.AcceptanceScenarios);
        bool InKeyEntitiesContext() => InAnyContext(SectionSemantics.KeyEntities);
        bool InAssumptionsContext() => ActiveSemantics() == SectionSemantics.Assumptions;
        bool InEdgeCasesContext() => ActiveSemantics() == SectionSemantics.EdgeCases;
        bool InApiSurfaceContext() => InAnyContext(SectionSemantics.ApiSurface);

        // ── Flush helpers ─────────────────────────────────────────────────────

        void CommitPending()
        {
            if (pendingItem == null) { pendingLines.Clear(); return; }
            var par = ActiveParent();
            if (par != null)
            {
                var relevant = pendingLines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
                if (relevant.Count > 0)
                {
                    pendingItem.FullContent = string.Join("\n", pendingLines).Trim();
                    var excerptRaw = StripMarkdown(relevant[0]);
                    pendingItem.Excerpt = excerptRaw.Length > 200 ? excerptRaw[..200] : excerptRaw;
                    par.Children.Add(pendingItem);
                    switch (pendingItem.NodeType)
                    {
                        case SpecNodeType.Requirement: hReq++; break;
                        case SpecNodeType.SuccessCriterion: hSc++; break;
                        case SpecNodeType.AcceptanceTest: hTest++; break;
                        case SpecNodeType.Entity: hEnt++; break;
                        case SpecNodeType.Assumption: hAssumptions++; break;
                        case SpecNodeType.EdgeCase: hEdgeCases++; break;
                        case SpecNodeType.UserStory: hUs++; break;
                    }
                }
            }
            pendingItem = null;
            pendingLines.Clear();
        }

        void CommitInlineBdd()
        {
            if (!inInlineBdd || inlineBddLines.Count == 0)
            {
                inInlineBdd = false;
                inlineBddLines.Clear();
                return;
            }
            var par = ActiveParent();
            if (par != null)
            {
                var fullText = string.Join(" ", inlineBddLines.Select(l => l.Trim()));
                fullText = Regex.Replace(fullText, @"^\d+\.\s+", "");
                var (given, when, then) = SplitInlineBdd(fullText);
                var title = StripMarkdown(given ?? fullText);
                if (title.Length > 200) title = title[..200];
                par.Children.Add(new SpecNode
                {
                    Title = title,
                    NodeType = SpecNodeType.BddScenario,
                    HeadingLevel = 0,
                    BddGiven = given,
                    BddWhen = when,
                    BddThen = then,
                    FullContent = fullText,
                });
                hBdd++;
                hTest++;
            }
            inInlineBdd = false;
            inlineBddLines.Clear();
        }

        void FlushQaPair()
        {
            if (qaQuestion == null) { qaAnswerLines.Clear(); qaInAnswer = false; return; }
            var par = ActiveParent();
            if (par != null)
            {
                var answer = string.Join(" ", qaAnswerLines).Trim();
                var titleText = StripMarkdown(qaQuestion);
                if (titleText.Length > 160) titleText = titleText[..160];
                var content = string.IsNullOrEmpty(answer)
                    ? $"Q: {qaQuestion}"
                    : $"Q: {qaQuestion}\nA: {answer}";
                var isDecision = IsInDecisionSession();
                par.Children.Add(new SpecNode
                {
                    Title = titleText,
                    NodeType = isDecision ? SpecNodeType.DecisionNode : SpecNodeType.QaPair,
                    HeadingLevel = 0,
                    QuestionText = qaQuestion,
                    AnswerText = answer.Length > 0 ? answer : null,
                    FullContent = content,
                });
                if (isDecision) hDecision++; else hClr++;
            }
            qaQuestion = null;
            qaAnswerLines.Clear();
            qaInAnswer = false;
        }

        void FlushBddScenario()
        {
            if (bddTitle == null && bddGiven.Count == 0 && bddWhen.Count == 0 && bddThen.Count == 0)
                return;
            var par = ActiveParent();
            if (par != null)
            {
                var title = bddTitle ?? (bddGiven.Count > 0 ? bddGiven[0] : "Scenario");
                if (title.Length > 200) title = title[..200];
                var given = string.Join("\n", bddGiven);
                var when  = string.Join("\n", bddWhen);
                var then  = string.Join("\n", bddThen);
                var sb = new StringBuilder();
                if (given.Length > 0) { sb.AppendLine("Given"); foreach (var g in bddGiven) sb.AppendLine($"  {g}"); }
                if (when.Length > 0)  { sb.AppendLine("When");  foreach (var w in bddWhen)  sb.AppendLine($"  {w}"); }
                if (then.Length > 0)  { sb.AppendLine("Then");  foreach (var t in bddThen)  sb.AppendLine($"  {t}"); }
                par.Children.Add(new SpecNode
                {
                    Title = title,
                    NodeType = SpecNodeType.BddScenario,
                    HeadingLevel = 0,
                    BddGiven = given.Length > 0 ? given : null,
                    BddWhen  = when.Length > 0  ? when  : null,
                    BddThen  = then.Length > 0  ? then  : null,
                    FullContent = sb.ToString().Trim(),
                });
                hBdd++;
                hTest++;
            }
            bddTitle = null;
            bddGiven.Clear();
            bddWhen.Clear();
            bddThen.Clear();
            bddPhase = 0;
        }

        void FlushContent()
        {
            if (headingStack.Count == 0 || contentLines.Count == 0) { contentLines.Clear(); return; }
            var node = headingStack[^1].Node;
            var relevant = contentLines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            if (relevant.Count == 0) { contentLines.Clear(); return; }
            var full = string.Join("\n", contentLines).Trim();
            if (!string.IsNullOrEmpty(full) && string.IsNullOrEmpty(node.FullContent))
                node.FullContent = full;
            if (string.IsNullOrEmpty(node.Excerpt))
                node.Excerpt = string.Join(" ", relevant.Take(5)).Trim();
            contentLines.Clear();
        }

        void FlushAll()
        {
            FlushTableBuffer(tableBuffer, headingStack, roots, ref hTables);
            CommitPending();
            CommitInlineBdd();
            FlushQaPair();
            FlushBddScenario();
            FlushContent();
        }

        // ── Main parse loop ───────────────────────────────────────────────────

        foreach (var line in lines)
        {
            // ── Heading ───────────────────────────────────────────────────────
            var hm = HeadingRe.Match(line);
            if (hm.Success)
            {
                FlushAll();
                var level = hm.Groups[1].Value.Length;
                var rawTitle = hm.Groups[2].Value.Trim();
                var title = StripMarkdown(rawTitle);
                var semantics = DetectSemantics(title);
                var nodeType = semantics == SectionSemantics.UserStory ? SpecNodeType.UserStory
                             : level == 1 ? SpecNodeType.Module
                             : level == 2 ? SpecNodeType.Section
                             : level == 3 ? SpecNodeType.SubSection
                             : SpecNodeType.DeepSection;
                hHeadings++;
                if (nodeType == SpecNodeType.UserStory) hUs++;
                var node = new SpecNode
                {
                    Title = title,
                    NodeType = nodeType,
                    HeadingLevel = level,
                    Semantics = semantics,
                };
                while (headingStack.Count > 0 && headingStack[^1].Level >= level)
                    headingStack.RemoveAt(headingStack.Count - 1);
                if (headingStack.Count == 0) roots.Add(node);
                else headingStack[^1].Node.Children.Add(node);
                headingStack.Add((level, node, semantics));
                continue;
            }

            // ── Table ─────────────────────────────────────────────────────────
            if (TableRowRe.IsMatch(line))
            {
                CommitPending();
                CommitInlineBdd();
                FlushQaPair();
                FlushBddScenario();
                tableBuffer.Add(line);
                continue;
            }
            if (tableBuffer.Count > 0)
                FlushTableBuffer(tableBuffer, headingStack, roots, ref hTables);

            // ── Blank line ────────────────────────────────────────────────────
            if (string.IsNullOrWhiteSpace(line))
            {
                // Blank line terminates pending FR/SC spec items.
                if (pendingItem != null &&
                    pendingItem.NodeType is SpecNodeType.Requirement
                                        or SpecNodeType.SuccessCriterion
                                        or SpecNodeType.AcceptanceTest)
                    CommitPending();

                // Blank line terminates an inline BDD scenario.
                CommitInlineBdd();

                // Blank line terminates a multi-line Q/A that has an answer.
                if (qaInAnswer && qaAnswerLines.Count > 0)
                    FlushQaPair();

                contentLines.Add(line);
                continue;
            }

            // ── Content before any heading ────────────────────────────────────
            if (headingStack.Count == 0)
            {
                contentLines.Add(line);
                continue;
            }

            var parent = headingStack[^1].Node;

            // ── Metadata (frontmatter-style lines near top-level heading) ─────
            // Only fire when we are directly inside the H1 (headingStack depth == 1).
            if (headingStack.Count == 1)
            {
                var mm = MetadataRe.Match(line);
                if (mm.Success)
                {
                    CommitPending();
                    CommitInlineBdd();
                    var key = mm.Groups[1].Value.Trim();
                    var value = StripMarkdown(mm.Groups[2].Value.Trim());
                    parent.Children.Add(new SpecNode
                    {
                        Title = $"{key}: {value}",
                        NodeType = SpecNodeType.Metadata,
                        HeadingLevel = 0,
                        FullContent = line.Trim(),
                    });
                    continue;
                }
            }

            // ── Clarifications context: Q/A handling ──────────────────────────
            if (InClarificationsContext())
            {
                // Inline format: "- Q: question → A: answer" (all on one line)
                var iqm = InlineQaRe.Match(line);
                if (iqm.Success)
                {
                    CommitPending();
                    FlushQaPair();
                    var q = iqm.Groups[1].Value.Trim();
                    var a = iqm.Groups[2].Value.Trim();
                    var titleText = StripMarkdown(q);
                    if (titleText.Length > 160) titleText = titleText[..160];
                    var isDecision = IsInDecisionSession();
                    parent.Children.Add(new SpecNode
                    {
                        Title = titleText,
                        NodeType = isDecision ? SpecNodeType.DecisionNode : SpecNodeType.QaPair,
                        HeadingLevel = 0,
                        QuestionText = q,
                        AnswerText = a,
                        FullContent = $"Q: {q}\nA: {a}",
                    });
                    if (isDecision) hDecision++; else hClr++;
                    continue;
                }

                // Multi-line: "Q: question" starts a new pair
                var qm = QaQuestionRe.Match(line);
                if (qm.Success)
                {
                    CommitPending();
                    FlushQaPair();
                    qaQuestion = qm.Groups[1].Value.Trim();
                    qaInAnswer = false;
                    continue;
                }

                // Multi-line: "A: answer" belongs to the open question
                var am = QaAnswerRe.Match(line);
                if (am.Success && qaQuestion != null)
                {
                    qaAnswerLines.Add(am.Groups[1].Value.Trim());
                    qaInAnswer = true;
                    continue;
                }

                // Continuation of multi-line question (before "A:")
                if (qaQuestion != null && !qaInAnswer)
                {
                    qaQuestion += " " + line.Trim();
                    continue;
                }

                // Continuation of multi-line answer
                if (qaInAnswer)
                {
                    qaAnswerLines.Add(line.Trim());
                    continue;
                }

                // No active Q/A state — accumulate as section content, do NOT create spec items
                contentLines.Add(line);
                continue;
            }

            // ── BDD context: acceptance scenario handling ──────────────────────
            if (InBddContext())
            {
                // Numbered inline BDD: "1. **Given** ..."
                if (NumberedBddStartRe.IsMatch(line))
                {
                    CommitPending();
                    CommitInlineBdd();
                    FlushBddScenario();
                    inInlineBdd = true;
                    inlineBddLines.Clear();
                    inlineBddLines.Add(line.Trim());
                    continue;
                }

                // Continuation of an in-progress inline BDD item (indented wrap)
                if (inInlineBdd)
                {
                    if (ContinuationRe.IsMatch(line))
                    {
                        inlineBddLines.Add(line.Trim());
                        continue;
                    }
                    // Non-indented line ends the inline BDD; fall through
                    CommitInlineBdd();
                }

                // Traditional BDD scenario header: "**Scenario 1:** title"
                var scenarioM = BddScenarioRe.Match(line);
                if (scenarioM.Success && Regex.IsMatch(line, @"Scenario", RegexOptions.IgnoreCase))
                {
                    var check = line.Replace("*", "").Replace("-", "").Trim();
                    if (!string.IsNullOrWhiteSpace(check))
                    {
                        CommitPending();
                        FlushBddScenario();
                        var rawT = scenarioM.Groups[1].Value.Trim();
                        bddTitle = string.IsNullOrEmpty(rawT)
                            ? ExtractScenarioTitle(line)
                            : StripMarkdown(rawT);
                        bddPhase = 0;
                        continue;
                    }
                }

                // Traditional BDD step keywords
                if (bddTitle != null)
                {
                    var km = BddKeywordRe.Match(line);
                    if (km.Success)
                    {
                        var keyword = km.Groups[1].Value.ToLowerInvariant();
                        var stepText = km.Groups[2].Value.Trim();
                        switch (keyword)
                        {
                            case "given": bddGiven.Add(stepText); bddPhase = 1; break;
                            case "when":  bddWhen.Add(stepText);  bddPhase = 2; break;
                            case "then":  bddThen.Add(stepText);  bddPhase = 3; break;
                            default:
                                if (bddPhase == 1) bddGiven.Add(stepText);
                                else if (bddPhase == 2) bddWhen.Add(stepText);
                                else bddThen.Add(stepText);
                                break;
                        }
                        continue;
                    }
                    var cleanLine = StripMarkdown(line.TrimStart('-', '*', ' '));
                    if (!string.IsNullOrWhiteSpace(cleanLine))
                    {
                        if (bddPhase == 1) bddGiven.Add(cleanLine);
                        else if (bddPhase == 2) bddWhen.Add(cleanLine);
                        else if (bddPhase == 3) bddThen.Add(cleanLine);
                        else contentLines.Add(line);
                        continue;
                    }
                }

                // Non-BDD content within a BDD section (narrative, headers)
                contentLines.Add(line);
                continue;
            }

            // ── Key Entities context: entity definitions ───────────────────────
            if (InKeyEntitiesContext())
            {
                // Entity start: "- **EntityName**: description"
                var em = EntityDefRe.Match(line);
                if (em.Success)
                {
                    CommitPending();
                    var entityName = em.Groups[1].Value.Trim();
                    pendingItem = new SpecNode
                    {
                        Title = entityName,
                        NodeType = SpecNodeType.Entity,
                        HeadingLevel = 0,
                        SpecItemId = entityName,
                    };
                    pendingLines.Clear();
                    pendingLines.Add(line.Trim());
                    continue;
                }
                // Continuation of entity description
                if (pendingItem?.NodeType == SpecNodeType.Entity)
                {
                    pendingLines.Add(line.TrimStart());
                    continue;
                }
                contentLines.Add(line);
                continue;
            }

            // ── Assumptions context: bullet items ─────────────────────────────
            if (InAssumptionsContext())
            {
                var bm = BulletStartRe.Match(line);
                if (bm.Success)
                {
                    CommitPending();
                    var bulletText = bm.Groups[1].Value.Trim();
                    var titleText = StripMarkdown(bulletText);
                    if (titleText.Length > 200) titleText = titleText[..200];
                    pendingItem = new SpecNode
                    {
                        Title = titleText,
                        NodeType = SpecNodeType.Assumption,
                        HeadingLevel = 0,
                    };
                    pendingLines.Clear();
                    pendingLines.Add(bulletText);
                    continue;
                }
                if (pendingItem?.NodeType == SpecNodeType.Assumption)
                {
                    pendingLines.Add(line.TrimStart());
                    continue;
                }
                contentLines.Add(line);
                continue;
            }

            // ── Edge Cases context: bullet items ──────────────────────────────
            if (InEdgeCasesContext())
            {
                var bm = BulletStartRe.Match(line);
                if (bm.Success)
                {
                    CommitPending();
                    var bulletText = bm.Groups[1].Value.Trim();
                    var titleText = StripMarkdown(bulletText);
                    if (titleText.Length > 200) titleText = titleText[..200];
                    pendingItem = new SpecNode
                    {
                        Title = titleText,
                        NodeType = SpecNodeType.EdgeCase,
                        HeadingLevel = 0,
                    };
                    pendingLines.Clear();
                    pendingLines.Add(bulletText);
                    continue;
                }
                if (pendingItem?.NodeType == SpecNodeType.EdgeCase)
                {
                    pendingLines.Add(line.TrimStart());
                    continue;
                }
                contentLines.Add(line);
                continue;
            }

            // ── API Surface context: bullet items ─────────────────────────────
            if (InApiSurfaceContext())
            {
                var bm = BulletStartRe.Match(line);
                if (bm.Success)
                {
                    CommitPending();
                    var bulletText = bm.Groups[1].Value.Trim();
                    var titleText = StripMarkdown(bulletText);
                    if (titleText.Length > 200) titleText = titleText[..200];
                    pendingItem = new SpecNode
                    {
                        Title = titleText,
                        NodeType = SpecNodeType.ApiSurfaceItem,
                        HeadingLevel = 0,
                    };
                    pendingLines.Clear();
                    pendingLines.Add(bulletText);
                    continue;
                }
                if (pendingItem?.NodeType == SpecNodeType.ApiSurfaceItem)
                {
                    if (ContinuationRe.IsMatch(line))
                    {
                        pendingLines.Add(line.TrimStart());
                        continue;
                    }
                    // Non-indented line ends the API Surface bullet
                    CommitPending();
                }
                contentLines.Add(line);
                continue;
            }

            // ── SpecItem at line start (FR / SC / US / AC / TS / REQ / NFR) ───
            {
                var sm = SpecItemStartRe.Match(line);
                if (sm.Success)
                {
                    CommitPending();
                    var prefix = sm.Groups[1].Value.ToUpperInvariant();
                    var numStr = sm.Groups[2].Value;
                    var itemId = $"{prefix}-{numStr.PadLeft(3, '0')}";
                    var nodeType = prefix switch
                    {
                        "FR" or "NFR" or "REQ" => SpecNodeType.Requirement,
                        "US" or "UC"           => SpecNodeType.UserStory,
                        "SC"                   => SpecNodeType.SuccessCriterion,
                        "AC" or "TS"           => SpecNodeType.AcceptanceTest,
                        _                      => SpecNodeType.Requirement,
                    };
                    var rawTitle = StripMarkdown(line.Trim().TrimStart('-', '*', '>', ' '));
                    if (rawTitle.Length > 200) rawTitle = rawTitle[..200];
                    pendingItem = new SpecNode
                    {
                        Title = rawTitle,
                        NodeType = nodeType,
                        HeadingLevel = 0,
                        SpecItemId = itemId,
                    };
                    pendingLines.Clear();
                    pendingLines.Add(line.Trim());
                    continue;
                }
            }

            // ── Continuation of a pending FR / SC spec item ───────────────────
            if (pendingItem != null &&
                pendingItem.NodeType is SpecNodeType.Requirement
                                    or SpecNodeType.SuccessCriterion
                                    or SpecNodeType.AcceptanceTest)
            {
                pendingLines.Add(line);
                continue;
            }

            // ── Inline user story (fallback for list-form user stories) ────────
            var um = UserStoryInlineRe.Match(line);
            if (um.Success)
            {
                CommitPending();
                var title = StripMarkdown(um.Groups[1].Value.Trim());
                parent.Children.Add(new SpecNode
                {
                    Title = title,
                    NodeType = SpecNodeType.UserStory,
                    HeadingLevel = 0,
                });
                hUs++;
                continue;
            }

            // ── Inline clarification (fallback — only outside clarification sections)
            var cm = ClarificationInlineRe.Match(line);
            if (cm.Success)
            {
                CommitPending();
                var title = StripMarkdown(cm.Groups[1].Value.Trim());
                parent.Children.Add(new SpecNode
                {
                    Title = title,
                    NodeType = SpecNodeType.Clarification,
                    HeadingLevel = 0,
                });
                hClr++;
                continue;
            }

            // ── Default: heading-level prose accumulator ──────────────────────
            contentLines.Add(line);
        }

        // ── EOF flushes ───────────────────────────────────────────────────────
        FlushAll();

        foreach (var root in roots)
            PropagateStats(root);

        return new SpecTree
        {
            Roots = roots,
            Health = new SpecHealth
            {
                TotalHeadings  = hHeadings,
                Requirements   = hReq,
                UserStories    = hUs,
                Tests          = hTest,
                BddScenarios   = hBdd,
                Clarifications = hClr,
                Decisions      = hDecision,
                SuccessCriteria = hSc,
                Entities       = hEnt,
                DomainItems    = hDomain,
                TablesDetected = hTables,
                Assumptions    = hAssumptions,
                EdgeCases      = hEdgeCases,
            },
        };
    }

    // ── Inline BDD splitter ───────────────────────────────────────────────────
    // Splits "Given X, When Y, Then Z" into (X, Y, Z).

    private static (string? Given, string? When, string? Then) SplitInlineBdd(string text)
    {
        var t = Regex.Replace(text, @"\*+", "");

        var givenM = Regex.Match(t, @"\bGiven\b\s+(.+?)(?=\s*,?\s*\bWhen\b|\s*$)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var whenM  = Regex.Match(t, @"\bWhen\b\s+(.+?)(?=\s*,?\s*\bThen\b|\s*$)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var thenM  = Regex.Match(t, @"\bThen\b\s+(.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        return (
            givenM.Success ? givenM.Groups[1].Value.Trim().TrimEnd(',', ' ') : null,
            whenM.Success  ? whenM.Groups[1].Value.Trim().TrimEnd(',', ' ')  : null,
            thenM.Success  ? thenM.Groups[1].Value.Trim()                    : null
        );
    }

    // ── Semantic section detection ────────────────────────────────────────────

    private static SectionSemantics DetectSemantics(string title)
    {
        var t = title.ToLowerInvariant().Trim();

        // ISO-date headings (e.g. "2026-03-06 Q/A Session") are decision sessions
        if (DateHeadingRe.IsMatch(title)) return SectionSemantics.Clarifications;
        if (UserStoryHeadingRe.IsMatch(title)) return SectionSemantics.UserStory;
        if (Regex.IsMatch(t, @"\bclarification"))  return SectionSemantics.Clarifications;
        if (Regex.IsMatch(t, @"\bedge\s+case"))    return SectionSemantics.EdgeCases;
        if (Regex.IsMatch(t, @"\bassumption"))     return SectionSemantics.Assumptions;
        if (Regex.IsMatch(t, @"\bapi\s+surface|\bapi\b.*\binterface|\bapi\b.*\bdesign"))
            return SectionSemantics.ApiSurface;
        if (Regex.IsMatch(t, @"\bobservabilit"))   return SectionSemantics.Observability;
        if (Regex.IsMatch(t, @"\bsecurity\b|\baccess\s+control")) return SectionSemantics.Security;
        if (Regex.IsMatch(t, @"\bperformance\b|\bscalab"))        return SectionSemantics.Performance;
        if (Regex.IsMatch(t, @"\bacceptance\s+scenario|\bscenarios?\b"))
            return SectionSemantics.AcceptanceScenarios;
        if (Regex.IsMatch(t, @"\bfunctional\s+req|\brequirements?\b"))
            return SectionSemantics.RequirementsSection;
        if (Regex.IsMatch(t, @"\bsuccess\s+criteri|\bmeasurable\s+outcome"))
            return SectionSemantics.SuccessCriteriaSection;
        if (Regex.IsMatch(t, @"\bkey\s+entit|\bentities\b"))
            return SectionSemantics.KeyEntities;

        return SectionSemantics.Generic;
    }

    private static string ExtractScenarioTitle(string line)
    {
        var m = Regex.Match(line, @"Scenario\s*\d*[:\s–-]+\*{0,2}\s*(.+?)[\*]*\s*$",
            RegexOptions.IgnoreCase);
        return m.Success ? StripMarkdown(m.Groups[1].Value.Trim()) : "Scenario";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void FlushTableBuffer(
        List<string> buffer,
        List<(int Level, SpecNode Node, SectionSemantics Semantics)> stack,
        List<SpecNode> roots,
        ref int hTables)
    {
        if (buffer.Count < 2) { buffer.Clear(); return; }
        var tableNode = ParseTable(buffer, ref hTables);
        if (tableNode is not null)
        {
            if (stack.Count > 0) stack[^1].Node.Children.Add(tableNode);
            else roots.Add(tableNode);
        }
        buffer.Clear();
    }

    private static SpecNode? ParseTable(List<string> lines, ref int tableCount)
    {
        if (lines.Count < 2) return null;
        var headers = SplitCells(lines[0]);
        if (headers.Count == 0) return null;
        var dataStart = TableSepRe.IsMatch(lines[1]) ? 2 : 1;
        if (dataStart >= lines.Count) return null;

        var tableKind = ClassifyTable(headers);
        var title = "Table: " + string.Join(" | ", headers.Take(3));
        var tableNode = new SpecNode
        {
            Title = title,
            NodeType = SpecNodeType.TableSection,
            HeadingLevel = 0,
            TableKind = tableKind,
            ColumnHeaders = headers,
        };

        for (var i = dataStart; i < lines.Count; i++)
        {
            var cells = SplitCells(lines[i]);
            if (cells.Count == 0) continue;
            var cellText = string.Join(" ", cells);
            var specRefs = ExtractSpecRefs(cellText);
            var rowTitle = cells[0].Length > 120 ? cells[0][..120] : cells[0];
            if (string.IsNullOrWhiteSpace(rowTitle))
                rowTitle = cellText.Length > 120 ? cellText[..120] : cellText;

            tableNode.Children.Add(new SpecNode
            {
                Title = rowTitle,
                NodeType = SpecNodeType.TableRow,
                HeadingLevel = 0,
                CellValues = cells,
                LinkedSpecItemIds = specRefs,
            });
        }

        if (tableNode.Children.Count == 0) return null;
        tableCount++;
        return tableNode;
    }

    private static TableType ClassifyTable(List<string> headers)
    {
        var joined = string.Join(" ", headers).ToLowerInvariant();
        if (Regex.IsMatch(joined, @"\bsc\b|success criteria|criterion")) return TableType.Traceability;
        if (Regex.IsMatch(joined, @"\bfr\b|requirement"))                return TableType.RequirementMap;
        if (Regex.IsMatch(joined, @"\bus\b|user stor"))                  return TableType.UserStoryMap;
        if (Regex.IsMatch(joined, @"\bts\b|\btc\b|test case|expected"))  return TableType.TestMapping;
        if (Regex.IsMatch(joined, @"entity|field|\btype\b|attribute|property")) return TableType.EntityModel;
        if (Regex.IsMatch(joined, @"endpoint|\bmethod\b|\bpath\b|operation"))   return TableType.ApiSpec;
        if (Regex.IsMatch(joined, @"depend|prerequisite|blocking"))      return TableType.DependencyMap;
        return TableType.Generic;
    }

    private static List<string> ExtractSpecRefs(string text)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (Match m in SpecRefRe.Matches(text))
        {
            var prefix = m.Groups[1].Value.ToUpperInvariant();
            var num = m.Groups[2].Value.PadLeft(3, '0');
            var key = $"{prefix}-{num}";
            if (seen.Add(key)) result.Add(key);
        }
        return result;
    }

    private static List<string> SplitCells(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|')) trimmed = trimmed[1..];
        if (trimmed.EndsWith('|')) trimmed = trimmed[..^1];
        return [.. trimmed.Split('|').Select(c => c.Trim()).Where(c => c.Length > 0)];
    }

    private static void PropagateStats(SpecNode node)
    {
        node.ReqCount = 0;
        node.UserStoryCount = 0;
        node.TestCount = 0;
        node.ClarCount = 0;
        node.ScCount = 0;
        node.DecisionCount = 0;
        node.TotalDescendants = 0;

        foreach (var child in node.Children)
        {
            if (child.HeadingLevel == 0 && child.Children.Count == 0)
            {
                switch (child.NodeType)
                {
                    case SpecNodeType.Requirement:   node.ReqCount++;  break;
                    case SpecNodeType.UserStory:     node.UserStoryCount++; break;
                    case SpecNodeType.AcceptanceTest:
                    case SpecNodeType.BddScenario:  node.TestCount++; break;
                    case SpecNodeType.Clarification:
                    case SpecNodeType.QaPair:       node.ClarCount++; break;
                    case SpecNodeType.SuccessCriterion: node.ScCount++; break;
                    case SpecNodeType.DecisionNode: node.DecisionCount++; break;
                    // Assumption, EdgeCase, Entity, Metadata, ApiSurfaceItem:
                    // counted in TotalDescendants but intentionally excluded from
                    // req/test/clr semantic counts so they don't inflate those metrics.
                }
                node.TotalDescendants++;
            }
            else
            {
                PropagateStats(child);
                node.ReqCount       += child.ReqCount;
                node.UserStoryCount += child.UserStoryCount;
                node.TestCount      += child.TestCount;
                node.ClarCount      += child.ClarCount;
                node.ScCount        += child.ScCount;
                node.DecisionCount  += child.DecisionCount;
                node.TotalDescendants += child.TotalDescendants + 1;
            }
        }
    }

    private static bool IsDecisionHeadingTitle(string heading)
    {
        if (DateHeadingRe.IsMatch(heading)) return true;
        var lower = heading.ToLowerInvariant();
        return lower.StartsWith("q/a", StringComparison.Ordinal)
            || lower.StartsWith("q&a", StringComparison.Ordinal)
            || lower.StartsWith("decisions", StringComparison.Ordinal)
            || (lower.Contains("clarification") && lower.Contains("session"));
    }

    private static string StripMarkdown(string s) =>
        Regex.Replace(s, @"[*_`#\[\]]", "").Trim();

    // ── Tree navigation utilities ─────────────────────────────────────────────

    public static SpecNode? FindNode(IEnumerable<SpecNode> nodes, string id)
    {
        foreach (var node in nodes)
        {
            if (node.Id == id) return node;
            var found = FindNode(node.Children, id);
            if (found is not null) return found;
        }
        return null;
    }

    public static List<(SpecNode Node, int Depth, bool IsMatch)> GetFlatVisible(
        IEnumerable<SpecNode> roots,
        HashSet<string> expandedIds,
        string searchQuery)
    {
        var result = new List<(SpecNode, int, bool)>();
        HashSet<string>? matchIds = null;
        HashSet<string>? ancestorIds = null;

        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            matchIds = [];
            ancestorIds = [];
            CollectMatches(roots, searchQuery, matchIds, ancestorIds, []);
        }

        foreach (var root in roots)
            FlattenNode(root, 0, result, expandedIds, matchIds, ancestorIds);

        return result;
    }

    private static void FlattenNode(
        SpecNode node, int depth,
        List<(SpecNode, int, bool)> result,
        HashSet<string> expanded,
        HashSet<string>? matchIds,
        HashSet<string>? ancestorIds)
    {
        var isMatch    = matchIds?.Contains(node.Id) ?? false;
        var isAncestor = ancestorIds?.Contains(node.Id) ?? false;

        if (matchIds is not null && !isMatch && !isAncestor) return;

        result.Add((node, depth, isMatch));

        var forceExpand = matchIds is not null && isAncestor;
        if ((expanded.Contains(node.Id) || forceExpand) && node.Children.Count > 0)
            foreach (var child in node.Children)
                FlattenNode(child, depth + 1, result, expanded, matchIds, ancestorIds);
    }

    private static bool CollectMatches(
        IEnumerable<SpecNode> nodes, string query,
        HashSet<string> matchIds, HashSet<string> ancestorIds,
        List<string> path)
    {
        var anyMatch = false;
        foreach (var node in nodes)
        {
            var isMatch =
                node.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (node.SpecItemId?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || (node.QuestionText?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || (node.AnswerText?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || (node.FullContent?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || (node.BddGiven?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || (node.BddWhen?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                || (node.BddThen?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);

            path.Add(node.Id);
            var childMatch = CollectMatches(node.Children, query, matchIds, ancestorIds, path);
            path.RemoveAt(path.Count - 1);

            if (isMatch || childMatch)
            {
                if (isMatch) matchIds.Add(node.Id);
                foreach (var id in path) ancestorIds.Add(id);
                anyMatch = true;
            }
        }
        return anyMatch;
    }

    public static HashSet<string> GetDefaultExpanded(IEnumerable<SpecNode> roots)
    {
        var expanded = new HashSet<string>();
        var collapseSemantics = new HashSet<SectionSemantics>
        {
            SectionSemantics.Clarifications,
            SectionSemantics.EdgeCases,
            SectionSemantics.Assumptions,
            SectionSemantics.ApiSurface,
            SectionSemantics.KeyEntities,
            SectionSemantics.Observability,
            SectionSemantics.Security,
            SectionSemantics.Performance,
        };

        foreach (var root in roots)
        {
            expanded.Add(root.Id);
            foreach (var l2 in root.Children)
            {
                if (l2.HeadingLevel <= 0) continue;
                if (collapseSemantics.Contains(l2.Semantics)) continue;
                expanded.Add(l2.Id);
            }
        }
        return expanded;
    }

    // ── Candidate-based tree (fallback when markdown has no headings) ─────────

    public static SpecTree BuildFromCandidates(IReadOnlyList<ExtractionCandidate> candidates)
    {
        if (candidates.Count == 0) return new SpecTree();

        var roots = new List<SpecNode>();
        int reqCount = 0, testCount = 0, clrCount = 0, decisionCount = 0;

        var hasContext = candidates.Any(c => !string.IsNullOrWhiteSpace(c.ContextHeading));

        if (hasContext)
        {
            var groups = candidates
                .GroupBy(c => string.IsNullOrWhiteSpace(c.ContextHeading) ? "(Uncategorized)" : c.ContextHeading!)
                .OrderBy(g => g.Key == "(Uncategorized)" ? "￿" : g.Key);

            foreach (var g in groups)
            {
                var section = new SpecNode { Title = g.Key, NodeType = SpecNodeType.Section, HeadingLevel = 2 };

                if (g.Key != "(Uncategorized)" && IsDecisionHeadingTitle(g.Key))
                {
                    // All candidates under decision headings (ISO-date Q/A sessions) become DecisionNode
                    foreach (var c in g)
                    {
                        section.Children.Add(new SpecNode
                        {
                            Title = c.Title.Length > 200 ? c.Title[..200] : c.Title,
                            NodeType = SpecNodeType.DecisionNode,
                            HeadingLevel = 0,
                        });
                        decisionCount++;
                    }
                }
                else
                {
                    // Non-decision heading: deduplicate requirements by FR-ID to eliminate sub-bullet fragments
                    var seenFrIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var normalized = g
                        .Where(c =>
                        {
                            if (c.Classification != ScenarioKind.Requirement) return true;
                            var m = FrIdRe.Match(c.Title);
                            return !m.Success || seenFrIds.Add(m.Value);
                        })
                        .ToList();
                    AddKindSubGroups(section.Children, normalized, headingLevel: 3, ref reqCount, ref testCount, ref clrCount);
                }

                if (section.Children.Count > 0) roots.Add(section);
            }
        }
        else
        {
            // No context headings — normalize requirements globally by FR-ID
            var seenFrIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var normalized = candidates
                .Where(c =>
                {
                    if (c.Classification != ScenarioKind.Requirement) return true;
                    var m = FrIdRe.Match(c.Title);
                    return !m.Success || seenFrIds.Add(m.Value);
                })
                .ToList();
            AddKindSubGroups(roots, normalized, headingLevel: 2, ref reqCount, ref testCount, ref clrCount);
        }

        foreach (var root in roots) PropagateStats(root);

        return new SpecTree
        {
            Roots = roots,
            Health = new SpecHealth
            {
                TotalHeadings  = roots.Count,
                Requirements   = reqCount,
                Tests          = testCount,
                Clarifications = clrCount,
                Decisions      = decisionCount,
            },
        };
    }

    private static void AddKindSubGroups(
        List<SpecNode> target,
        List<ExtractionCandidate> candidates,
        int headingLevel,
        ref int reqCount, ref int testCount, ref int clrCount)
    {
        var nodeType = headingLevel <= 2 ? SpecNodeType.Section : SpecNodeType.SubSection;
        var ordered = candidates
            .GroupBy(c => c.Classification)
            .OrderBy(g => g.Key switch
            {
                ScenarioKind.Requirement => 0,
                ScenarioKind.Test => 1,
                _ => 2,
            });

        foreach (var kg in ordered)
        {
            var label = kg.Key switch
            {
                ScenarioKind.Requirement => "Requirements",
                ScenarioKind.Test => "Tests",
                _ => "Clarifications",
            };

            var group = new SpecNode { Title = label, NodeType = nodeType, HeadingLevel = headingLevel };

            foreach (var c in kg)
            {
                var itemType = c.Classification switch
                {
                    ScenarioKind.Requirement => SpecNodeType.Requirement,
                    ScenarioKind.Test => SpecNodeType.AcceptanceTest,
                    _ => SpecNodeType.Clarification,
                };
                group.Children.Add(new SpecNode
                {
                    Title = c.Title.Length > 200 ? c.Title[..200] : c.Title,
                    NodeType = itemType,
                    HeadingLevel = 0,
                });
                switch (c.Classification)
                {
                    case ScenarioKind.Requirement: reqCount++; break;
                    case ScenarioKind.Test: testCount++; break;
                    default: clrCount++; break;
                }
            }

            if (group.Children.Count > 0) target.Add(group);
        }
    }
}
