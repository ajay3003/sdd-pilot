using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using BirkNext.TestPlanning;

namespace BirkNext.Api.Services.TestPlanning;

/// <summary>
/// The one Azure DevOps Test Plans export rule (pure, no I/O): saved cases of one saved plan revision → validated import rows → the same rows as
/// an XLSX workbook or a CSV file. Layout per Microsoft Learn "Bulk import or export test cases" (2026-08-10): columns ID, Work Item Type,
/// Title, Test Step, Step Action, Step Expected, Area Path, Assigned To, State; one row per step with the case fields repeated; a blank ID
/// creates a new test case; re-importing a matching ID replaces all of its steps. Nothing is filled in that the case does not state, no
/// BirkNext identifier is ever used as an Azure DevOps id, and destination values are checked for form only.
/// </summary>
public static partial class AdoTestPlanExporter
{
    public const int MaxTitleLength = 255;

    [GeneratedRegex(@"^([A-Za-z]{1,10}-\d{1,6}):\s*(?:[-*+]\s+)?\1\s*:\s*")]
    private static partial Regex RepeatedId();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^[0-9]{1,10}$")]
    private static partial Regex WorkItemId();

    // ── Preview (validation + rows) ─────────────────────────────────────────────────────────────────────────

    public static AdoExportPreview Preview(TestPlanDraft plan, AdoExportRequest request)
    {
        var profile = request.Profile;
        var issues = new List<AdoExportIssue>();
        var fields = Fields(profile, request.Mode, issues);
        if (request.Mode == AdoExportMode.UpdateExisting)
        {
            issues.Add(new(AdoIssueLevel.Warning, "Updating replaces ALL existing steps of each listed Azure DevOps test case with the steps in this file; missing steps are removed. Export the full test case from Azure DevOps first if you need a copy."));
            if (!request.ExistingIdsConfirmed)
                issues.Add(new(AdoIssueLevel.Blocking, "Confirm that you checked every Azure DevOps test case id in Azure DevOps and that their steps may be replaced."));
        }
        var selected = request.CaseIds.Count == 0 ? null : request.CaseIds.ToHashSet(StringComparer.Ordinal);
        var cases = new List<AdoExportCase>();
        var rows = new List<AdoExportRow>();
        var usedIds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in plan.Cases)
        {
            var id = c.Source.ScenarioId;
            var caseIssues = new List<AdoExportIssue>();
            // The chosen language for the whole case (never mixed): translated only when the case has a complete text in that language.
            var text = ScenarioPlanning.Localized(c, request.Language);
            var title = CleanTitle(text.Title);
            var review = ReviewLabel(c.Review);
            string? excluded = c.Review is TestCaseReviewState.Excluded or TestCaseReviewState.Superseded ? $"{review} in the plan — never exported"
                : selected is not null && !selected.Contains(id) ? "Not selected"
                : c.Review != TestCaseReviewState.Accepted && !request.IncludeNotApproved ? "Not approved yet — choose \"Include cases that are not approved\" to export it"
                : null;

            var steps = Steps(text.Steps, text.StepExpected, text.Preconditions, profile.Preconditions);
            if (excluded is null && !string.IsNullOrWhiteSpace(request.Language) && text.Note is { } languageNote)
                caseIssues.Add(new(text.Translated ? AdoIssueLevel.Warning : AdoIssueLevel.Info, languageNote, id));
            if (excluded is null)
            {
                if (title.Length == 0) caseIssues.Add(new(AdoIssueLevel.Blocking, "The test case has no title.", id));
                if (title.Length > MaxTitleLength) caseIssues.Add(new(AdoIssueLevel.Blocking, $"The title is longer than {MaxTitleLength} characters; shorten it in Plan & prepare.", id));
                if (steps.Count == 0) caseIssues.Add(new(AdoIssueLevel.Blocking, "The test case has no steps.", id));
                var noAction = steps.Count(s => s.Action.Length == 0);
                if (noAction > 0) caseIssues.Add(new(AdoIssueLevel.Blocking, $"{noAction} step{(noAction == 1 ? " has" : "s have")} no action.", id));
                var noExpected = steps.Count(s => s.Expected.Length == 0);
                if (noExpected > 0) caseIssues.Add(new(AdoIssueLevel.Blocking, $"{noExpected} step{(noExpected == 1 ? " needs" : "s need")} an expected result.", id));
            }

            string adoId = "";
            var remembered = false;
            if (excluded is null && request.Mode == AdoExportMode.UpdateExisting)
            {
                var typed = request.ExistingIds.GetValueOrDefault(id)?.Trim();
                var given = typed is { Length: > 0 } ? typed : profile.KnownTestCaseIds.GetValueOrDefault($"{plan.Id:N}/{id}")?.Trim() ?? c.VerifiedAzureDevOpsTestCaseId?.Trim();
                remembered = typed is not { Length: > 0 } && !string.IsNullOrEmpty(given);
                if (string.IsNullOrEmpty(given)) caseIssues.Add(new(AdoIssueLevel.Blocking, "Update mode needs this case's Azure DevOps test case id (as shown in Azure DevOps).", id));
                else if (!WorkItemId().IsMatch(given)) caseIssues.Add(new(AdoIssueLevel.Blocking, $"\"{given}\" is not an Azure DevOps work item id (digits only).", id));
                else if (usedIds.TryGetValue(given, out var other)) caseIssues.Add(new(AdoIssueLevel.Blocking, $"Azure DevOps id {given} is also given for \"{other}\"; each test case needs its own id.", id));
                else { adoId = given; usedIds[given] = title; }
            }

            string? priority = null;
            if (excluded is null && profile.IncludePriority)
            {
                var raw = c.Edits.Priority?.Trim();
                if (string.IsNullOrEmpty(raw)) caseIssues.Add(new(AdoIssueLevel.Info, "No priority set; the Priority cell stays empty.", id));
                else if (AdoExport.PriorityMap.TryGetValue(raw, out var mapped)) priority = mapped;
                else caseIssues.Add(new(AdoIssueLevel.Warning, $"Priority \"{raw}\" has no Azure DevOps value (1–4); the cell stays empty. Use Critical/High/Medium/Low or 1–4.", id));
            }

            var included = excluded is null && caseIssues.All(i => i.Level != AdoIssueLevel.Blocking);
            var origin = ManualCaseIdentity.Origin(c);
            var requirementSources = origin.Requirements.Select(q => q.SpecificationFile is { } f ? $"{q.RequirementId} ({f})" : q.RequirementId).ToList();
            var scenario = c.ParentScenario is { } scenarioKey ? (Key: scenarioKey, Title: plan.Cases.FirstOrDefault(x => x.Source.Kind != ScenarioSourceKind.Manual && x.Source.ScenarioId == scenarioKey)?.Generated.Title ?? c.ScenarioTitle) : default;
            var story = c.Generated.Story ?? plan.Cases.FirstOrDefault(x => x.Source.Kind != ScenarioSourceKind.Manual && x.Source.ScenarioId == c.ParentScenario)?.Generated.Story;
            var description = Describe(origin, requirementSources, $"{plan.Id:N}/{id}", plan.PlanName, c.Source.Kind == ScenarioSourceKind.Manual ? scenario.Title : null)
                + (story is null ? "" : $" User story: {story.StoryId ?? $"story {story.Ordinal}"} — {story.Title}.");
            cases.Add(new AdoExportCase
            {
                SourceSpecification = origin.SpecificationFile, SourceScenario = origin.SpecificationScenarioId, RequirementSources = requirementSources,
                AzureDevOpsIdRemembered = remembered && adoId.Length > 0,
                CaseId = id, CaseKey = $"{plan.Id:N}/{id}", Title = title, Review = review, Included = excluded is null, ExcludedReason = excluded, Steps = steps.Count,
                AzureDevOpsId = adoId.Length == 0 ? null : adoId, Requirements = RequirementIds(c), TestData = [.. c.TestData.Select(d => string.IsNullOrWhiteSpace(d.Alias) ? d.AssetId : $"{d.Alias} ({d.AssetId})")],
                Preconditions = text.Preconditions, Priority = priority, Issues = caseIssues,
                Language = text.Translated ? request.Language! : AdoExportCase.OriginalLanguage, ScenarioKey = scenario.Key, ScenarioTitle = scenario.Title, CaseRevision = plan.Revision,
            });
            if (!included) continue;
            for (var i = 0; i < steps.Count; i++)
                rows.Add(new AdoExportRow(adoId, AdoExport.WorkItemType, title, i + 1, steps[i].Action, steps[i].Expected, profile.AreaPath?.Trim() ?? "", profile.AssignedTo?.Trim() ?? "",
                    profile.State?.Trim() ?? "", profile.IncludePriority ? priority ?? "" : null, profile.IncludeSourceReferences ? description : null));
        }

        var exported = cases.Where(c => c.Included && c.Issues.All(i => i.Level != AdoIssueLevel.Blocking)).ToList();
        foreach (var dup in exported.GroupBy(c => c.Title, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            issues.Add(new(AdoIssueLevel.Warning, $"{dup.Count()} test cases share the title \"{dup.Key}\". They become separate test cases, but give them distinct titles so testers can tell them apart."));
        var withRequirements = exported.Count(c => c.Requirements.Count > 0);
        if (withRequirements > 0)
            issues.Add(new(AdoIssueLevel.Info, $"{withRequirements} test case(s) are linked to requirements in BirkNext. The import file does not create Azure DevOps requirement links; the mapping report lists them so you can link them in Azure DevOps."));
        if (profile.Preconditions == AdoPreconditionsMode.MappingReport && exported.Any(c => !string.IsNullOrWhiteSpace(c.Preconditions)))
            issues.Add(new(AdoIssueLevel.Info, "Preconditions are not an import column. They are listed in the mapping report (or choose \"Add preconditions as a first step\")."));
        if (exported.Any(c => c.TestData.Count > 0))
            issues.Add(new(AdoIssueLevel.Info, "Test Data is referenced by name in the mapping report only; no Test Data values are exported."));
        if (profile.Format == AdoExportFormat.Csv && profile.CsvSpreadsheetSafe && rows.Any(r => Cells(r).Any(Risky)))
            issues.Add(new(AdoIssueLevel.Warning, "Some cells start with = + - or @. In the CSV they get a leading apostrophe so a spreadsheet program never runs them; Azure DevOps keeps that apostrophe as text. Choose Excel (.xlsx) to keep them exactly."));
        if (exported.Count == 0) issues.Add(new(AdoIssueLevel.Blocking, "No test case is ready for export. Select cases, or fix the ones that need attention."));

        var attention = cases.Count(c => c.Included && c.Issues.Any(i => i.Level == AdoIssueLevel.Blocking));
        return new AdoExportPreview
        {
            ProjectKey = plan.ProjectKey ?? "", DraftId = plan.Id, PlanName = plan.PlanName, PlanRevision = plan.Revision, TotalCases = plan.Cases.Count,
            SelectedCases = cases.Count(c => c.Included), ReadyCases = exported.Count, CasesNeedingAttention = attention, Cases = cases, Rows = rows, Fields = fields,
            Issues = [.. issues, .. cases.SelectMany(c => c.Issues)],
            CanDownload = exported.Count > 0 && attention == 0 && issues.All(i => i.Level != AdoIssueLevel.Blocking),
            Headers = [.. AdoExport.Headers, .. profile.IncludePriority ? new[] { AdoExport.PriorityHeader } : [], .. profile.IncludeSourceReferences ? new[] { AdoExport.DescriptionHeader } : []],
        };
    }

    private static List<AdoFieldStatus> Fields(AdoExportProfile p, AdoExportMode mode, List<AdoExportIssue> issues)
    {
        var list = new List<AdoFieldStatus>();
        var area = p.AreaPath?.Trim();
        if (p.Target == AdoTarget.Server)
            list.Add(new("Area Path", area, AdoFieldCheck.NotRequired, "Azure DevOps Server does not require or change the Area Path on import."));
        else if (string.IsNullOrEmpty(area))
        {
            list.Add(new("Area Path", null, AdoFieldCheck.Missing, "Required for Azure DevOps Services."));
            issues.Add(new(AdoIssueLevel.Blocking, "Azure DevOps Area Path is missing (required for Azure DevOps Services), for example MyProject\\MyArea."));
        }
        else if (!AreaPathForm(area))
        {
            list.Add(new("Area Path", area, AdoFieldCheck.Invalid, "Use the project name and areas separated by \\, without empty parts or characters such as / : * ? \" < > |."));
            issues.Add(new(AdoIssueLevel.Blocking, "The Area Path is not a valid path (project\\area\\…)."));
        }
        else list.Add(new("Area Path", area, AdoFieldCheck.NeedsConfirmationInAzureDevOps, "Valid form; it must exist in your Azure DevOps project."));

        var assigned = p.AssignedTo?.Trim();
        if (string.IsNullOrEmpty(assigned))
            list.Add(new("Assigned To", null, AdoFieldCheck.Missing, "Left empty: the test cases are created unassigned."));
        else if (assigned.Length > 256 || assigned.Any(char.IsControl))
        {
            list.Add(new("Assigned To", assigned, AdoFieldCheck.Invalid, "Use a user's email or display name."));
            issues.Add(new(AdoIssueLevel.Blocking, "Assigned To is not a valid user name or email."));
        }
        else list.Add(new("Assigned To", assigned, AdoFieldCheck.NeedsConfirmationInAzureDevOps, "It must be a valid user in your organization."));

        var state = p.State?.Trim();
        if (string.IsNullOrEmpty(state))
        {
            list.Add(new("State", null, AdoFieldCheck.Missing, "Required."));
            issues.Add(new(AdoIssueLevel.Blocking, "The initial State is missing (Design in the default processes)."));
        }
        else if (state.Length > 128 || state.Any(char.IsControl))
        {
            list.Add(new("State", state, AdoFieldCheck.Invalid, "Use a state name of your process."));
            issues.Add(new(AdoIssueLevel.Blocking, "The State is not a valid state name."));
        }
        else
        {
            list.Add(new("State", state, AdoFieldCheck.NeedsConfirmationInAzureDevOps, mode == AdoExportMode.CreateNew
                ? "For new test cases it must be a state in the Proposed category of your process (Design by default)."
                : "For existing test cases use their current state or a valid next state of your workflow."));
            if (!string.Equals(state, "Design", StringComparison.Ordinal))
                issues.Add(new(AdoIssueLevel.Warning, $"The selected state \"{state}\" needs confirmation for your Azure DevOps process."));
        }
        list.Add(new("Work Item Type", AdoExport.WorkItemType, AdoFieldCheck.LocallyValid, "Always Test Case."));
        return list;
    }

    private static bool AreaPathForm(string path) =>
        path.Length <= 4000 && !path.StartsWith('\\') && !path.EndsWith('\\') && path.Split('\\').All(s => s.Trim().Length > 0 && s == s.Trim())
        && path.IndexOfAny(['/', ':', '*', '?', '"', '<', '>', '|', '\t', '\r', '\n']) < 0 && !path.Any(char.IsControl);

    private static List<(string Action, string Expected)> Steps(List<string> actions, List<string?> expected, string? preconditions, AdoPreconditionsMode mode)
    {
        var list = actions.Select((a, i) => (Text(a), Text(i < expected.Count ? expected[i] : null))).ToList();
        if (mode == AdoPreconditionsMode.FirstStep && Text(preconditions) is { Length: > 0 } pre)
            list.Insert(0, ($"Check the preconditions: {pre}", "The preconditions are met"));
        return list;
    }

    private static List<string> RequirementIds(TestCaseDraft c)
    {
        var automatic = c.Source.Kind is ScenarioSourceKind.SystemGuide or ScenarioSourceKind.Manual || c.Generated.Capability != TestScenarioDiscoveryEngine.Specification
            ? [] : c.Generated.Requirements.Select(r => r.RuleId);
        return [.. automatic.Concat(c.RequirementLinks.Select(l => l.RequirementId)).Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static string ReviewLabel(TestCaseReviewState state) => state switch
    {
        TestCaseReviewState.Accepted => "Approved", TestCaseReviewState.NeedsClarification => "Needs clarification", TestCaseReviewState.UnderReview => "Under review",
        TestCaseReviewState.Excluded => "Excluded", TestCaseReviewState.Superseded => "Superseded", _ => "Prepared",
    };

    /// <summary>The readable title without Markdown emphasis, list markers or a repeated requirement id ("FR-001: - **FR-001**: …" → "FR-001: …").</summary>
    public static string CleanTitle(string? title)
    {
        var t = Text(title).Replace("**", "", StringComparison.Ordinal).Replace("__", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal);
        t = Spaces().Replace(t, " ").Trim();
        while (t.Length > 0 && t[0] is '-' or '*' or '+' or '#' && t.Length > 1 && t[1] == ' ') t = t[2..].TrimStart();
        t = RepeatedId().Replace(t, "$1: ");
        return t.Trim();
    }

    /// <summary>Trimmed text with line breaks normalised to \n and control characters (other than \n and \t) removed.</summary>
    public static string Text(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : new string(value.Replace("\r\n", "\n").Replace('\r', '\n').Where(ch => ch is '\n' or '\t' || !char.IsControl(ch)).ToArray()).Trim();

    private static IEnumerable<string> Cells(AdoExportRow r) => [r.Id, r.WorkItemType, r.Title, r.StepAction, r.StepExpected, r.AreaPath, r.AssignedTo, r.State, r.Priority ?? "", r.Description ?? ""];

    /// <summary>The Description cell: where the case came from, in plain words, so the origin travels with the test case into Azure DevOps.</summary>
    internal static string Describe(ManualRunOrigin origin, IReadOnlyList<string> requirementSources, string caseKey, string planName, string? writtenUnderScenario = null)
    {
        var parts = new List<string>();
        parts.Add(origin.SpecificationFile is { } f
            ? $"Prepared in BirkNext from {f}{(origin.SpecificationScenarioId is { } sc ? $", acceptance scenario {sc}" : "")}."
            : origin.SourceKind == ScenarioSourceKind.Manual
                ? writtenUnderScenario is { Length: > 0 } st ? $"Written by hand in BirkNext under the scenario \"{st}\"." : "Written by hand in BirkNext (no specification source)."
                : "Prepared in BirkNext from a discovered scenario.");
        if (requirementSources.Count > 0) parts.Add($"Requirements: {string.Join("; ", requirementSources)}.");
        parts.Add($"BirkNext test case: {planName} ({caseKey}).");
        return string.Join(" ", parts);
    }

    private static bool Risky(string value) => value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t';

    private static IReadOnlyList<string> Values(AdoExportRow r, bool priority, bool description)
    {
        List<string> values = [r.Id, r.WorkItemType, r.Title, r.TestStep.ToString(System.Globalization.CultureInfo.InvariantCulture), r.StepAction, r.StepExpected, r.AreaPath, r.AssignedTo, r.State];
        if (priority) values.Add(r.Priority ?? "");
        if (description) values.Add(r.Description ?? "");
        return values;
    }

    // ── CSV ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>RFC 4180 CSV in UTF-8 (with BOM, so spreadsheet programs read æ ø å correctly): every field quoted, quotes doubled, line breaks kept
    /// inside quoted fields, rows separated by CRLF. With <paramref name="spreadsheetSafe"/>, a text cell starting with = + - @ or tab gets a
    /// leading apostrophe (the Test Step number is never changed).</summary>
    public static byte[] Csv(IReadOnlyList<AdoExportRow> rows, IReadOnlyList<string> headers, bool spreadsheetSafe)
    {
        var priority = headers.Contains(AdoExport.PriorityHeader);
        var description = headers.Contains(AdoExport.DescriptionHeader);
        var sb = new StringBuilder();
        sb.Append(string.Join(",", headers.Select(h => Quote(h)))).Append("\r\n");
        foreach (var r in rows)
        {
            var values = Values(r, priority, description);
            sb.Append(string.Join(",", values.Select((v, i) => Quote(spreadsheetSafe && i != 3 && Risky(v) ? "'" + v : v)))).Append("\r\n");
        }
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    // ── XLSX ────────────────────────────────────────────────────────────────────────────────────────────────

    public const string SheetName = "Test Cases";

    /// <summary>
    /// A minimal Office Open XML workbook with one worksheet: header row, then one row per step. Text cells are inline strings (stored as text,
    /// never interpreted as formulas — no &lt;f&gt; element is ever written), Test Step is a number. No merged cells, no blank rows, no formulas,
    /// no hidden values; the header row is frozen and columns have readable widths.
    /// </summary>
    public static byte[] Xlsx(IReadOnlyList<AdoExportRow> rows, IReadOnlyList<string> headers)
    {
        var priority = headers.Contains(AdoExport.PriorityHeader);
        var description = headers.Contains(AdoExport.DescriptionHeader);
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Part(zip, "[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/></Types>""");
            Part(zip, "_rels/.rels", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            Part(zip, "xl/workbook.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="{SheetName}" sheetId="1" r:id="rId1"/></sheets></workbook>""");
            Part(zip, "xl/_rels/workbook.xml.rels", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""");
            // Style 1 = bold (header row); style 2 = wrapped text (step cells). Nothing else.
            Part(zip, "xl/styles.xml", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs><cellXfs count="3"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0" applyAlignment="1"><alignment wrapText="1" vertical="top"/></xf></cellXfs></styleSheet>""");
            Part(zip, "xl/worksheets/sheet1.xml", Sheet(rows, headers, priority, description));
        }
        return stream.ToArray();
    }

    private static string Sheet(IReadOnlyList<AdoExportRow> rows, IReadOnlyList<string> headers, bool priority, bool description)
    {
        var widths = new[] { 10, 12, 50, 9, 60, 60, 30, 28, 12, 9 };
        var sb = new StringBuilder();
        sb.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");
        sb.Append("""<sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>""");
        sb.Append("<cols>");
        for (var i = 0; i < headers.Count; i++) sb.Append($"""<col min="{i + 1}" max="{i + 1}" width="{(headers[i] == AdoExport.DescriptionHeader ? 70 : widths[Math.Min(i, widths.Length - 1)])}" customWidth="1"/>""");
        sb.Append("</cols><sheetData>");
        sb.Append("""<row r="1">""");
        for (var i = 0; i < headers.Count; i++) sb.Append(TextCell(Column(i) + "1", headers[i], 1));
        sb.Append("</row>");
        for (var r = 0; r < rows.Count; r++)
        {
            var n = r + 2;
            var values = Values(rows[r], priority, description);
            sb.Append($"""<row r="{n}">""");
            for (var i = 0; i < values.Count; i++)
            {
                var cell = Column(i) + n;
                if (i == 3) sb.Append($"""<c r="{cell}"><v>{rows[r].TestStep}</v></c>""");
                else if (values[i].Length > 0) sb.Append(TextCell(cell, values[i], i is 4 or 5 ? 2 : 0));
            }
            sb.Append("</row>");
        }
        sb.Append("</sheetData></worksheet>");
        return sb.ToString();
    }

    private static string TextCell(string reference, string value, int style) =>
        $"""<c r="{reference}" t="inlineStr"{(style > 0 ? $" s=\"{style}\"" : "")}><is><t xml:space="preserve">{Xml(value)}</t></is></c>""";

    private static string Column(int index) => ((char)('A' + index)).ToString();

    /// <summary>XML-escaped text without characters XML 1.0 forbids.</summary>
    private static string Xml(string value)
    {
        var clean = new string(value.Where(XmlConvert.IsXmlChar).ToArray());
        return clean.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
    }

    private static void Part(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    // ── Mapping report (CSV) ────────────────────────────────────────────────────────────────────────────────

    /// <summary>What the import file cannot carry, per case: BirkNext identity and revision, requirement ids (BirkNext ids, not Azure DevOps ids),
    /// Test Data names, preconditions, and why a case is not in the file.</summary>
    public static byte[] MappingReport(AdoExportPreview preview, IReadOnlyDictionary<string, string> caseRevisions) =>
        Csv2([
            ["BirkNext case", "Title", "In import file", "Not exported because", "Azure DevOps id", "Plan revision", "Case revision", "Requirements (BirkNext ids)", "Test Data (names only)", "Preconditions",
             "Source specification", "Acceptance scenario", "Requirements with their specification", "Scenario", "Language"],
            .. preview.Cases.Select(c => (IReadOnlyList<string>)[c.CaseKey, c.Title,
                c.Included && c.Issues.All(i => i.Level != AdoIssueLevel.Blocking) ? "Yes" : "No",
                c.ExcludedReason ?? string.Join(" ", c.Issues.Where(i => i.Level == AdoIssueLevel.Blocking).Select(i => i.Message)),
                c.AzureDevOpsId ?? "", preview.PlanRevision.ToString(System.Globalization.CultureInfo.InvariantCulture), caseRevisions.GetValueOrDefault(c.CaseId) ?? "",
                string.Join("; ", c.Requirements), string.Join("; ", c.TestData), c.Preconditions ?? "",
                c.SourceSpecification ?? "", c.SourceScenario ?? "", string.Join("; ", c.RequirementSources), c.ScenarioTitle is { } st ? $"{st} ({c.ScenarioKey})" : "", c.Language]),
        ]);

    private static byte[] Csv2(IEnumerable<IReadOnlyList<string>> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines) sb.Append(string.Join(",", line.Select(v => Quote(Risky(v) ? "'" + v : v)))).Append("\r\n");
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }
}
