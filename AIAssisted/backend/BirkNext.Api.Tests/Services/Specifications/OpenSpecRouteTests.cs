using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.Specifications;
using BirkNext.Api.Services.Specifications.OpenSpec;
using BirkNext.Specifications;
using BirkNext.TestPlanning;
using FluentAssertions;
using Xunit;
using D = BirkNext.Api.Services.TestPlanning.TestScenarioDiscoveryEngine;

namespace BirkNext.Api.Tests.Services.Specifications;

/// <summary>OpenSpec isolated route (2026-11-27): parsing (O), lifecycle (L), data quality (D) and performance/security (P) on deterministic fixtures.</summary>
public sealed class OpenSpecRouteTests
{
    private const string Project = "import:import-openspec";

    private static OpenSpecRequirement Req(OpenSpecSourceModel m, string capability, string name) =>
        m.Capabilities.Single(c => c.Key == capability).Requirements.Single(r => r.Name == name);

    private static List<DiscoveredScenario> Candidates(OpenSpecModelBuilder.Built b) =>
        D.Finalize(Project, "s", b.Evidence.SelectMany(e => D.FromSpecification(Project, e.Evidence with { ArtifactScope = b.Evidence.Count > 1 ? e.Path : null }).Candidates), []);

    // ── Parsing ────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void O01_canonical_specs_are_discovered_and_every_document_is_classified_once()
    {
        var b = OpenSpecFixture.Build();
        b.Model.Capabilities.Select(c => c.Key).Should().Equal("auth", "billing");
        var inv = OpenSpecStatusService.Inventory(b.Model);
        (inv.Discovered, inv.CurrentSpecifications, inv.ActiveChangeDocuments, inv.ActiveDeltaSpecifications, inv.ArchivedChangeDocuments, inv.SupportingDocuments,
            inv.Configuration, inv.NotOpenSpec, inv.Unsupported).Should().Be((13, 2, 3, 2, 2, 1, 1, 1, 1));
        b.Documents.Select(d => d.Path).Should().Equal(new[] { "openspec/specs/auth/spec.md", "openspec/specs/billing/spec.md" }, "only current specifications are catalog documents");
        b.Fingerprint.Should().StartWith(SpecificationFormatMarker.OpenSpecPrefix).And.HaveLength(64);
        b.Model.SchemaName.Should().Be("spec-driven");
        b.Model.SchemaState.Should().Be("BuiltIn");
    }

    [Fact]
    public void O02_requirements_without_an_FR_id_get_generated_os_keys_never_presented_as_source_ids()
    {
        var b = OpenSpecFixture.Build();
        b.Entries.Should().HaveCount(5).And.OnlyContain(e => SpecificationFormatMarker.IsGeneratedOpenSpecKey(e.RequirementId));
        b.Entries.Select(e => e.RequirementId).Should().Contain(["os:auth/user-authentication", "os:auth/session-timeout", "os:billing/invoice-export"]);
        b.Entries.Should().NotContain(e => e.RequirementId.StartsWith("FR-", StringComparison.OrdinalIgnoreCase));
        Req(b.Model, "auth", "User Authentication").Statement.Should().Be("The system SHALL issue a session token when a user submits valid credentials.");
        Req(b.Model, "auth", "User Authentication").HasNormativeKeyword.Should().BeTrue();
    }

    [Fact]
    public void O03_scenarios_without_a_user_story_are_candidates_under_their_requirement()
    {
        var b = OpenSpecFixture.Build();
        b.Evidence.SelectMany(e => e.Evidence.AcceptanceScenarios).Should().OnlyContain(s => s.StoryKey == null && s.RequirementIds.Count == 1);
        b.Evidence.Should().OnlyContain(e => e.Evidence.Stories.Count == 0, "no story is invented");
        var valid = Candidates(b).Single(c => c.Title == "Valid credentials");
        valid.Story.Should().BeNull();
        valid.RequirementIds.Should().Equal("os:auth/user-authentication");
        valid.Steps.Should().ContainSingle().Which.Action.Should().Be("a user submits a valid username and password");
        valid.ExpectedBehaviour.Should().Be("a session token is returned");
        var reset = Candidates(b).Single(c => c.Title == "Reset link");
        reset.Preconditions.Should().Equal("a registered user");
    }

    [Fact]
    public void O04_a_requirement_keeps_all_its_scenarios_and_AND_steps_join_the_previous_keyword()
    {
        var r = Req(OpenSpecFixture.Build().Model, "auth", "User Authentication");
        r.Scenarios.Select(s => s.Name).Should().Equal("Valid credentials", "Invalid credentials");
        r.Scenarios[1].Then.Should().Be("an error message is shown; no session token is returned");
        r.Scenarios[1].Steps.Should().Equal("WHEN a user submits an invalid password", "THEN an error message is shown", "AND no session token is returned");
        r.Line.Should().Be(8, "exact source line of the requirement heading");
        r.Scenarios[0].Line.Should().Be(11);
    }

    [Fact]
    public void O05_a_requirement_without_scenarios_is_a_requirement_only_candidate_and_counted()
    {
        var b = OpenSpecFixture.Build();
        Req(b.Model, "auth", "Session Timeout").Scenarios.Should().BeEmpty();
        var only = Candidates(b).Where(c => c.CanonicalKey.StartsWith("requirement:", StringComparison.Ordinal)).ToList();
        only.Should().ContainSingle().Which.RequirementIds.Should().Equal("os:auth/session-timeout");
        only[0].Steps.Should().BeEmpty("no test step is fabricated");
        only[0].OpenQuestions.Should().NotBeEmpty();
        b.Model.Diagnostics.Should().Contain(d => d.Code == "REQUIREMENT_WITHOUT_SCENARIO");
    }

    [Fact]
    public void O06_identical_requirement_titles_in_two_capabilities_are_never_merged()
    {
        var b = OpenSpecFixture.Build();
        var auth = Req(b.Model, "auth", "User Authentication");
        var billing = Req(b.Model, "billing", "User Authentication");
        auth.Key.Should().NotBe(billing.Key);
        var candidates = Candidates(b);
        candidates.Count(c => c.RequirementIds.Contains(auth.Key)).Should().Be(2);
        candidates.Count(c => c.RequirementIds.Contains(billing.Key)).Should().Be(1);
        candidates.GroupBy(c => c.SourceIdentity).Should().OnlyContain(g => g.Count() == 1, "no two candidates share an identity");
        candidates.GroupBy(c => c.CandidateId).Should().OnlyContain(g => g.Count() == 1);
    }

    [Fact]
    public void O07_malformed_and_unsupported_structures_are_reported_never_dropped_silently()
    {
        const string bad = """
            # Broken

            #### Scenario: Before any requirement
            - **WHEN** something
            - **THEN** nothing

            ## Requirements

            ### Requirement: Good
            The system SHALL work.

            #### Notes
            not a scenario

            ### Something else

            ### Requirement: Good
            Duplicate definition.

            ### Requirement:

            ## ADDED Requirements

            ### Requirement: Not baseline
            #### Scenario: Under a delta section
            - **WHEN** x

            ```
            ### Requirement: Inside code
            ```
            """;
        var b = OpenSpecFixture.Build(OpenSpecFixture.Documents(auth: bad));
        var codes = b.Model.Diagnostics.Where(d => d.Path == "openspec/specs/auth/spec.md").Select(d => d.Code).ToList();
        codes.Should().Contain(["ORPHAN_SCENARIO", "UNSUPPORTED_HEADING", "DUPLICATE_REQUIREMENT", "MISSING_REQUIREMENT_NAME", "MALFORMED_DELTA_IN_BASELINE"]);
        b.Model.Diagnostics.Should().Contain(d => d.Code == "UNSUPPORTED_DOCUMENT" && d.Path == "openspec/schemas/custom/README.md");
        b.Model.Capabilities.Single(c => c.Key == "auth").Requirements.Select(r => r.Name).Should().Equal("Good");
        b.Model.Diagnostics.Where(d => d.Code == "ORPHAN_SCENARIO").Should().ContainSingle("a scenario under an excluded delta requirement is excluded with it, not orphaned");
        // Every heading is accounted for: the independent reference scan reconciles with parsed + reported exclusions.
        var report = OpenSpecStatusService.Reconcile(b.Model, View(b), b.Evidence);
        report.Checks.Single(c => c.Id == "D01").Status.Should().Be(ReconciliationStatus.Pass, report.Checks.Single(c => c.Id == "D01").Observed);
        report.Checks.Single(c => c.Id == "D05").Status.Should().Be(ReconciliationStatus.Pass, report.Checks.Single(c => c.Id == "D05").Observed);
        report.Checks.Single(c => c.Id == "D06").Status.Should().Be(ReconciliationStatus.Pass);
    }

    [Fact]
    public void O08_an_unknown_custom_schema_is_reported_and_left_NOT_VERIFIED()
    {
        var b = OpenSpecFixture.Build(OpenSpecFixture.Documents(config: "# custom\nschema: research-first\n"));
        b.Model.SchemaName.Should().Be("research-first");
        b.Model.SchemaState.Should().Be("Unknown");
        b.Model.Diagnostics.Should().Contain(d => d.Code == "UNKNOWN_CUSTOM_SCHEMA" && d.Severity == OpenSpecDiagnosticSeverity.Error);
        var report = OpenSpecStatusService.Reconcile(b.Model, View(b), b.Evidence);
        report.Checks.Single(c => c.Id == "S00").Status.Should().Be(ReconciliationStatus.NotVerified);
        report.Overall.Should().Be(ReconciliationStatus.NotVerified, "an unverified schema is never reported as PASS");
        OpenSpecFixture.Build(OpenSpecFixture.Documents(config: null)).Model.SchemaState.Should().Be("BuiltIn", "no config = the built-in workflow");
    }

    [Fact]
    public void O09_identities_are_stable_across_an_unchanged_reimport_a_wording_edit_and_a_renamed_archive_folder()
    {
        var first = OpenSpecFixture.Build();
        var again = OpenSpecFixture.Build();
        again.Fingerprint.Should().Be(first.Fingerprint);
        Candidates(again).Select(c => (c.CandidateId, c.SourceIdentity)).Should().Equal(Candidates(first).Select(c => (c.CandidateId, c.SourceIdentity)));

        var edited = OpenSpecFixture.Build(OpenSpecFixture.Documents(auth: OpenSpecFixture.Auth.Replace("a session token is returned", "a signed session token is returned")));
        var before = Candidates(first).Single(c => c.Title == "Valid credentials");
        var after = Candidates(edited).Single(c => c.Title == "Valid credentials");
        after.SourceIdentity.Should().Be(before.SourceIdentity, "a content edit keeps the logical identity");
        after.CandidateId.Should().Be(before.CandidateId);
        after.Fingerprint.Should().NotBe(before.Fingerprint, "the content revision changes");
        edited.Fingerprint.Should().NotBe(first.Fingerprint);

        var moved = OpenSpecFixture.Build(OpenSpecFixture.Documents(root: "demo-main/"));
        moved.Model.CurrentRequirements.Select(r => r.Key).Should().Equal(first.Model.CurrentRequirements.Select(r => r.Key), "the archive's own root folder is not part of identity");
    }

    [Fact]
    public void O10_rename_needs_explicit_evidence_and_a_move_is_never_a_silent_rename()
    {
        var previous = OpenSpecFixture.Build().Model;
        // Baseline after the rename was archived: «User Authentication» → «User Sign-In» in auth, with the archived RENAMED delta as evidence.
        var renamedAuth = OpenSpecFixture.Auth.Replace("### Requirement: User Authentication", "### Requirement: User Sign-In");
        var docs = OpenSpecFixture.Documents(auth: renamedAuth).Where(d => !d.Path.Contains("/changes/add-2fa/")).ToList();
        docs.Add(new("openspec/changes/archive/2026-11-20-add-2fa/specs/auth/spec.md", "## RENAMED Requirements\n\n- FROM: `### Requirement: User Authentication`\n- TO: `### Requirement: User Sign-In`\n"));
        var current = OpenSpecFixture.Build(docs).Model;
        var changes = OpenSpecStatusService.Compare(previous, current);
        var rename = changes.Single(c => c.Kind == OpenSpecChangeKind.RenamedRequirement);
        (rename.PreviousKey, rename.Key).Should().Be(("os:auth/user-authentication", "os:auth/user-sign-in"));
        rename.Evidence.Should().Contain("2026-11-20-add-2fa").And.Contain("flyttes ikke automatisk");
        changes.Should().NotContain(c => c.Kind == OpenSpecChangeKind.RemovedRequirement && c.Key == "os:auth/user-authentication");

        // The same rename WITHOUT archive evidence: removed + new, never guessed from similarity.
        var unexplained = OpenSpecFixture.Build(OpenSpecFixture.Documents(auth: renamedAuth)).Model;
        var plain = OpenSpecStatusService.Compare(previous, unexplained);
        plain.Should().NotContain(c => c.Kind == OpenSpecChangeKind.RenamedRequirement && !c.Prospective);
        plain.Should().Contain(c => c.Kind == OpenSpecChangeKind.RemovedRequirement && c.Key == "os:auth/user-authentication");
        plain.Should().Contain(c => c.Kind == OpenSpecChangeKind.NewRequirement && c.Key == "os:auth/user-sign-in");
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void L01_L02_L03_L04_active_changes_are_proposals_only_and_never_change_the_baseline()
    {
        var b = OpenSpecFixture.Build();
        var evidenceIds = b.Evidence.SelectMany(e => e.Evidence.Requirements.Select(r => r.Id)).ToList();
        evidenceIds.Should().NotContain("os:auth/two-factor-authentication", "L01: ADDED is a proposal only");
        Req(b.Model, "auth", "Session Timeout").Statement.Should().Contain("30 minutes", "L02: MODIFIED does not replace the current text");
        evidenceIds.Should().Contain("os:auth/password-reset", "L03: REMOVED does not remove the current requirement");
        evidenceIds.Should().Contain("os:auth/user-authentication").And.NotContain("os:auth/user-sign-in", "L04: RENAMED does not rewrite the current identity");

        var add2fa = b.Model.ActiveChanges.Single(c => c.ChangeId == "add-2fa");
        add2fa.State.Should().Be(OpenSpecLifecycleState.ProposedChange);
        add2fa.Title.Should().Be("Change: Add two-factor authentication");
        add2fa.Deltas.Select(d => (d.Operation, d.RequirementName, d.Resolution)).Should().BeEquivalentTo(new[]
        {
            (OpenSpecDeltaOperation.Added, "Two-Factor Authentication", "NewRequirement"),
            (OpenSpecDeltaOperation.Modified, "Session Timeout", "Conflict"),
            (OpenSpecDeltaOperation.Removed, "Password Reset", "Resolved"),
            (OpenSpecDeltaOperation.Renamed, "User Authentication", "Resolved"),
        });
        add2fa.Deltas.Single(d => d.Operation == OpenSpecDeltaOperation.Removed).Reason.Should().Contain("identity provider");
        add2fa.Deltas.Single(d => d.Operation == OpenSpecDeltaOperation.Renamed).RenamedTo.Should().Be("User Sign-In");
        add2fa.Deltas.Single(d => d.Operation == OpenSpecDeltaOperation.Modified).BaselineRequirementKey.Should().Be("os:auth/session-timeout");
    }

    [Fact]
    public void L05_an_archived_change_is_history_and_not_double_counted()
    {
        var b = OpenSpecFixture.Build();
        b.Model.ArchivedChanges.Should().ContainSingle().Which.State.Should().Be(OpenSpecLifecycleState.ArchivedChange);
        b.Entries.Count(e => e.RequirementId == "os:billing/invoice-export").Should().Be(1, "counted once (from the current spec), not again from the archive");
        b.Evidence.Should().NotContain(e => e.Path.Contains("/archive/"));
        b.Model.ArchivedChanges[0].Deltas.Should().OnlyContain(d => d.Resolution == "History");
    }

    [Fact]
    public void L06_conflicting_active_changes_are_reported_and_never_resolved_by_ordering()
    {
        var b = OpenSpecFixture.Build();
        var conflict = b.Model.Diagnostics.Single(d => d.Code == "CONFLICTING_ACTIVE_CHANGES");
        conflict.ChangeId.Should().Be("add-2fa,shorter-sessions");
        b.Model.ActiveChanges.SelectMany(c => c.Deltas).Where(d => d.RequirementName == "Session Timeout").Should().OnlyContain(d => d.Resolution == "Conflict");
        Req(b.Model, "auth", "Session Timeout").Statement.Should().Contain("30 minutes", "neither proposal is applied");
    }

    [Fact]
    public void L07_a_baseline_transition_is_explicit_only_a_changed_current_spec_changes_the_baseline()
    {
        var before = OpenSpecFixture.Build().Model;
        // A new proposal alone: the baseline comparison is empty; the proposal appears only as prospective impact.
        var withProposal = OpenSpecFixture.Build([.. OpenSpecFixture.Documents(), new("openspec/changes/extra/specs/billing/spec.md",
            "## ADDED Requirements\n\n### Requirement: Credit Notes\nThe system SHALL issue credit notes.\n")]).Model;
        OpenSpecStatusService.Compare(before, withProposal).Should().BeEmpty();
        OpenSpecStatusService.Prospective(withProposal).Should().Contain(c => c.Name == "Credit Notes" && c.Prospective && c.ChangeId == "extra");
        // The change applied to openspec/specs (accepted): now it is an actual baseline change.
        var accepted = OpenSpecFixture.Build(OpenSpecFixture.Documents(auth: OpenSpecFixture.Auth.Replace("30 minutes", "15 minutes"))).Model;
        OpenSpecStatusService.Compare(before, accepted).Should().ContainSingle(c => c.Kind == OpenSpecChangeKind.ModifiedRequirement && c.Key == "os:auth/session-timeout" && !c.Prospective);
    }

    [Fact]
    public void Unresolved_delta_targets_are_reported()
    {
        var b = OpenSpecFixture.Build([.. OpenSpecFixture.Documents(), new("openspec/changes/ghost/specs/auth/spec.md",
            "## MODIFIED Requirements\n\n### Requirement: Does Not Exist\nThe system SHALL x.\n\n## ADDED Requirements\n\n### Requirement: Invoice Export\nx\n")]);
        b.Model.Diagnostics.Should().Contain(d => d.Code == "UNRESOLVED_DELTA_TARGET" && d.ChangeId == "ghost");
        b.Model.ActiveChanges.Single(c => c.ChangeId == "ghost").Deltas.Single(d => d.RequirementName == "Does Not Exist").Resolution.Should().Be("Unresolved");
        // ADDED «Invoice Export» to auth: a different capability than billing, so not «target exists» — capabilities are never merged.
        b.Model.ActiveChanges.Single(c => c.ChangeId == "ghost").Deltas.Single(d => d.RequirementName == "Invoice Export").Resolution.Should().Be("NewRequirement");
    }

    // ── Data quality ───────────────────────────────────────────────────────────────────────────────────────────────────────

    internal static SpecificationCatalogView View(OpenSpecModelBuilder.Built b) =>
        new(Guid.NewGuid(), Project, 1, DateTimeOffset.UtcNow, b.Fingerprint, b.Model.ArchiveSha256, b.Model.SnapshotId, b.Documents, b.Entries);

    [Fact]
    public void D01_to_D07_the_independent_reconciliation_passes_on_the_fixture()
    {
        var b = OpenSpecFixture.Build();
        var report = OpenSpecStatusService.Reconcile(b.Model, View(b), b.Evidence);
        report.Checks.Where(c => c.Status != ReconciliationStatus.Pass).Should().BeEmpty(string.Join("; ", report.Checks.Where(c => c.Status != ReconciliationStatus.Pass).Select(c => $"{c.Id}: {c.Expected} vs {c.Observed}")));
        report.Checks.Single(c => c.Id == "D01").Expected.Should().Be("5");
        report.Checks.Single(c => c.Id == "D05").Expected.Should().Be("5");
        report.Checks.Single(c => c.Id == "D07a").Observed.Should().Be("6", "5 scenarios + 1 requirement without scenarios");
        OpenSpecStatusService.ToMarkdown(report with { ProjectKey = Project }).Should().Contain("| D01 |").And.Contain("Pass");
    }

    [Fact]
    public void D_a_catalog_without_its_source_model_is_NOT_VERIFIED_not_PASS()
    {
        var b = OpenSpecFixture.Build();
        OpenSpecStatusService.Reconcile(null, View(b), b.Evidence).Overall.Should().Be(ReconciliationStatus.NotVerified);
    }

    [Fact]
    public void D02_D03_proposals_and_archives_counted_separately()
    {
        var m = OpenSpecFixture.Build().Model;
        m.ActiveChanges.Should().HaveCount(2);
        m.ActiveChanges.Sum(c => c.Deltas.Count).Should().Be(5);
        m.ArchivedChanges.Should().HaveCount(1);
        OpenSpecStatusService.Prospective(m).Should().OnlyContain(c => c.Prospective);
    }

    // ── Performance / security ─────────────────────────────────────────────────────────────────────────────────────────────

    private static List<SpecificationCatalogService.Document> Large(int capabilities, int requirements, int scenarios)
    {
        var docs = new List<SpecificationCatalogService.Document>();
        for (var c = 0; c < capabilities; c++)
        {
            var sb = new StringBuilder("# Capability\n\n## Requirements\n\n");
            for (var r = 0; r < requirements; r++)
            {
                sb.Append($"### Requirement: Rule {r}\nThe system SHALL handle case {c}-{r}.\n\n");
                for (var s = 0; s < scenarios; s++) sb.Append($"#### Scenario: Case {s}\n- **WHEN** input {c}-{r}-{s} arrives\n- **THEN** result {s} is shown\n\n");
            }
            docs.Add(new($"openspec/specs/cap-{c:000}/spec.md", sb.ToString()));
        }
        return docs;
    }

    [Fact]
    public void P01_P02_a_large_project_over_2000_scenarios_is_complete_and_searchable_beyond_the_window()
    {
        var clock = Stopwatch.StartNew();
        var b = OpenSpecFixture.Build(Large(120, 10, 2));
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20));
        b.Model.CurrentRequirements.Should().HaveCount(1200);
        b.Evidence.Sum(e => e.Evidence.AcceptanceScenarios.Count).Should().Be(2400);
        var candidates = Candidates(b);
        candidates.Should().HaveCount(2400, "the complete population is kept");
        var result = new ScenarioDiscoveryResult { DiscoveryId = "d", ProjectKey = Project, Candidates = candidates };
        D.Window(result).Candidates.Should().HaveCount(D.MaxCandidates, "only the client window is cut");
        var beyond = candidates.Except(D.Window(result).Candidates).First();
        D.Search(result, beyond.CanonicalKey, 0, 0, 10).Items.Should().Contain(c => c.CandidateId == beyond.CandidateId, "server-side search reaches beyond the window");
        D.Pick(result, [beyond.SourceIdentity]).Should().ContainSingle(c => c.CandidateId == beyond.CandidateId, "exact navigation by identity");
        OpenSpecStatusService.Reconcile(b.Model, View(b), b.Evidence).Overall.Should().Be(ReconciliationStatus.Pass);
    }

    [Fact]
    public void P03_malformed_markdown_never_throws_and_is_reported()
    {
        var garbage = string.Join("\n", Enumerable.Range(0, 2000).Select(i => i % 7 == 0 ? "```" : i % 5 == 0 ? "#### Scenario:" : i % 3 == 0 ? "### Requirement: " + new string('x', i % 50) : "- **THEN** ****"));
        var act = () => OpenSpecFixture.Build(OpenSpecFixture.Documents(auth: garbage + "\n" + new string('#', 30_000)));
        var b = act.Should().NotThrow().Subject;
        b.Model.Diagnostics.Should().NotBeEmpty();
        b.Model.Diagnostics.Should().Contain(d => d.Code == "LINE_TOO_LONG");
    }

    [Fact]
    public void P04_duplicate_and_unsafe_source_paths_are_unsupported_with_a_reason()
    {
        var docs = OpenSpecFixture.Documents();
        docs.Add(new("OpenSpec/Specs/Auth/spec.md", "### Requirement: Shadow\nThe system SHALL shadow."));
        docs.Add(new("../openspec/specs/evil/spec.md", "### Requirement: Evil\nx"));
        docs.Add(new("openspec/specs/../../etc/spec.md", "x"));
        var b = OpenSpecFixture.Build(docs);
        b.Model.Documents.Where(d => d.Kind == OpenSpecDocumentKind.Unsupported).Select(d => d.Reason).Should().Contain(r => r!.Contains("Duplikat")).And.Contain(r => r!.Contains("Usikker sti"));
        b.Model.CurrentRequirements.Select(r => r.Name).Should().NotContain(["Shadow", "Evil"]);
        OpenSpecSourceDiscovery.SafePath("C:/x/openspec/specs/a/spec.md").Should().BeNull();
        OpenSpecSourceDiscovery.SafePath("/openspec/specs/a/spec.md").Should().BeNull();
    }

    [Fact]
    public void P05_two_projects_and_two_applications_are_isolated()
    {
        var dir = Path.Combine(Path.GetTempPath(), "os-iso-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new OpenSpecSourceModelStore(dir);
            var a = OpenSpecFixture.Build(projectKey: "import:a");
            var c = OpenSpecFixture.Build(OpenSpecFixture.Documents(auth: OpenSpecFixture.Auth.Replace("30 minutes", "10 minutes")), projectKey: "import:b");
            store.Save("import:a", a.Fingerprint, a.Model);
            store.Save("import:b", c.Fingerprint, c.Model);
            store.Find("import:a", c.Fingerprint).Should().BeNull("another project's model is never returned");
            store.Find("import:b", a.Fingerprint).Should().BeNull();
            store.Find("import:a", a.Fingerprint)!.ProjectKey.Should().Be("import:a");
            var settings = new SpecificationFormatSettingsStore(dir);
            settings.Save("repo:a", SpecificationFormat.OpenSpec, "t", DateTimeOffset.UtcNow);
            settings.FormatOf("repo:b").Format.Should().Be(SpecificationFormat.SpecKit);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void P06_zip_path_traversal_is_rejected_before_any_route_reads_it()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var w = new StreamWriter(zip.CreateEntry("openspec/specs/auth/spec.md").Open())) w.Write(OpenSpecFixture.Auth);
            using (var w = new StreamWriter(zip.CreateEntry("../../outside/spec.md").Open())) w.Write("### Requirement: Escape");
        }
        var read = IqrSourceArchiveReader.ReadDetailed("evil.zip", ms.ToArray(), captureDocuments: true);
        read.Workspace.Should().BeNull();
        read.Failure!.Code.Should().Be("ARCHIVE_PATH_TRAVERSAL");
    }

    [Fact]
    public void P07_parser_resource_limits_and_cancellation_are_enforced()
    {
        var tooMany = Enumerable.Range(0, OpenSpecLimits.MaxDocuments + 1).Select(i => new SpecificationCatalogService.Document($"openspec/specs/c{i}/spec.md", "x")).ToList();
        var act = () => OpenSpecFixture.Build(tooMany);
        act.Should().Throw<OpenSpecModelBuilder.RouteProblem>().Which.Code.Should().Be("OPENSPEC_TOO_MANY_DOCUMENTS");

        var huge = OpenSpecFixture.Build(OpenSpecFixture.Documents(auth: "### Requirement: Big\n" + new string('a', OpenSpecLimits.MaxDocumentChars + 1)));
        huge.Model.Documents.Single(d => d.Path == "openspec/specs/auth/spec.md").Kind.Should().Be(OpenSpecDocumentKind.Unsupported);
        huge.Model.Diagnostics.Should().Contain(d => d.Code == "UNSUPPORTED_DOCUMENT" && d.Path == "openspec/specs/auth/spec.md");

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = () => OpenSpecFixture.Build(Large(5, 5, 1), ct: cts.Token);
        cancelled.Should().Throw<OperationCanceledException>();
    }

    [Fact]
    public void M10_OpenSpec_scenarios_are_never_classified_as_E2E_without_documented_evidence()
    {
        var crossing = OpenSpecFixture.Auth.Replace("a user submits a valid username and password", "the portal calls the identity service and the billing service");
        var candidates = Candidates(OpenSpecFixture.Build(OpenSpecFixture.Documents(auth: crossing)));
        candidates.Should().OnlyContain(c => c.Type == ScenarioCandidateType.BusinessFunctional, "two services named in a step is not E2E evidence");
        OpenSpecFixture.Build().Evidence.SelectMany(e => e.Evidence.AcceptanceScenarios).Should().OnlyContain(s => s.IntendedLevel == BirkNext.TestCoverage.TestLevel.Unknown);
    }

    [Fact]
    public void A_route_without_an_openspec_folder_raises_a_problem_and_never_falls_back()
    {
        var act = () => OpenSpecFixture.Build([new("specs/001-login/spec.md", SpecKitRegressionBaselineTests.RootSpec)]);
        act.Should().Throw<OpenSpecModelBuilder.RouteProblem>().Which.Code.Should().Be("OPENSPEC_ROOT_MISSING");
    }

    [Theory]
    [InlineData("User Authentication", "user-authentication")]
    [InlineData("  Brukerens   Pålogging ", "brukerens-pålogging")]
    [InlineData("API: v2 / Export (PDF)", "api-v2-export-pdf")]
    [InlineData("!!!", "x-")]
    public void Identity_normalization_rules_are_as_documented(string name, string slugPrefix)
    {
        OpenSpecIdentity.Slug(name).Should().StartWith(slugPrefix);
        OpenSpecIdentity.Slug(new string('a', 100)).Should().HaveLength(69, "60 characters + «-» + 8 hex");
        OpenSpecIdentity.RequirementKey("", "auth", name).Should().StartWith("os:auth/");
        OpenSpecIdentity.RequirementKey("svc-a.openspec", "auth", name).Should().StartWith("os:svc-a.openspec~auth/");
    }
}
