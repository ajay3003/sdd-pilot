using BirkNext.Schemas;
using BirkNext.Web.Components;
using BirkNext.Web.Pages;
using BirkNext.Web.Services.SchemaStudio;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Pages;

/// <summary>
/// Schema Studio grouped navigation, readable tables, progressive disclosure and profile editing, exercised with a generic order schema
/// (no Skolenærvær or other project knowledge) so the behavior is proven independent of one message.
/// </summary>
public sealed class SchemaStudioWorkflowTests : BunitContext
{
    private const string Order = """
        <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:example:order" xmlns="urn:example:order" elementFormDefault="qualified">
          <xs:element name="Order">
            <xs:complexType><xs:sequence>
              <xs:element name="OrderId" type="xs:string"><xs:annotation><xs:documentation>Unique order reference.</xs:documentation></xs:annotation></xs:element>
              <xs:element name="Note" type="xs:string" minOccurs="0"/>
              <xs:element name="Customer"><xs:complexType><xs:sequence>
                <xs:element name="CustomerName" type="xs:string"/>
                <xs:element name="Country"><xs:simpleType><xs:restriction base="xs:string"><xs:enumeration value="NO"/><xs:enumeration value="SE"/></xs:restriction></xs:simpleType></xs:element>
              </xs:sequence></xs:complexType></xs:element>
              <xs:element name="Quantity"><xs:simpleType><xs:restriction base="xs:integer"><xs:minInclusive value="1"/><xs:maxInclusive value="99"/></xs:restriction></xs:simpleType></xs:element>
            </xs:sequence></xs:complexType>
          </xs:element>
        </xs:schema>
        """;

    private SchemaStudioService ImportOrder(out SchemaPackage package)
    {
        var service = new SchemaStudioService();
        package = service.Import(new Dictionary<string, string> { ["order.xsd"] = Order }, "order.xsd");
        Services.AddSingleton(service);
        return service;
    }

    private static void OpenView(IRenderedComponent<SchemaStudio> page, string view)
    {
        page.FindAll("[data-testid=ss-group]").Single(b => b.TextContent == SchemaStudioNavigation.GroupOf(view).Name).Click();
        page.FindAll("[data-testid=ss-view]").Single(b => b.GetAttribute("data-view") == view).Click();
    }

    // ---- Navigation model ----

    [Fact]
    public void NavigationGroupsEveryViewExactlyOnceInTheTesterWorkflowOrder()
    {
        Assert.Equal(["Understand", "Schema", "Inspect", "Design & Compare", "Validate & Test"], SchemaStudioNavigation.Groups.Select(g => g.Name));
        Assert.Equal(15, SchemaStudioNavigation.AllViews.Count);
        Assert.Equal(SchemaStudioNavigation.AllViews.Count, SchemaStudioNavigation.AllViews.Distinct().Count());
        // Structure is reachable from Understand; the former flat views keep their place under named groups.
        Assert.Equal(["Overview", "Business structure", "Schema tree", "Effective structure"], SchemaStudioNavigation.Groups[0].Views.Select(SchemaStudioNavigation.Label));
        Assert.Equal(["Fields and rules", "Types", "Constraints", "Namespaces"], SchemaStudioNavigation.Groups[1].Views.Select(SchemaStudioNavigation.Label));
        Assert.Equal(["References", "Dependencies", "Raw XSD"], SchemaStudioNavigation.Groups[2].Views.Select(SchemaStudioNavigation.Label));
        Assert.Equal(["Designer", "Compare"], SchemaStudioNavigation.Groups[3].Views.Select(SchemaStudioNavigation.Label));
        Assert.Equal(["Generated tests", "Generation profile"], SchemaStudioNavigation.Groups[4].Views.Select(SchemaStudioNavigation.Label));
        Assert.Equal("Validate & Test", SchemaStudioNavigation.GroupOf("Generated Tests").Name);
        Assert.Equal("Inspect", SchemaStudioNavigation.GroupOf("References").Name);
        Assert.Equal("Understand", SchemaStudioNavigation.GroupOf("unknown").Name);
        Assert.All(SchemaStudioNavigation.AllViews, view => Assert.False(string.IsNullOrWhiteSpace(SchemaStudioNavigation.Purpose(view))));
        Assert.NotEqual(SchemaStudioNavigation.Purpose("Forretning"), SchemaStudioNavigation.Purpose("Schema Tree"));
    }

    [Theory]
    [InlineData("generated-tests", "Generated Tests")]
    [InlineData("felt-og-regler", "Felt og regler")]
    [InlineData("Schema dependencies", "Schema dependencies")]
    [InlineData("raw-xsd", "Raw XSD")]
    [InlineData("business-structure", "Forretning")]
    [InlineData("schema-tree", "Schema Tree")]
    [InlineData("effective-structure", "Effective Schema")]
    [InlineData("effective-schema", "Effective Schema")]
    [InlineData("fields-and-rules", "Felt og regler")]
    [InlineData("struktur", "Forretning")]
    [InlineData("forretning", "Forretning")]
    [InlineData("nonsense", null)]
    [InlineData("", null)]
    public void QueryValuesMapOnlyToKnownViews(string value, string? expected) =>
        Assert.Equal(expected, SchemaStudioNavigation.FromQuery(value));

    [Fact]
    public void EverySlugRoundTripsToItsView() =>
        Assert.All(SchemaStudioNavigation.AllViews, view => Assert.Equal(view, SchemaStudioNavigation.FromQuery(SchemaStudioNavigation.Slug(view))));

    [Fact]
    public void DeepLinkSelectsTheViewAndItsArea()
    {
        ImportOrder(out _);
        Services.GetRequiredService<NavigationManager>().NavigateTo("schema-studio?view=generated-tests");

        var page = Render<SchemaStudio>();

        Assert.Equal("true", page.FindAll("[data-testid=ss-group]").Single(b => b.TextContent == "Validate & Test").GetAttribute("aria-current"));
        Assert.Equal(["Generated tests", "Generation profile"], page.FindAll("[data-testid=ss-view]").Select(b => b.TextContent));
        Assert.NotNull(page.Find("[data-testid=ss-generated-tests]"));
    }

    [Fact]
    public void DeepLinkOpenedBeforeAnyPackageAppliesToTheFirstImportOnly()
    {
        var service = new SchemaStudioService();
        Services.AddSingleton(service);
        Services.GetRequiredService<NavigationManager>().NavigateTo("schema-studio?view=felt-og-regler");
        var page = Render<SchemaStudio>();

        page.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>().UploadFiles(InputFileContent.CreateFromText(Order, "order.xsd"));
        page.WaitForAssertion(() => Assert.Equal("Felt og regler", page.Find("[data-testid=ss-view][aria-current=page]").GetAttribute("data-view")));

        // With a package active, importing is a secondary action: open it first.
        page.Find("[data-testid=ss-import-another]").Click();
        page.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>().UploadFiles(InputFileContent.CreateFromText(Order, "order-copy.xsd"));
        page.WaitForAssertion(() => Assert.Equal("Overview", page.Find("[data-testid=ss-view][aria-current=page]").GetAttribute("data-view")));
    }

    [Fact]
    public void OnlyTheCurrentAreasViewsAreShownAndEveryViewIsReachable()
    {
        ImportOrder(out _);
        var page = Render<SchemaStudio>();

        Assert.Equal(5, page.FindAll("[data-testid=ss-group]").Count);
        Assert.True(page.FindAll("[data-testid=ss-view]").Count < SchemaStudioNavigation.AllViews.Count);
        foreach (var view in SchemaStudioNavigation.AllViews)
        {
            OpenView(page, view);
            Assert.Equal(SchemaStudioNavigation.GroupOf(view).Views, page.FindAll("[data-testid=ss-view]").Select(b => b.GetAttribute("data-view")));
            Assert.Equal(SchemaStudioNavigation.GroupOf(view).Views.Select(SchemaStudioNavigation.Label), page.FindAll("[data-testid=ss-view]").Select(b => b.TextContent));
            Assert.Equal(SchemaStudioNavigation.Purpose(view), page.Find("[data-testid=ss-view-purpose]").TextContent.Trim());
        }
    }

    [Fact]
    public void EveryView_RendersInTheSameStableShell_WithoutAViewSpecificWidth()
    {
        ImportOrder(out _);
        var page = Render<SchemaStudio>();
        var shell = page.Find("main").ClassName;

        foreach (var view in SchemaStudioNavigation.AllViews)
        {
            OpenView(page, view);
            Assert.Equal(shell, page.Find("main").ClassName);
            Assert.Single(page.FindAll("[data-testid=ss-workspace]"));
            Assert.Single(page.FindAll("[data-testid=ss-view-content]"));
        }
        // Wide content scrolls locally: every table sits in a labelled scroll region, never directly in the shell.
        OpenView(page, "Types");
        Assert.All(page.FindAll("table"), table => Assert.Contains("ss-table-wrap", table.ParentElement!.ClassName));
        OpenView(page, "Raw XSD");
        Assert.Contains("ss-code-scroll", page.Find("[data-testid=ss-raw]").ClassName);
    }

    [Fact]
    public void OverviewShowsShortFingerprintWithFullValueAndCapabilityNotice()
    {
        ImportOrder(out var package);
        var page = Render<SchemaStudio>();

        var fingerprint = page.Find("[data-testid=ss-fingerprint]");
        Assert.Equal(package.Fingerprint, fingerprint.GetAttribute("title"));
        Assert.True(fingerprint.TextContent.Trim().Length < package.Fingerprint.Length);
        Assert.Contains(package.Fingerprint, page.Find("[data-testid=ss-full-fingerprint]").TextContent);
        Assert.Contains("Order", page.Find("[data-testid=ss-root-element]").TextContent);
        Assert.Contains("business", page.Find("[data-testid=ss-capability]").TextContent, StringComparison.OrdinalIgnoreCase);

        page.Find("[data-testid=ss-shortcut-tests]").Click();
        Assert.Equal("page", page.FindAll("[data-testid=ss-view]").Single(b => b.GetAttribute("data-view") == "Generated Tests").GetAttribute("aria-current"));
    }

    [Fact]
    public void PackageSearchNamesTheAreaOfEachResult()
    {
        ImportOrder(out _);
        var page = Render<SchemaStudio>();

        page.Find("input[type=search]").Input("CustomerName");

        Assert.Contains("Schema → Fields and rules", page.Markup);
    }

    // ---- Readable tables ----

    [Fact]
    public void AnalysisTablesUseReadableColumnClassesInsteadOfCharacterWrapping()
    {
        ImportOrder(out _);
        var page = Render<SchemaStudio>();

        OpenView(page, "Generated Tests");
        var headers = page.Find("[data-testid=ss-generated-tests]").QuerySelectorAll("th").Select(th => th.ClassName).ToArray();
        Assert.All(headers, header => Assert.StartsWith("ss-col-", header));
        OpenView(page, "Types");
        Assert.NotNull(page.Find("[data-testid=ss-types]"));

        var css = File.ReadAllText(Path.Combine(RepoFrontend(), "BirkNext.Web", "Pages", "SchemaStudio.razor.css"));
        var readableCss = File.ReadAllText(Path.Combine(RepoFrontend(), "BirkNext.Web", "Components", "SchemaReadableView.razor.css"));
        AssertTableCellsNeverWrapAnywhere(css, ".ss-page");
        AssertTableCellsNeverWrapAnywhere(readableCss, ".sr-view");
    }

    /// <summary>
    /// "anywhere" makes every character a break point when a table measures its columns (columns collapsed to one character). No rule may
    /// apply it to cells, and cell content must be reset to break-word after the general text rules (p, li, small...) that use it.
    /// </summary>
    private static void AssertTableCellsNeverWrapAnywhere(string css, string scope)
    {
        var rules = System.Text.RegularExpressions.Regex.Matches(System.Text.RegularExpressions.Regex.Replace(css, @"/\*.*?\*/", "", System.Text.RegularExpressions.RegexOptions.Singleline), @"([^{}]+)\{([^{}]*)\}")
            .Select(m => (Selector: m.Groups[1].Value.Trim(), Body: m.Groups[2].Value)).ToList();
        // The one exception is the capped generated-value block: its column has a minimum width, so it cannot collapse.
        Assert.DoesNotContain(rules, r => r.Body.Contains("anywhere") && System.Text.RegularExpressions.Regex.IsMatch(r.Selector, @"\b(td|th|table)\b")
            && !(r.Selector.Contains("code.ss-value") && r.Body.Contains("max-width")));
        var reset = rules.FindLastIndex(r => r.Selector.Contains($"{scope} td *") && r.Body.Contains("break-word"));
        Assert.True(reset >= 0, "cell content is reset to break-word");
        Assert.True(reset > rules.FindLastIndex(r => r.Body.Contains("anywhere") && r.Selector.Contains($"{scope} li")), "the reset follows the general text rule");
    }

    private static string RepoFrontend()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "BirkNext.Web"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("frontend directory not found");
    }

    [Fact]
    public void GeneratedTestsSeparateExpectedValidatorAndResultAndBoundTheCandidateList()
    {
        ImportOrder(out var package);
        var page = Render<SchemaStudio>();
        OpenView(page, "Generated Tests");

        Assert.Contains($"Showing first {Math.Min(100, package.GeneratedTests.Count)} of {package.GeneratedTests.Count}", page.Find("[data-testid=ss-candidates-shown]").TextContent);
        Assert.True(page.Find("[data-testid=ss-generated-tests]").QuerySelectorAll("tbody tr").Length <= 100);
        Assert.Equal(package.GeneratedTests.Count.ToString(), page.Find("[data-testid=ss-test-count]").TextContent);
        Assert.Contains("do not test business logic", page.Find("[data-testid=ss-test-scope]").TextContent);

        page.Find("[data-testid=ss-run-tests]").Click();
        var results = page.Find("[data-testid=ss-test-results]");
        Assert.Equal(["Path", "Expected", "Derived case", "Validator", "Result", "Diagnostic"], results.QuerySelectorAll("th").Select(th => th.TextContent));
        var summary = page.Find("[data-testid=ss-test-summary]").QuerySelectorAll("dt").Select(dt => dt.TextContent);
        Assert.Equal(["Pass", "Fail", "Not evaluated"], summary);
        var invalidRow = results.QuerySelectorAll("tbody tr").First(tr => tr.Children[1].TextContent == "Invalid");
        Assert.Contains(invalidRow.Children[3].TextContent, new[] { "Accepted", "Rejected", "Not evaluated" });
        Assert.DoesNotContain(results.QuerySelectorAll("tbody tr"), tr => tr.Children[3].TextContent is "Valid" or "Invalid");
    }

    [Fact]
    public void LongPathsAndNamesGetBreakOpportunitiesWithoutChangingTheirText()
    {
        Assert.Equal("Root/<wbr>Group/<wbr>a.<wbr>b&lt;c&gt;", SchemaText.Breakable("Root/Group/a.b<c>").Value);
        Assert.Equal("", SchemaText.Breakable(null).Value);
        Assert.Equal("Root/", SchemaText.Breakable("Root/").Value);

        ImportOrder(out _);
        var page = Render<SchemaStudio>();
        OpenView(page, "Generated Tests");
        var firstPath = page.Find("[data-testid=ss-generated-tests] tbody td code");
        Assert.StartsWith("/Order", firstPath.TextContent);
        Assert.Contains("<wbr>", firstPath.InnerHtml);
    }

    // ---- Trees: expand/collapse and filters ----

    [Fact]
    public void EffectiveSchemaStartsAtDefaultDepthAndExpandsCollapsesAndFilters()
    {
        ImportOrder(out _);
        var page = Render<SchemaStudio>();
        OpenView(page, "Effective Schema");

        // Root fields are listed under the root heading; groups at the first level start open, deeper ones collapsed.
        Details(page, "Customer").Should(open: true);
        page.Find("[data-testid=ss-tree-collapse]").Click();
        Details(page, "Customer").Should(open: false);
        page.Find("[data-testid=ss-tree-expand]").Click();
        Details(page, "Customer").Should(open: true);
        page.Find("[data-testid=ss-tree-collapse]").Click();

        page.Find("[data-testid=ss-effective-filter]").Change(nameof(SchemaTreeFilterKind.WithConstraints));
        var tree = page.Find("[data-testid=ss-effective-tree]").TextContent;
        Assert.Contains("Country", tree);
        Assert.Contains("Quantity", tree);
        Assert.DoesNotContain("Note", tree);
        Assert.DoesNotContain("CustomerName", tree);
        Details(page, "Customer").Should(open: true); // a match is shown with its path

        page.Find("[data-testid=ss-effective-filter]").Change(nameof(SchemaTreeFilterKind.All));
        page.Find("[data-testid=ss-effective-search]").Input("customername");
        Assert.Contains("CustomerName", page.Find("[data-testid=ss-effective-tree]").TextContent);
        Assert.DoesNotContain("Quantity", page.Find("[data-testid=ss-effective-tree]").TextContent);
    }

    [Fact]
    public void BusinessViewExpandsCollapsesAndShowsOnlyMandatoryFields()
    {
        ImportOrder(out var package);
        var view = Render<SchemaReadableView>(p => p.Add(x => x.Package, package).Add(x => x.View, "Forretning"));

        // The root's own fields are visible by default (a top-level group is open; deeper groups start collapsed).
        bool CustomerOpen() => view.FindAll("details.sr-node").Single(d => d.QuerySelector(".sr-name")!.TextContent == "Customer").HasAttribute("open");
        Assert.True(CustomerOpen());
        view.Find("[data-testid=sr-collapse-all]").Click();
        Assert.All(view.FindAll("details.sr-node"), d => Assert.False(d.HasAttribute("open")));
        view.Find("[data-testid=sr-expand-all]").Click();
        Assert.True(CustomerOpen());

        view.Find("[data-testid=sr-tree-filter]").Change(nameof(SchemaTreeFilterKind.Mandatory));
        var business = view.Find("[data-testid=sr-business]").TextContent;
        Assert.Contains("OrderId", business);
        Assert.DoesNotContain("Note", business);

        view.Find("[data-testid=sr-tree-filter]").Change(nameof(SchemaTreeFilterKind.WithDocumentation));
        Assert.Contains("OrderId", view.Find("[data-testid=sr-business]").TextContent);
        Assert.DoesNotContain("Quantity", view.Find("[data-testid=sr-business]").TextContent);
    }

    [Fact]
    public void TreeFilterKeepsMatchesWithTheirAncestorsAndTreatsChoiceMembersAsNotMandatory()
    {
        static EffectiveMember Leaf(string name, string min = "1", string? compositor = null, IReadOnlyList<SchemaFacet>? facets = null) =>
            new(name, "xs:string", min, "1", "a.xsd", null, null, compositor, facets ?? [], [], "urn:x", null);
        var root = new EffectiveMember("Root", null, "1", "1", "a.xsd", null, null, null, [], [
            new EffectiveMember("Group", null, "0", "1", "a.xsd", null, null, null, [], [Leaf("Deep", facets: [new SchemaFacet("pattern", "[0-9]+")])], "urn:x", null),
            Leaf("Pick", compositor: "choice"),
            Leaf("Plain")], "urn:x", null);

        Assert.True(SchemaTreeFilter.Keep(root, SchemaTreeFilterKind.WithConstraints, null));
        Assert.True(SchemaTreeFilter.Keep(root.Children[0], SchemaTreeFilterKind.WithConstraints, null));
        Assert.False(SchemaTreeFilter.Keep(root.Children[2], SchemaTreeFilterKind.WithConstraints, null));
        Assert.False(SchemaTreeFilter.IsMandatory(root.Children[1]));
        Assert.False(SchemaTreeFilter.IsOptional(root.Children[1]));
        Assert.True(SchemaTreeFilter.Keep(root, SchemaTreeFilterKind.All, "deep"));
        Assert.False(SchemaTreeFilter.Keep(root.Children[2], SchemaTreeFilterKind.All, "deep"));

        Assert.True(SchemaTreeFilter.IsOpen(0, null, filtering: false));
        Assert.False(SchemaTreeFilter.IsOpen(1, null, filtering: false));
        Assert.True(SchemaTreeFilter.IsOpen(5, null, filtering: true));
        Assert.False(SchemaTreeFilter.IsOpen(0, false, filtering: true));
        Assert.Equal("Numeric bounds", SchemaTreeFilter.ConstraintCategory("maxInclusive"));
        Assert.Equal("Length", SchemaTreeFilter.ConstraintCategory("minLength"));
    }

    // ---- Schema views ----

    [Fact]
    public void ConstraintsFilterByCategoryAndSingleFileEmptyStatesAreExplicit()
    {
        ImportOrder(out _);
        var page = Render<SchemaStudio>();

        OpenView(page, "Constraints");
        // Summary first: the total and the per-category counts come before the (collapsed) table.
        var all = page.Find("[data-testid=ss-constraints]").QuerySelectorAll("tbody tr").Length;
        Assert.Equal($"{all} rules detected", page.Find("[data-testid=ss-constraint-total]").TextContent);
        Assert.Equal(all, page.FindAll("[data-testid=ss-rule-category] dd").Sum(dd => int.Parse(dd.TextContent)));
        Assert.Equal("false", page.Find("[data-testid=ss-constraint-list-toggle]").GetAttribute("aria-expanded"));
        Assert.True(page.Markup.IndexOf("ss-constraint-summary", StringComparison.Ordinal) < page.Markup.IndexOf("data-testid=\"ss-constraints\"", StringComparison.Ordinal));
        page.Find("[data-testid=ss-constraint-filter]").Change("Enumeration");
        var enumerations = page.Find("[data-testid=ss-constraints]").QuerySelectorAll("tbody tr");
        Assert.InRange(enumerations.Length, 1, all - 1);
        // Plain-language rule name first; the exact XSD facet stays available.
        Assert.All(enumerations, tr => { Assert.StartsWith("Allowed value", tr.Children[1].TextContent); Assert.Contains("enumeration", tr.Children[1].TextContent); });
        Assert.All(enumerations, tr => Assert.DoesNotContain("/", tr.Children[3].TextContent));

        OpenView(page, "References");
        Assert.Contains("one XSD file, so there are no references between schema files", page.Find("[data-testid=ss-references-empty]").TextContent);
        OpenView(page, "Namespaces");
        Assert.Equal("None in this package", page.Find("[data-testid=ss-imported-by]").TextContent);
    }

    [Fact]
    public void RawXsdIsNumberedAndSearchable()
    {
        ImportOrder(out _);
        var page = Render<SchemaStudio>();
        OpenView(page, "Raw XSD");

        Assert.Equal(Order.Split('\n').Length, page.Find("[data-testid=ss-raw]").QuerySelectorAll("li").Length);
        page.Find("[data-testid=ss-raw-search]").Input("Quantity");
        Assert.Contains("1", page.Find("[data-testid=ss-raw-hits]").TextContent);
        Assert.NotNull(page.Find("[data-testid=ss-raw-copy]"));
    }

    [Fact]
    public void CompareWithoutAnotherVersionOffersImportInsteadOfABlankView()
    {
        ImportOrder(out _);
        var page = Render<SchemaStudio>();
        OpenView(page, "Compare");

        var empty = page.Find("[data-testid=ss-compare-empty]");
        Assert.Contains("Import second version", empty.TextContent);
    }

    [Fact]
    public void DesignerGroupsItsFormAndStatesThatImportedSourceIsNeverChanged()
    {
        ImportOrder(out _);
        var page = Render<SchemaStudio>();
        OpenView(page, "Designer");

        Assert.Equal(["Field", "Occurrence", "Rules"], page.Find("[data-testid=ss-designer-form]").QuerySelectorAll("legend").Select(l => l.TextContent));
        Assert.Contains("never changed", page.Find("[data-testid=ss-source-safety]").TextContent);
        Assert.Equal("The imported source is never changed.", page.Find("[data-testid=ss-designer-immutable]").TextContent);
        // Plain-language occurrence first; the exact XML attributes stay as technical detail.
        var form = page.Find("[data-testid=ss-designer-form]").TextContent;
        Assert.Contains("Required?", form); Assert.Contains("Repeat", form); Assert.Contains("minOccurs / maxOccurs", form);
        Assert.Equal(["Once", "Up to 2 times", "Unlimited"], page.Find("[data-testid=ss-designer-max]").QuerySelectorAll("option").Select(o => o.TextContent));
        // The draft is secondary: collapsed until requested.
        Assert.Equal("false", page.Find("[data-testid=ss-draft-editor-toggle]").GetAttribute("aria-expanded"));
    }

    // ---- Generation Profile ----

    [Fact]
    public void GenerationProfileIsSplitIntoProfileRuleEditorAndGenerateSections()
    {
        ImportOrder(out _);
        Services.AddSingleton(new SchemaGenerationProfilesService());
        var page = Render<SchemaStudio>();
        OpenView(page, "Generation Profile");

        Assert.Equal("Profile", page.Find("[data-testid=ss-profile-section] > legend").TextContent);
        Assert.Equal("Field rules", page.Find("[data-testid=ss-rule-section] > legend").TextContent);
        Assert.Equal("Generate XML", page.Find("[data-testid=ss-generate-section] > legend").TextContent);
        Assert.Equal("New profile random seed", page.Find("[data-testid=ss-seed-label]").FirstChild!.TextContent.Trim());
    }

    [Fact]
    public void UpdatingASavedProfileWithoutLoadingItCannotOverwriteItsSeedWithNewProfileValues()
    {
        ImportOrder(out var package);
        var profiles = new SchemaGenerationProfilesService();
        var saved = profiles.Create("Regression profile", package.Fingerprint, null, SchemaGenerationProfileKind.Boundary, 4101);
        Services.AddSingleton(profiles);
        var page = Render<SchemaStudio>();
        OpenView(page, "Generation Profile");

        page.Find("select[aria-label='Generation Profile']").Change(saved.Id.ToString("D"));
        Assert.Contains("seed 4101", page.Find("[data-testid=ss-selected-profile]").TextContent);
        // The form holds new-profile values (seed 1001); there is no Update action until the selected profile is loaded.
        Assert.Equal("1001", page.Find("[data-testid=ss-profile-seed]").GetAttribute("value"));
        Assert.DoesNotContain(page.FindAll("button"), b => b.TextContent.Contains("Update selected profile", StringComparison.Ordinal));

        page.FindAll("button").Single(b => b.TextContent.Contains("Load selected profile for editing", StringComparison.Ordinal)).Click();
        Assert.Equal("4101", page.Find("[data-testid=ss-profile-seed]").GetAttribute("value"));
        Assert.Equal("Random seed of the selected profile", page.Find("[data-testid=ss-seed-label]").FirstChild!.TextContent.Trim());
        page.FindAll("button").Single(b => b.TextContent.Contains("Cancel editing", StringComparison.Ordinal)).Click();
        Assert.Equal(4101, Assert.Single(profiles.Profiles).Seed);

        // Switching the selection drops the loaded state, so values loaded from one profile are never written into another.
        page.FindAll("button").Single(b => b.TextContent.Contains("Load selected profile for editing", StringComparison.Ordinal)).Click();
        page.Find("select[aria-label='Generation Profile']").Change(SchemaGenerationProfilesService.BuiltIns(package.Fingerprint).First().Id.ToString("D"));
        Assert.DoesNotContain(page.FindAll("button"), b => b.TextContent.Contains("Update selected profile", StringComparison.Ordinal));
        Assert.Equal(4101, Assert.Single(profiles.Profiles).Seed);
    }

    // ---- Graph ----

    [Fact]
    public void GraphDrilldownShowsOneFilesDeclarationsAndResetReturnsToFiles()
    {
        const string common = """<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:c" xmlns="urn:c"><xs:simpleType name="Code"><xs:restriction base="xs:string"/></xs:simpleType></xs:schema>""";
        const string main = """<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:c="urn:c" targetNamespace="urn:m"><xs:import namespace="urn:c" schemaLocation="common.xsd"/><xs:element name="Doc" type="c:Code"/></xs:schema>""";
        var package = new SchemaStudioService().Import(new Dictionary<string, string> { ["main.xsd"] = main, ["common.xsd"] = common }, "linked");
        var cut = Render<SchemaRelationshipGraph>(p => p.Add(x => x.Package, package));

        Assert.DoesNotContain(">Code<", cut.Markup);
        cut.FindAll("g.ss-graph-node").First(n => n.TextContent.Contains("common.xsd")).KeyDown("Enter");
        cut.Find("[data-testid=ss-graph-drilldown]").Click();
        Assert.Contains("Showing declarations of", cut.Markup);
        Assert.Contains("Code", cut.Find("[data-testid=ss-graph-svg]").TextContent);

        cut.Find("[data-testid=ss-graph-fit]").Click();
        Assert.Contains("width:100%", cut.Find("[data-testid=ss-graph-svg]").GetAttribute("style"));
        cut.Find("[data-testid=ss-graph-reset]").Click();
        Assert.DoesNotContain("Showing declarations of", cut.Markup);
        Assert.DoesNotContain("width:100%", cut.Find("[data-testid=ss-graph-svg]").GetAttribute("style"));
        Assert.Equal("files", cut.Find("[data-testid=ss-graph-level]").GetAttribute("value"));
    }

    private static DetailsProbe Details(IRenderedComponent<SchemaStudio> page, string name) =>
        new(page.Find("[data-testid=ss-effective-tree]").QuerySelectorAll("details").Single(d => d.QuerySelector("summary strong")!.TextContent == name));

    private readonly record struct DetailsProbe(AngleSharp.Dom.IElement Element)
    {
        public void Should(bool open) => Assert.Equal(open, Element.HasAttribute("open"));
    }
}
