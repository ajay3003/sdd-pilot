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

    private static readonly Regex SpecItemRe = new(
        @"\b(FR|NFR|SC|US|UC|AC|TS|REQ)-?\s*(\d{1,4})\b",
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

    // Q/A patterns: "Q: text", "**Q:** text", "1. Q: text"
    private static readonly Regex QaQuestionRe = new(
        @"^\s*(?:\d+[\.\)]\s*)?\*{0,2}Q[:\.\)]\*{0,2}\s+(.+)$",
        RegexOptions.Compiled);

    private static readonly Regex QaAnswerRe = new(
        @"^\s*\*{0,2}A[:\.\)]\*{0,2}\s+(.+)$",
        RegexOptions.Compiled);

    // BDD scenario header: "**Scenario 1:** title", "Scenario 1: title", "Scenario: title"
    private static readonly Regex BddScenarioRe = new(
        @"^\s*(?:[-*]\s*)?\*{0,2}Scenario\s*(?:#?\d+)?[:\*\s–-]*\*{0,2}\s*(.*?)[\*]*\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // BDD step keywords
    private static readonly Regex BddKeywordRe = new(
        @"^\s*(?:[-*]\s*)?\*{0,2}(Given|When|Then|And|But)\*{0,2}\s+(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex EntityRe = new(
        @"\b([A-Z][a-z]{2,}(?:[A-Z][a-z]{2,})+)\b",
        RegexOptions.Compiled);

    private static readonly Regex TableRowRe = new(
        @"^\|(.+)\|$", RegexOptions.Compiled);

    private static readonly Regex TableSepRe = new(
        @"^\|[\s\-\|:]+\|$", RegexOptions.Compiled);

    private static readonly Regex SpecRefRe = new(
        @"\b(FR|NFR|SC|US|UC|AC|TS|REQ|TC)-?\s*(\d{1,4})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Public API ────────────────────────────────────────────────────────────

    public static SpecTree Parse(string markdown)
    {
        var lines = markdown.Split('\n').Select(l => l.TrimEnd()).ToArray();
        var roots = new List<SpecNode>();

        // headingStack: (level, node, semantics)
        var headingStack = new List<(int Level, SpecNode Node, SectionSemantics Semantics)>();

        // Health counters
        var hHeadings = 0;
        var hReq = 0; var hUs = 0; var hTest = 0; var hBdd = 0;
        var hClr = 0; var hSc = 0; var hEnt = 0; var hDomain = 0; var hTables = 0;

        // Full content accumulator (no line limit)
        var contentLines = new List<string>();

        // Table line buffer
        var tableBuffer = new List<string>();

        // Q/A state
        string? qaQuestion = null;
        var qaAnswerLines = new List<string>();
        bool qaInAnswer = false;

        // BDD state
        string? bddTitle = null;
        var bddGiven = new List<string>();
        var bddWhen = new List<string>();
        var bddThen = new List<string>();
        int bddPhase = 0; // 0=none, 1=given, 2=when, 3=then

        SectionSemantics ActiveSemantics() =>
            headingStack.Count > 0 ? headingStack[^1].Semantics : SectionSemantics.Generic;

        bool InClarificationsContext() =>
            headingStack.Any(h => h.Semantics == SectionSemantics.Clarifications);

        bool InBddContext() =>
            headingStack.Any(h =>
                h.Semantics == SectionSemantics.UserStory ||
                h.Semantics == SectionSemantics.AcceptanceScenarios);

        SpecNode? ActiveParent() =>
            headingStack.Count > 0 ? headingStack[^1].Node : null;

        void FlushQaPair()
        {
            if (qaQuestion == null) { qaAnswerLines.Clear(); qaInAnswer = false; return; }
            var parent = ActiveParent();
            if (parent != null)
            {
                var answer = string.Join(" ", qaAnswerLines).Trim();
                var title = StripMarkdown(qaQuestion);
                if (title.Length > 160) title = title[..160];
                var content = string.IsNullOrEmpty(answer)
                    ? $"Q: {qaQuestion}"
                    : $"Q: {qaQuestion}\nA: {answer}";
                parent.Children.Add(new SpecNode
                {
                    Title = title,
                    NodeType = SpecNodeType.QaPair,
                    HeadingLevel = 0,
                    QuestionText = qaQuestion,
                    AnswerText = answer.Length > 0 ? answer : null,
                    FullContent = content,
                });
                hClr++;
            }
            qaQuestion = null;
            qaAnswerLines.Clear();
            qaInAnswer = false;
        }

        void FlushBddScenario()
        {
            if (bddTitle == null && bddGiven.Count == 0 && bddWhen.Count == 0 && bddThen.Count == 0)
                return;
            var parent = ActiveParent();
            if (parent != null)
            {
                var title = bddTitle ?? (bddGiven.Count > 0 ? bddGiven[0] : "Scenario");
                if (title.Length > 200) title = title[..200];

                var given = string.Join("\n", bddGiven);
                var when = string.Join("\n", bddWhen);
                var then = string.Join("\n", bddThen);

                var sb = new StringBuilder();
                if (!string.IsNullOrEmpty(given)) { sb.AppendLine("Given"); foreach (var g in bddGiven) sb.AppendLine($"  {g}"); }
                if (!string.IsNullOrEmpty(when)) { sb.AppendLine("When"); foreach (var w in bddWhen) sb.AppendLine($"  {w}"); }
                if (!string.IsNullOrEmpty(then)) { sb.AppendLine("Then"); foreach (var t in bddThen) sb.AppendLine($"  {t}"); }

                parent.Children.Add(new SpecNode
                {
                    Title = title,
                    NodeType = SpecNodeType.BddScenario,
                    HeadingLevel = 0,
                    BddGiven = given.Length > 0 ? given : null,
                    BddWhen = when.Length > 0 ? when : null,
                    BddThen = then.Length > 0 ? then : null,
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

        // ── Main parse loop ──────────────────────────────────────────────────

        foreach (var line in lines)
        {
            // ── Heading ──────────────────────────────────────────────────────
            var hm = HeadingRe.Match(line);
            if (hm.Success)
            {
                FlushTableBuffer(tableBuffer, headingStack, roots, ref hTables);
                FlushQaPair();
                FlushBddScenario();
                FlushContent();

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

            // ── Table ────────────────────────────────────────────────────────
            if (TableRowRe.IsMatch(line))
            {
                tableBuffer.Add(line);
                continue;
            }
            if (tableBuffer.Count > 0)
                FlushTableBuffer(tableBuffer, headingStack, roots, ref hTables);

            if (string.IsNullOrWhiteSpace(line))
            {
                // Blank line: flush Q/A pair if we were fully in answer mode
                if (qaInAnswer && qaAnswerLines.Count > 0)
                    FlushQaPair();
                contentLines.Add(line);
                continue;
            }

            if (headingStack.Count == 0)
            {
                contentLines.Add(line);
                continue;
            }

            var parent = headingStack[^1].Node;
            var sem = ActiveSemantics();

            // ── Q/A mode (Clarifications sections) ──────────────────────────
            if (InClarificationsContext())
            {
                var qm = QaQuestionRe.Match(line);
                var am = QaAnswerRe.Match(line);

                if (qm.Success)
                {
                    FlushQaPair();
                    qaQuestion = qm.Groups[1].Value.Trim();
                    qaInAnswer = false;
                    continue;
                }
                if (am.Success && qaQuestion != null)
                {
                    qaAnswerLines.Add(am.Groups[1].Value.Trim());
                    qaInAnswer = true;
                    continue;
                }
                if (qaQuestion != null && !qaInAnswer)
                {
                    // Multi-line question continuation
                    qaQuestion += " " + line.Trim();
                    continue;
                }
                if (qaInAnswer)
                {
                    // Multi-line answer continuation
                    qaAnswerLines.Add(line.Trim());
                    continue;
                }
                // Fall through to standard processing if no Q/A state
            }

            // ── BDD mode (User Story / Acceptance Scenarios sections) ────────
            if (InBddContext())
            {
                var scenarioM = BddScenarioRe.Match(line);
                if (scenarioM.Success && !string.IsNullOrWhiteSpace(line.Replace("*", "").Replace("-", "").Trim()))
                {
                    // Verify it's actually a Scenario line (not just "---")
                    var rawScenarioTitle = scenarioM.Groups[1].Value.Trim();
                    if (!string.IsNullOrEmpty(rawScenarioTitle) || Regex.IsMatch(line, @"Scenario", RegexOptions.IgnoreCase))
                    {
                        FlushBddScenario();
                        bddTitle = string.IsNullOrEmpty(rawScenarioTitle)
                            ? ExtractScenarioTitle(line)
                            : StripMarkdown(rawScenarioTitle);
                        bddPhase = 0;
                        continue;
                    }
                }

                if (bddTitle != null)
                {
                    var km = BddKeywordRe.Match(line);
                    if (km.Success)
                    {
                        var keyword = km.Groups[1].Value.ToLowerInvariant();
                        var stepText = km.Groups[2].Value.Trim();
                        if (keyword == "given")
                        {
                            bddGiven.Add(stepText);
                            bddPhase = 1;
                        }
                        else if (keyword == "when")
                        {
                            bddWhen.Add(stepText);
                            bddPhase = 2;
                        }
                        else if (keyword == "then")
                        {
                            bddThen.Add(stepText);
                            bddPhase = 3;
                        }
                        else // And / But
                        {
                            var step = stepText;
                            if (bddPhase == 1) bddGiven.Add(step);
                            else if (bddPhase == 2) bddWhen.Add(step);
                            else if (bddPhase == 3) bddThen.Add(step);
                            else bddThen.Add(step);
                        }
                        continue;
                    }

                    // Continuation line for current BDD phase
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
            }

            // ── Standard item detection ──────────────────────────────────────

            // Spec ID patterns (FR-001, SC-002, etc.)
            var sm = SpecItemRe.Match(line);
            if (sm.Success)
            {
                var prefix = sm.Groups[1].Value.ToUpperInvariant();
                var numStr = sm.Groups[2].Value;
                var itemId = $"{prefix}-{numStr.PadLeft(3, '0')}";

                var nodeType = prefix switch
                {
                    "FR" or "NFR" or "REQ" => SpecNodeType.Requirement,
                    "US" or "UC" => SpecNodeType.UserStory,
                    "SC" => SpecNodeType.SuccessCriterion,
                    "AC" or "TS" => SpecNodeType.AcceptanceTest,
                    _ => SpecNodeType.Requirement,
                };

                var rawTitle = StripMarkdown(line.Trim().TrimStart('-', '*', '>', ' '));

                parent.Children.Add(new SpecNode
                {
                    Title = rawTitle,
                    NodeType = nodeType,
                    HeadingLevel = 0,
                    SpecItemId = itemId,
                    FullContent = line.Trim(),
                });

                CountByType(nodeType, ref hReq, ref hUs, ref hTest, ref hClr, ref hSc, ref hEnt, ref hDomain);
                continue;
            }

            // Inline user story pattern
            var um = UserStoryInlineRe.Match(line);
            if (um.Success)
            {
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

            // Inline clarification pattern (fallback when not in Q/A mode)
            var cm = ClarificationInlineRe.Match(line);
            if (cm.Success && !InClarificationsContext())
            {
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

            // Accumulate as full content
            contentLines.Add(line);
        }

        // ── End-of-file flushes ──────────────────────────────────────────────
        FlushTableBuffer(tableBuffer, headingStack, roots, ref hTables);
        FlushQaPair();
        FlushBddScenario();
        FlushContent();

        foreach (var root in roots)
            PropagateStats(root);

        return new SpecTree
        {
            Roots = roots,
            Health = new SpecHealth
            {
                TotalHeadings = hHeadings,
                Requirements = hReq,
                UserStories = hUs,
                Tests = hTest,
                BddScenarios = hBdd,
                Clarifications = hClr,
                SuccessCriteria = hSc,
                Entities = hEnt,
                DomainItems = hDomain,
                TablesDetected = hTables,
            },
        };
    }

    // ── Semantic section detection ────────────────────────────────────────────

    private static SectionSemantics DetectSemantics(string title)
    {
        var t = title.ToLowerInvariant().Trim();

        if (UserStoryHeadingRe.IsMatch(title)) return SectionSemantics.UserStory;
        if (Regex.IsMatch(t, @"\bclarification")) return SectionSemantics.Clarifications;
        if (Regex.IsMatch(t, @"\bedge\s+case")) return SectionSemantics.EdgeCases;
        if (Regex.IsMatch(t, @"\bassumption")) return SectionSemantics.Assumptions;
        if (Regex.IsMatch(t, @"\bapi\s+surface|\bapi\b.*\binterface|\bapi\b.*\bdesign")) return SectionSemantics.ApiSurface;
        if (Regex.IsMatch(t, @"\bobservabilit")) return SectionSemantics.Observability;
        if (Regex.IsMatch(t, @"\bsecurity\b|\baccess\s+control")) return SectionSemantics.Security;
        if (Regex.IsMatch(t, @"\bperformance\b|\bscalab")) return SectionSemantics.Performance;
        if (Regex.IsMatch(t, @"\bacceptance\s+scenario|\bscenarios?\b")) return SectionSemantics.AcceptanceScenarios;
        if (Regex.IsMatch(t, @"\bfunctional\s+req|\brequirements?\s*$")) return SectionSemantics.RequirementsSection;
        if (Regex.IsMatch(t, @"\bsuccess\s+criteri")) return SectionSemantics.SuccessCriteriaSection;

        return SectionSemantics.Generic;
    }

    private static string ExtractScenarioTitle(string line)
    {
        // Extract title from "**Scenario 1:** Happy path" → "Happy path"
        var m = Regex.Match(line, @"Scenario\s*\d*[:\s–-]+\*{0,2}\s*(.+?)[\*]*\s*$", RegexOptions.IgnoreCase);
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
        if (Regex.IsMatch(joined, @"\bfr\b|requirement")) return TableType.RequirementMap;
        if (Regex.IsMatch(joined, @"\bus\b|user stor")) return TableType.UserStoryMap;
        if (Regex.IsMatch(joined, @"\bts\b|\btc\b|test case|expected")) return TableType.TestMapping;
        if (Regex.IsMatch(joined, @"entity|field|\btype\b|attribute|property")) return TableType.EntityModel;
        if (Regex.IsMatch(joined, @"endpoint|\bmethod\b|\bpath\b|operation")) return TableType.ApiSpec;
        if (Regex.IsMatch(joined, @"depend|prerequisite|blocking")) return TableType.DependencyMap;
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
        node.TotalDescendants = 0;

        foreach (var child in node.Children)
        {
            if (child.HeadingLevel == 0 && child.Children.Count == 0)
            {
                switch (child.NodeType)
                {
                    case SpecNodeType.Requirement: node.ReqCount++; break;
                    case SpecNodeType.UserStory: node.UserStoryCount++; break;
                    case SpecNodeType.AcceptanceTest:
                    case SpecNodeType.BddScenario: node.TestCount++; break;
                    case SpecNodeType.Clarification:
                    case SpecNodeType.QaPair: node.ClarCount++; break;
                    case SpecNodeType.SuccessCriterion: node.ScCount++; break;
                }
                node.TotalDescendants++;
            }
            else
            {
                PropagateStats(child);
                node.ReqCount += child.ReqCount;
                node.UserStoryCount += child.UserStoryCount;
                node.TestCount += child.TestCount;
                node.ClarCount += child.ClarCount;
                node.ScCount += child.ScCount;
                node.TotalDescendants += child.TotalDescendants + 1;
            }
        }
    }

    private static string StripMarkdown(string s) =>
        Regex.Replace(s, @"[*_`#\[\]]", "").Trim();

    private static void CountByType(SpecNodeType t,
        ref int req, ref int us, ref int test, ref int clr, ref int sc, ref int ent, ref int domain)
    {
        switch (t)
        {
            case SpecNodeType.Requirement: req++; break;
            case SpecNodeType.UserStory: us++; break;
            case SpecNodeType.AcceptanceTest: test++; break;
            case SpecNodeType.Clarification:
            case SpecNodeType.QaPair: clr++; break;
            case SpecNodeType.SuccessCriterion: sc++; break;
            case SpecNodeType.Entity: ent++; break;
            case SpecNodeType.DomainItem: domain++; break;
        }
    }

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
        var isMatch = matchIds?.Contains(node.Id) ?? false;
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
            var isMatch = node.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
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
        // Sections that should start collapsed
        var collapseSemantics = new HashSet<SectionSemantics>
        {
            SectionSemantics.Clarifications,
            SectionSemantics.EdgeCases,
            SectionSemantics.Assumptions,
            SectionSemantics.ApiSurface,
            SectionSemantics.Observability,
            SectionSemantics.Security,
            SectionSemantics.Performance,
        };

        foreach (var root in roots)
        {
            expanded.Add(root.Id); // level 1 always expanded
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
        int reqCount = 0, testCount = 0, clrCount = 0;

        var hasContext = candidates.Any(c => !string.IsNullOrWhiteSpace(c.ContextHeading));

        if (hasContext)
        {
            var groups = candidates
                .GroupBy(c => string.IsNullOrWhiteSpace(c.ContextHeading) ? "(Uncategorized)" : c.ContextHeading!)
                .OrderBy(g => g.Key == "(Uncategorized)" ? "￿" : g.Key);

            foreach (var g in groups)
            {
                var section = new SpecNode { Title = g.Key, NodeType = SpecNodeType.Section, HeadingLevel = 2 };
                AddKindSubGroups(section.Children, g.ToList(), headingLevel: 3, ref reqCount, ref testCount, ref clrCount);
                roots.Add(section);
            }
        }
        else
        {
            AddKindSubGroups(roots, candidates.ToList(), headingLevel: 2, ref reqCount, ref testCount, ref clrCount);
        }

        foreach (var root in roots) PropagateStats(root);

        return new SpecTree
        {
            Roots = roots,
            Health = new SpecHealth
            {
                TotalHeadings = roots.Count,
                Requirements = reqCount,
                Tests = testCount,
                Clarifications = clrCount,
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
