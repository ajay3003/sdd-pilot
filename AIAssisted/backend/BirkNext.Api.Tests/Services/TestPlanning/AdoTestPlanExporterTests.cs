using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using BirkNext.Api.Services.TestPlanning;
using BirkNext.TestPlanning;
using FluentAssertions;
using Microsoft.VisualBasic.FileIO;
using Xunit;

namespace BirkNext.Api.Tests.Services.TestPlanning;

/// <summary>
/// The Azure DevOps Test Plans export rule and both file formats, checked against the Microsoft Learn layout (one row per step, case fields
/// repeated, blank ID for new cases, the nine documented columns) and read back with independent readers: CSV with
/// <see cref="TextFieldParser"/>, XLSX as an OOXML ZIP package with its XML parts (no BirkNext code reads them back).
/// </summary>
public sealed class AdoTestPlanExporterTests
{
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static TestCaseDraft Case(string id, string title, string[] steps, string?[] expected, TestCaseReviewState review = TestCaseReviewState.Accepted,
        string? preconditions = null, string? priority = null, string? adoId = null) => new()
    {
        Source = new ScenarioReference { Kind = ScenarioSourceKind.Manual, Owner = "library", ScenarioId = id }, Review = review,
        Generated = new TestCaseContent { Title = title, Steps = [.. steps], StepExpected = [.. expected], Preconditions = preconditions },
        Edits = new TestCaseEdits { Priority = priority }, VerifiedAzureDevOpsTestCaseId = adoId,
    };

    private static TestPlanDraft Plan(params TestCaseDraft[] cases) => new()
    {
        Id = Guid.Parse("66666666-6666-6666-6666-666666666666"), GuideKey = "project:library", ProjectKey = "library", PlanName = "Utlån – testplan", Revision = 7, Cases = [.. cases],
    };

    private static AdoExportRequest Request(params string[] caseIds) => new()
    {
        ProjectKey = "library", DraftId = Guid.Parse("66666666-6666-6666-6666-666666666666"), PlanRevision = 7, CaseIds = [.. caseIds],
        Profile = new AdoExportProfile { AreaPath = "Library\\Loans", AssignedTo = "tester@contoso.com", State = "Design" },
    };

    private static readonly TestCaseDraft Borrow = Case("c1", "Låne en bok", ["Åpne utlånssiden", "Registrer lånet, \"raskt\", med kort"], ["Siden vises", "Lånet er registrert\nog vises i listen"]);
    private static readonly TestCaseDraft Return = Case("c2", "Return a book", ["Return the book"], ["The book is available"]);

    private static List<string[]> ReadCsv(byte[] bytes)
    {
        bytes.Take(3).Should().Equal(new byte[] { 0xEF, 0xBB, 0xBF }, "UTF-8 with BOM, so spreadsheet programs read æ ø å");
        using var parser = new TextFieldParser(new MemoryStream(bytes), Encoding.UTF8) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        parser.SetDelimiters(",");
        var rows = new List<string[]>();
        while (!parser.EndOfData) rows.Add(parser.ReadFields()!);
        return rows;
    }

    /// <summary>Reads the worksheet straight from the ZIP package: (cell reference → (type, text, has formula)).</summary>
    private static (List<Dictionary<string, (string? Type, string Text, bool Formula)>> Rows, ZipArchive Zip) ReadXlsx(byte[] bytes)
    {
        var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        zip.GetEntry("[Content_Types].xml").Should().NotBeNull();
        var workbook = XDocument.Load(zip.GetEntry("xl/workbook.xml")!.Open());
        workbook.Descendants(S + "sheet").Should().ContainSingle().Which.Attribute("name")!.Value.Should().Be("Test Cases");
        var sheet = XDocument.Load(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        sheet.Descendants(S + "mergeCell").Should().BeEmpty("no merged cells in import data");
        var rows = sheet.Descendants(S + "row").Select(r => r.Elements(S + "c").ToDictionary(
            c => new string(c.Attribute("r")!.Value.TakeWhile(char.IsLetter).ToArray()),
            c => (c.Attribute("t")?.Value, c.Descendants(S + "t").FirstOrDefault()?.Value ?? c.Element(S + "v")?.Value ?? "", c.Element(S + "f") is not null))).ToList();
        return (rows, zip);
    }

    [Fact]
    public void Each_step_is_one_row_with_the_case_fields_repeated_a_blank_id_and_the_documented_columns()
    {
        var preview = AdoTestPlanExporter.Preview(Plan(Borrow, Return), Request());

        preview.CanDownload.Should().BeTrue();
        preview.Headers.Should().Equal("ID", "Work Item Type", "Title", "Test Step", "Step Action", "Step Expected", "Area Path", "Assigned To", "State", "Description");
        AdoTestPlanExporter.Preview(Plan(Borrow), Request() with { Profile = Request().Profile with { IncludeSourceReferences = false } }).Headers
            .Should().Equal(["ID", "Work Item Type", "Title", "Test Step", "Step Action", "Step Expected", "Area Path", "Assigned To", "State"], "without source references the documented columns only");
        preview.Rows.Select(r => (r.Id, r.WorkItemType, r.Title, r.TestStep, r.StepAction)).Should().Equal(
            ("", "Test Case", "Låne en bok", 1, "Åpne utlånssiden"), ("", "Test Case", "Låne en bok", 2, "Registrer lånet, \"raskt\", med kort"), ("", "Test Case", "Return a book", 1, "Return the book"));
        preview.Rows.Should().OnlyContain(r => r.AreaPath == "Library\\Loans" && r.AssignedTo == "tester@contoso.com" && r.State == "Design");
        preview.ReadyCases.Should().Be(2);
    }

    [Fact]
    public void Csv_and_xlsx_carry_the_same_rows_read_back_by_independent_readers_with_norwegian_text_quotes_commas_and_line_breaks()
    {
        var preview = AdoTestPlanExporter.Preview(Plan(Borrow, Return), Request());

        var csv = ReadCsv(AdoTestPlanExporter.Csv(preview.Rows, preview.Headers, spreadsheetSafe: true));
        var (xlsx, zip) = ReadXlsx(AdoTestPlanExporter.Xlsx(preview.Rows, preview.Headers));
        using (zip)
        {
            csv.Should().HaveCount(4);
            xlsx.Should().HaveCount(4, "a header row and one row per step, no title rows, no blank rows");
            csv[0].Should().Equal(preview.Headers);
            xlsx[0].Values.Select(v => v.Text).Should().Equal(preview.Headers);
            for (var i = 1; i < 4; i++)
            {
                var r = preview.Rows[i - 1];
                var expected = new[] { r.Id, r.WorkItemType, r.Title, r.TestStep.ToString(), r.StepAction, r.StepExpected, r.AreaPath, r.AssignedTo, r.State, r.Description! };
                csv[i].Should().Equal(expected, $"CSV row {i}");
                string Cell(string col) => xlsx[i].TryGetValue(col, out var v) ? v.Text : "";
                new[] { Cell("A"), Cell("B"), Cell("C"), Cell("D"), Cell("E"), Cell("F"), Cell("G"), Cell("H"), Cell("I"), Cell("J") }.Should().Equal(expected, $"XLSX row {i}");
                r.Description.Should().Contain("BirkNext test case:", "every row carries where its test case came from");
                xlsx[i]["D"].Type.Should().BeNull("Test Step is a number cell");
            }
            csv[2][5].Should().Be("Lånet er registrert\nog vises i listen", "the line break survives inside the quoted CSV field");
            csv[2][4].Should().Be("Registrer lånet, \"raskt\", med kort");
            xlsx.SelectMany(r => r.Values).Should().OnlyContain(c => !c.Formula, "no formula is ever written");
        }
    }

    [Fact]
    public void A_formula_like_cell_is_never_a_formula_in_xlsx_and_is_neutralised_in_csv_only_when_chosen()
    {
        var risky = Case("c3", "=HYPERLINK(\"http://x\")", ["+cmd|' /C calc'!A0", "-2 then @SUM(A1)"], ["@result", "=1+1"]);
        var preview = AdoTestPlanExporter.Preview(Plan(risky), Request() with { Profile = Request().Profile with { Format = AdoExportFormat.Csv } });

        var (xlsx, zip) = ReadXlsx(AdoTestPlanExporter.Xlsx(preview.Rows, preview.Headers));
        using (zip)
        {
            xlsx[1]["C"].Should().Be(("inlineStr", "=HYPERLINK(\"http://x\")", false), "text stays literal and is stored as a string, not a formula");
            xlsx.SelectMany(r => r.Values).Should().OnlyContain(c => !c.Formula);
        }
        var safe = ReadCsv(AdoTestPlanExporter.Csv(preview.Rows, preview.Headers, spreadsheetSafe: true));
        safe[1][2].Should().Be("'=HYPERLINK(\"http://x\")");
        safe[1][4].Should().Be("'+cmd|' /C calc'!A0");
        safe[1][3].Should().Be("1", "the step number is never prefixed");
        var literal = ReadCsv(AdoTestPlanExporter.Csv(preview.Rows, preview.Headers, spreadsheetSafe: false));
        literal[1][2].Should().Be("=HYPERLINK(\"http://x\")", "without the option the text is exactly what the tester wrote");
        preview.Issues.Should().Contain(i => i.Level == AdoIssueLevel.Warning && i.Message.Contains("leading apostrophe"));
    }

    [Fact]
    public void Missing_title_action_or_expected_result_and_an_overlong_title_block_the_case_and_nothing_is_filled_in()
    {
        var noExpected = Case("a", "Needs answers", ["Do it", "Check it"], ["Done", null]);
        var noAction = Case("b", "No action", [" "], ["x"]);
        var noTitle = Case("c", "  ", ["Step"], ["Ok"]);
        var noSteps = Case("d", "Nothing to do", [], []);
        var tooLong = Case("e", new string('x', 256), ["Step"], ["Ok"]);
        var preview = AdoTestPlanExporter.Preview(Plan(noExpected, noAction, noTitle, noSteps, tooLong, Return), Request());

        preview.CanDownload.Should().BeFalse();
        preview.CasesNeedingAttention.Should().Be(5);
        preview.ReadyCases.Should().Be(1);
        preview.Issues.Should().Contain(i => i.CaseId == "a" && i.Message == "1 step needs an expected result.");
        preview.Issues.Should().Contain(i => i.CaseId == "b" && i.Message.Contains("no action"));
        preview.Issues.Should().Contain(i => i.CaseId == "c" && i.Message.Contains("no title"));
        preview.Issues.Should().Contain(i => i.CaseId == "d" && i.Message.Contains("no steps"));
        preview.Issues.Should().Contain(i => i.CaseId == "e" && i.Message.Contains("255"));
        preview.Rows.Should().OnlyContain(r => r.Title == "Return a book", "a blocked case is never written with invented content");
    }

    [Fact]
    public void Selection_approval_and_excluded_cases_are_explained_never_silently_dropped()
    {
        var prepared = Case("p", "Prepared only", ["Step"], ["Ok"], TestCaseReviewState.UnderReview);
        var excluded = Case("x", "Out of scope", ["Step"], ["Ok"], TestCaseReviewState.Excluded);
        var plan = Plan(Borrow, prepared, excluded, Return);

        var defaults = AdoTestPlanExporter.Preview(plan, Request());
        defaults.Cases.Single(c => c.CaseId == "p").ExcludedReason.Should().StartWith("Not approved yet");
        defaults.Cases.Single(c => c.CaseId == "x").ExcludedReason.Should().Contain("never exported");
        defaults.ReadyCases.Should().Be(2);

        var withDrafts = AdoTestPlanExporter.Preview(plan, Request() with { IncludeNotApproved = true });
        withDrafts.ReadyCases.Should().Be(3);
        withDrafts.Cases.Single(c => c.CaseId == "x").Included.Should().BeFalse("excluded cases are never exported, even when drafts are included");

        var selected = AdoTestPlanExporter.Preview(plan, Request("c2"));
        selected.Rows.Should().OnlyContain(r => r.Title == "Return a book");
        selected.Cases.Single(c => c.CaseId == "c1").ExcludedReason.Should().Be("Not selected");

        var none = AdoTestPlanExporter.Preview(plan, Request("nothing-like-this"));
        none.CanDownload.Should().BeFalse();
        none.Issues.Should().Contain(i => i.Level == AdoIssueLevel.Blocking && i.Message.StartsWith("No test case is ready"));
    }

    [Fact]
    public void Update_mode_uses_only_confirmed_azure_devops_ids_rejects_invalid_or_duplicate_ids_and_warns_that_steps_are_replaced()
    {
        var plan = Plan(Borrow, Return, Case("c3", "Known in ADO", ["Step"], ["Ok"], adoId: "9876"));
        var request = Request() with { Mode = AdoExportMode.UpdateExisting, ExistingIds = new() { ["c1"] = "1234", ["c2"] = "12a" } };

        var unconfirmed = AdoTestPlanExporter.Preview(plan, request);
        unconfirmed.CanDownload.Should().BeFalse();
        unconfirmed.Issues.Should().Contain(i => i.Level == AdoIssueLevel.Blocking && i.Message.StartsWith("Confirm"));
        unconfirmed.Issues.Should().Contain(i => i.Level == AdoIssueLevel.Warning && i.Message.Contains("replaces ALL existing steps"));
        unconfirmed.Issues.Should().Contain(i => i.CaseId == "c2" && i.Message.Contains("not an Azure DevOps work item id"));

        var duplicate = AdoTestPlanExporter.Preview(plan, request with { ExistingIdsConfirmed = true, ExistingIds = new() { ["c1"] = "1234", ["c2"] = "1234" } });
        duplicate.Issues.Should().Contain(i => i.CaseId == "c2" && i.Message.Contains("also given for"));

        var ok = AdoTestPlanExporter.Preview(plan, request with { ExistingIdsConfirmed = true, ExistingIds = new() { ["c1"] = "1234", ["c2"] = "5678" } });
        ok.CanDownload.Should().BeTrue();
        ok.Rows.Select(r => r.Id).Distinct().Should().BeEquivalentTo(["1234", "5678", "9876"], "a stored verified id is used; nothing else");
        AdoTestPlanExporter.Preview(plan, Request()).Rows.Should().OnlyContain(r => r.Id == "", "create mode never writes an id — not a BirkNext case, plan or requirement id");
    }

    [Fact]
    public void Destination_fields_are_checked_for_form_only_and_azure_devops_server_does_not_require_an_area_path()
    {
        AdoExportPreview With(AdoExportProfile p) => AdoTestPlanExporter.Preview(Plan(Return), Request() with { Profile = p });

        var missing = With(new AdoExportProfile { State = "Design" });
        missing.Fields.Single(f => f.Field == "Area Path").Check.Should().Be(AdoFieldCheck.Missing);
        missing.Issues.Should().Contain(i => i.Message.StartsWith("Azure DevOps Area Path is missing"));
        missing.CanDownload.Should().BeFalse();

        foreach (var bad in new[] { "\\Library", "Library\\", "Library\\\\Loans", "Library/Loans", "Library\\Lo:ans" })
            With(new AdoExportProfile { AreaPath = bad }).Fields.Single(f => f.Field == "Area Path").Check.Should().Be(AdoFieldCheck.Invalid, bad);

        var good = With(new AdoExportProfile { AreaPath = "Library\\Loans", State = "Ready" });
        good.Fields.Single(f => f.Field == "Area Path").Check.Should().Be(AdoFieldCheck.NeedsConfirmationInAzureDevOps, "BirkNext cannot prove the path exists offline");
        good.Fields.Single(f => f.Field == "Assigned To").Check.Should().Be(AdoFieldCheck.Missing);
        good.Issues.Should().Contain(i => i.Message.Contains("\"Ready\" needs confirmation for your Azure DevOps process"));
        good.CanDownload.Should().BeTrue("an empty Assigned To creates unassigned test cases");

        var server = With(new AdoExportProfile { Target = AdoTarget.Server });
        server.Fields.Single(f => f.Field == "Area Path").Check.Should().Be(AdoFieldCheck.NotRequired);
        server.CanDownload.Should().BeTrue();
        With(new AdoExportProfile { AreaPath = "Library", State = " " }).CanDownload.Should().BeFalse("a state is required");
    }

    [Fact]
    public void Titles_lose_markdown_and_a_repeated_requirement_id_and_duplicate_titles_are_warned()
    {
        AdoTestPlanExporter.CleanTitle("FR-001: - **FR-001**: The system MUST register a loan").Should().Be("FR-001: The system MUST register a loan");
        AdoTestPlanExporter.CleanTitle("- **Borrow** a `book`  now").Should().Be("Borrow a book now");
        AdoTestPlanExporter.CleanTitle("Rapport  for\nbarn").Should().Be("Rapport for barn");

        var preview = AdoTestPlanExporter.Preview(Plan(Return, Case("c9", "Return  a book", ["Step"], ["Ok"])), Request());
        preview.Issues.Should().Contain(i => i.Level == AdoIssueLevel.Warning && i.Message.Contains("share the title \"Return a book\""));
    }

    [Fact]
    public void Preconditions_priority_requirements_and_test_data_are_handled_transparently()
    {
        var c = Case("c1", "Låne", ["Lån boken"], ["Lånt"], preconditions: "Medlemmet er innlogget", priority: "High") with
        {
            RequirementLinks = [new ManualRequirementLink { RequirementId = "FR-001", SpecificationPath = "specs/spec.md" }],
            TestData = [new TestCaseDataLink { AssetId = "asset-1", Alias = "member-card" }],
        };
        var odd = Case("c2", "Odd priority", ["Step"], ["Ok"], priority: "Urgent-ish");

        var report = AdoTestPlanExporter.Preview(Plan(c, odd), Request());
        report.Rows.Should().HaveCount(2, "preconditions are not invented as a step by default");
        report.Issues.Should().Contain(i => i.Message.StartsWith("Preconditions are not an import column"));
        report.Issues.Should().Contain(i => i.Message.Contains("does not create Azure DevOps requirement links"));
        report.Issues.Should().Contain(i => i.Message.Contains("no Test Data values are exported"));
        report.Headers.Should().NotContain("Priority");

        var first = AdoTestPlanExporter.Preview(Plan(c, odd), Request() with { Profile = Request().Profile with { Preconditions = AdoPreconditionsMode.FirstStep, IncludePriority = true } });
        first.Rows.Where(r => r.Title == "Låne").Select(r => (r.TestStep, r.StepAction, r.StepExpected, r.Priority)).Should().Equal(
            (1, "Check the preconditions: Medlemmet er innlogget", "The preconditions are met", "2"), (2, "Lån boken", "Lånt", "2"));
        first.Headers.Should().ContainInOrder("State", "Priority", "Description");
        first.Issues.Should().Contain(i => i.CaseId == "c2" && i.Level == AdoIssueLevel.Warning && i.Message.Contains("\"Urgent-ish\" has no Azure DevOps value"));
        first.Rows.Single(r => r.Title == "Odd priority").Priority.Should().Be("");

        var mapping = ReadCsv(AdoTestPlanExporter.MappingReport(report, new Dictionary<string, string> { ["c1"] = "abc123" }));
        mapping[1].Should().Contain("FR-001").And.Contain("member-card (asset-1)").And.Contain("Medlemmet er innlogget").And.Contain("abc123").And.Contain("7");
    }

    [Fact]
    public void The_plan_is_never_changed_by_building_an_export()
    {
        var plan = Plan(Borrow, Return);
        var before = System.Text.Json.JsonSerializer.Serialize(plan, TestPlanSuggestionEngine.Json);
        var preview = AdoTestPlanExporter.Preview(plan, Request() with { Profile = Request().Profile with { Preconditions = AdoPreconditionsMode.FirstStep } });
        AdoTestPlanExporter.Xlsx(preview.Rows, preview.Headers);
        AdoTestPlanExporter.Csv(preview.Rows, preview.Headers, true);
        System.Text.Json.JsonSerializer.Serialize(plan, TestPlanSuggestionEngine.Json).Should().Be(before);
    }
}
