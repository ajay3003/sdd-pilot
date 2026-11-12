namespace BirkNext.Web.Services;

/// <summary>One guide topic: a section of the User Guide with its group, search keywords and the anchors that sit inside it.</summary>
public sealed record GuideTopic(string Id, string Title, string Group, string Keywords, params string[] Anchors)
{
    public string Href => UserGuideContent.Href(Id);
    public bool Owns(string anchor) => Id.Equals(anchor, StringComparison.OrdinalIgnoreCase) || Anchors.Contains(anchor, StringComparer.OrdinalIgnoreCase);
}

/// <summary>A task card: what the workflow is for, when to use it, and the real feature it opens.</summary>
public sealed record GuideTaskCard(string Title, string Purpose, string When, string Href, string OpenLabel, string GuideId);

/// <summary>A status word as the product uses it. <see cref="Tone"/> is a <c>status-chip-*</c> suffix; the word itself carries the meaning.</summary>
public sealed record GuideStatusTerm(string Term, string Tone, string Meaning, string NotTheSameAs);

public sealed record GuideGlossaryEntry(string Term, string Definition);

/// <summary>A troubleshooting entry: the symptom a tester sees, the usual cause and the feature that resolves it.</summary>
public sealed record GuideTroubleshootingEntry(string Problem, string Cause, string Action, string Href, string OpenLabel);

/// <summary>
/// Structured content of the User Guide: topics (navigation and search), task cards, the canonical status reference, glossary and
/// troubleshooting. The prose lives in the guide section components; every anchor named here exists literally in a guide source file
/// (checked by Documentation Health and by the guide tests).
/// </summary>
public static class UserGuideContent
{
    public const string Route = "/user-guide";
    public static string Href(string anchor) => $"{Route}#{anchor}";

    /// <summary>Target Environment tabs open on the active environment, so a guide link keeps the current environment context.</summary>
    public static string TargetEnvironmentTab(string tab) => $"/admin/system-settings?section=target-environments&tab={tab}";

    public const string GettingStarted = "Getting started", ProjectEvidence = "Project & evidence", TestSetup = "Test setup",
        Testing = "Testing", IntegrationE2E = "Integration & E2E", Governance = "Review & governance", VersionsWorkspace = "Versions & workspace", Reference = "Reference";

    public static readonly IReadOnlyList<string> Groups = [GettingStarted, ProjectEvidence, TestSetup, Testing, IntegrationE2E, Governance, VersionsWorkspace, Reference];

    public static readonly IReadOnlyList<GuideTopic> Topics =
    [
        new("overview", "What BirkNext does", GettingStarted, "introduction home purpose quick start tasks"),
        new("start-here", "Start here", GettingStarted, "first time onboarding steps order setup path"),
        new("concepts", "Core concepts", GettingStarted, "applicability coverage quality evidence maturity snapshot provenance analyzer provider engine extension"),
        new("getting-started", "Import a project", GettingStarted, "import zip archive package demo project documents modules artifact candidates selection workspace",
            "project-inputs", "project-import", "multiple-documents", "sample-project-discovery", "current-workspace"),
        new("project-quality", "Project Quality and the next action", GettingStarted, "project quality dashboard next action recommendation metrics execution evidence score stage workflow imported reviewed tested"),
        new("system-understanding", "System Understanding (the system under test)", GettingStarted, "system understanding systemforstaelse systemforståelse flow how the system works step by step data contracts business rules what to test guide revision review publish evidence class"),
        new("manual-testing", "Manual testing", GettingStarted, "manual testing workspace view functional tester test scenarios what to test test data coverage azure devops test plans test case no frontend xsd manuell testing testscenarioer"),
        new("recommended-workflow", "Recommended Workflow", GettingStarted, "next step next action configure project bind evidence review decision", "review-decisions"),

        new("project-model", "Project model and binding", ProjectEvidence, "project logical system package bind binding role required optional bound ready partial conflict source-only docs-only contract infrastructure supporting",
            "package-types", "project-readiness", "project-tab"),
        new("source-analysis", "Source Analysis", ProjectEvidence, "snapshot source zip analyze fingerprint technology coverage", "technology-coverage"),
        new("explorers", "Explorers and documents", ProjectEvidence, "specification constitution plan task data model explorer documents module", "task-explorer", "specification-review"),
        new("generated-documentation", "Generated documentation", ProjectEvidence, "autodoc generated docs explorer health freshness implementation-derived"),
        new("traceability", "Traceability", ProjectEvidence, "requirements traceability task alignment change traceability azure devops implementation evidence", "implementation-review"),

        new("target-environment", "Target Environment", TestSetup, "environment tabs general project target application authentication endpoint browser thresholds core web vitals validation"),
        new("connected-systems", "Connected Systems", TestSetup, "altinn system provider detect needs attention blocked review required manual automated readiness",
            "per-hop-authentication", "needs-attention", "detection-vs-configuration", "manual-vs-automated"),
        new("browser-authentication", "Browser authentication", TestSetup, "authentication attended unattended login mfa automation identity credential validate automated execution scheduled browser",
            "attended-login", "unattended-login"),
        new("integrations", "Integration Catalog and IQR", TestSetup, "integration catalog iqr integration quality review proposal technical relationship consumer mapping contract"),
        new("test-data", "Test Data", TestSetup, "asset reporter child identity xml fixture payload scalar alias mapping fodselsnummer provenance",
            "reporter-vs-child", "asset-names", "xml-fixtures", "xml-field-mappings", "xml-provenance"),
        new("schema-studio", "Schema Studio", TestSetup, "xsd schema validation contract generate xml snapshot"),
        new("setup-library", "Setup Library", TestSetup, "setup package export import preflight portable sanitized version"),

        new("testing", "Runtime testing", Testing, "runtime authentication proxy certificate edge frontend api quality review critical e2e"),
        new("browser-endpoint-discovery", "Browser and Endpoint Discovery", IntegrationE2E, "browser companion endpoint discovery observed dom accessibility performance traffic"),
        new("performance-test-review", "Performance Test Review", Testing, "performance load k6 scenario workload thresholds readiness run results compare history baseline dynamic parameter graphql",
            "performance-scenario", "dynamic-parameters", "step-ordering", "rest-graphql", "performance-readiness", "performance-results", "performance-compare-history"),
        new("quality-reviews", "Quality reviews", Testing, "document quality review frontend api integration security fuzzing active tests", "integration-journeys"),
        new("skolenaervaer", "Skolenærvær Testing", IntegrationE2E, "skolenaervaer altinn mu mm bufdata rapportmottak journey reporter child"),
        new("test-coverage-review", "Test Coverage & Overlap", Testing, "test coverage overlap duplicate journeys qa scope"),
        new("code-risk-review", "Code Risk Review", Testing, "code risk deterministic rules findings"),
        new("contract-testing", "Contract Testing", Testing, "contract openapi swagger graphql schema version test revision assertion impact coverage archive retention"),
        new("integration-suites", "Integration Test Suites and multi-source E2E", IntegrationE2E, "integration suite composite source context multi-source end-to-end e2e stage altinn mu mm skolenaervaer bufdata subflow full e2e correlation readiness kode 6 7 schema namespace",
            "mu-mm-contract-risk"),
        new("suggested-e2e", "Suggested E2E tests and the Edge Companion", IntegrationE2E, "suggested tests critical e2e regression browser companion edge attended session pairing dom locator verification approve diagnostic flow route form razor",
            "dom-verification", "companion-pairing"),
        new("execution-results", "Test Execution & Evidence", Testing, "execution evidence results runs outcome passed failed not assessed partial run call attempt request response assertion graphql retry redacted export timeline traceability compare report retention journey performance parquet static assessment"),
        new("parquet-viewer", "Parquet Quality Review", Testing, "parquet dataset data file bufdata validation rules profile compare baseline schema"),
        new("pipeline", "Pipeline Review", Testing, "pipeline ci cd definitions needs refresh"),

        new("documentation-drift", "Documentation Drift", Governance, "drift compare changes overview authored generated lineage consistent difference conflict", "drift-statuses"),
        new("readiness", "Readiness and review decisions", Governance, "readiness blockers ownership next action review decision"),
        new("delivery-overview", "Delivery overview", Governance, "delivery overview virtual sdd backlog planned in progress implemented verified accepted blocked features user stories acceptance criteria"),
        new("delivery-reporting", "Delivery reporting", Governance, "delivery weekly report scrum master project leader stories features tasks progress baseline current snapshot export"),

        new("status-reference", "Status meanings", Reference, "status configured detected bound ready observed verified partial blocked needs review missing unavailable not applicable passed failed error not assessed cancelled timed out interrupted unknown outcome historical currently applicable",
            "configured-vs-detected", "observed-vs-verified", "execution-outcomes", "result-currency"),
        new("feature-availability", "Feature availability", Reference, "unavailable preview available azure not configured", "environment-analysis"),
        new("troubleshooting", "Troubleshooting", Reference, "problem fix missing empty blocked invalid tls baseline", "scenarios", "faq"),
        new("glossary", "Glossary", Reference, "terms definitions project package fixture contract baseline"),
        new("system-settings", "System Settings", VersionsWorkspace, "admin settings diagnostics maintenance reset feature visibility hidden switch sidebar menu legacy optional runtime profile tester native developer container postgresql k6 environment",
            "runtime-profiles", "settings-overview", "feature-visibility"),
        new("versioning", "Versions: artifacts and BirkNext builds", VersionsWorkspace, "version versioning revision contract version test revision execution manifest historical pinned dependency build number commit ci application version release archive checksum companion version about",
            "artifact-versioning", "application-versioning"),
        new("whats-new", "What's new", VersionsWorkspace, "whats new release notes changes changelog version"),
        new("workspace-transfer", "Backup & Transfer", VersionsWorkspace, "backup restore export import package birknext move project integration disaster recovery passphrase encryption"),
        new("feature-matrix", "Feature matrix", Reference, "all features navigation purpose boundary", "where", "workflows"),
        new("discovery", "Navigation badges", Reference, "badge partial setup no evidence backend connectivity", "backend-connectivity", "validation"),
        new("about", "About and limitations", Reference, "ecosystem optional extension limitations", "ecosystem", "optional-capabilities", "feature-details"),
    ];

    /// <summary>The three audience areas of the Help Center. User guides are the default landing area.</summary>
    public enum GuideArea { UserGuides, Administration, TechnicalReference }

    public static string AreaLabel(GuideArea area) => area switch
    {
        GuideArea.Administration => "Administration", GuideArea.TechnicalReference => "Technical reference", _ => "User guides",
    };

    private static readonly HashSet<string> AdministrationTopics = new(StringComparer.Ordinal)
        { "target-environment", "connected-systems", "browser-authentication", "integrations", "setup-library", "system-settings", "workspace-transfer" };
    private static readonly HashSet<string> ReferenceTopics = new(StringComparer.Ordinal)
        { "status-reference", "glossary", "feature-matrix", "discovery", "about", "feature-availability" };

    public static GuideArea AreaOf(GuideTopic topic) =>
        AdministrationTopics.Contains(topic.Id) ? GuideArea.Administration : ReferenceTopics.Contains(topic.Id) ? GuideArea.TechnicalReference : GuideArea.UserGuides;

    /// <summary>
    /// When an article's content was last checked against the product, and which BirkNext version that was. Only articles that were actually
    /// verified are listed; every other article says its verification is not recorded (a modification date is never taken as verification).
    /// The evidence names what the check compared against (kept for reviewers; not shown as the guide itself).
    /// </summary>
    public sealed record ArticleVerification(string VerifiedOn, string AppliesTo, string Evidence);

    public static readonly IReadOnlyDictionary<string, ArticleVerification> Verified = new Dictionary<string, ArticleVerification>(StringComparer.Ordinal)
    {
        ["execution-results"] = new("2026-11-11", "BirkNext 1.0.0", "Execution Results areas and the Executed against panel; execution manifest acceptance AV-01–AV-33"),
        ["integration-suites"] = new("2026-11-11", "BirkNext 1.0.0", "Integration Test Suites headings; MU → MM gap report MM-G1"),
        ["suggested-e2e"] = new("2026-11-11", "BirkNext 1.0.0", "Critical E2E Regression → Suggested tests actions; Browser Companion pairing contracts"),
        ["versioning"] = new("2026-11-11", "BirkNext 1.0.0", "Artifact versioning acceptance AV-*; application versioning acceptance CI-*, RT-*, BV-*"),
        ["whats-new"] = new("2026-11-11", "BirkNext 1.0.0", "No release-notes source exists in this build"),
        ["status-reference"] = new("2026-11-11", "BirkNext 1.0.0", "ExecutionState and ResultApplicability contracts"),
        ["project-quality"] = new("2026-11-11", "BirkNext 1.0.0", "Dashboard and Recommended Workflow share DashboardPresentation.NextStep; ProjectQualityStages; ExecutionEvidenceFacts"),
        ["system-understanding"] = new("2026-11-11", "BirkNext 1.0.0", "SystemGuideService revisions (system_guide_revisions), SystemUnderstanding page, curated Skolenærvær draft"),
        ["troubleshooting"] = new("2026-11-11", "BirkNext 1.0.0", "Messages of the build-info card, Backup & Transfer import and Execution Results"),
        ["system-settings"] = new("2026-11-11", "BirkNext 1.0.0", "Application information card (api/system/build-info)"),
        ["workspace-transfer"] = new("2026-11-11", "BirkNext 1.0.0", "Backup & Transfer acceptance WT-01–WT-43"),
    };

    private static readonly Dictionary<string, string[]> Related = new(StringComparer.Ordinal)
    {
        ["getting-started"] = ["source-analysis", "project-model", "recommended-workflow"],
        ["source-analysis"] = ["getting-started", "contract-testing", "documentation-drift"],
        ["target-environment"] = ["browser-authentication", "connected-systems", "test-data"],
        ["connected-systems"] = ["target-environment", "integration-suites", "integrations"],
        ["contract-testing"] = ["test-data", "execution-results", "versioning"],
        ["integration-suites"] = ["contract-testing", "connected-systems", "execution-results"],
        ["suggested-e2e"] = ["browser-authentication", "browser-endpoint-discovery", "execution-results"],
        ["execution-results"] = ["status-reference", "versioning", "troubleshooting"],
        ["versioning"] = ["execution-results", "system-settings", "workspace-transfer"],
        ["workspace-transfer"] = ["versioning", "system-settings", "connected-systems"],
        ["test-data"] = ["contract-testing", "schema-studio", "suggested-e2e"],
        ["status-reference"] = ["glossary", "troubleshooting", "execution-results"],
        ["troubleshooting"] = ["status-reference", "glossary", "feature-availability"],
        ["system-settings"] = ["versioning", "workspace-transfer", "feature-availability"],
    };

    /// <summary>Related guides of a topic (explicit, reviewed list; empty when none is defined).</summary>
    public static IReadOnlyList<GuideTopic> RelatedTo(string topicId) =>
        Related.TryGetValue(topicId, out var ids) ? [.. ids.Select(id => Topics.FirstOrDefault(t => t.Id == id)).OfType<GuideTopic>()] : [];

    /// <summary>One line per category for the Help Center home (what the group helps with), and its icon key.</summary>
    public static readonly IReadOnlyDictionary<string, (string Icon, string Description)> GroupInfo = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
    {
        [GettingStarted] = ("start", "What BirkNext does and the first steps with a project."),
        [ProjectEvidence] = ("source", "Projects, source snapshots, documents and traceability."),
        [TestSetup] = ("setup", "Target environments, connected systems, integrations and Test Data."),
        [Testing] = ("test", "Reviews, contract and performance tests, results and evidence."),
        [IntegrationE2E] = ("flow", "Integration suites, Skolenærvær and end-to-end browser tests."),
        [Governance] = ("review", "Readiness, review decisions, drift and delivery reporting."),
        [VersionsWorkspace] = ("box", "Versions, settings, backup and what changed."),
        [Reference] = ("book", "Status meanings, glossary, troubleshooting and feature reference."),
    };

    /// <summary>The getting-started journey of a new tester: ten steps, each one an existing article (never one giant article).</summary>
    public static readonly IReadOnlyList<(string Title, string TopicId)> Journey =
    [
        ("What BirkNext does", "overview"), ("Import a source project", "getting-started"), ("Select the project and snapshot", "project-model"),
        ("Understand the system", "system-understanding"), ("Review requirements", "traceability"), ("Inspect integrations", "integrations"),
        ("Prepare Test Data", "test-data"), ("Prepare an Integration Suite", "integration-suites"), ("Check execution readiness", "readiness"),
        ("Review execution evidence", "execution-results"),
    ];

    /// <summary>The topic before and after a topic in reading order (the registry order), for article paging.</summary>
    public static (GuideTopic? Previous, GuideTopic? Next) Neighbours(string topicId)
    {
        for (var i = 0; i < Topics.Count; i++)
            if (Topics[i].Id == topicId) return (i > 0 ? Topics[i - 1] : null, i < Topics.Count - 1 ? Topics[i + 1] : null);
        return (null, null);
    }

    public static GuideTopic? TopicFor(string? anchor) =>
        string.IsNullOrWhiteSpace(anchor) ? null : Topics.FirstOrDefault(t => t.Owns(anchor.TrimStart('#')));

    /// <summary>Search over titles, groups, keywords, headings and body text (see <see cref="UserGuideSearch"/>). An empty query returns nothing.</summary>
    public static IReadOnlyList<GuideTopic> Search(string? query) => [.. UserGuideSearch.Find(query).Select(h => h.Topic)];

    public static readonly IReadOnlyList<GuideTaskCard> TaskCards =
    [
        new("Import a project", "Bring a project ZIP into BirkNext: documents become artifacts, source becomes a Source Analysis snapshot.",
            "First time, or when a new version or another package (docs, contracts, infrastructure) is available.", "/project-import", "Open Import Project", "getting-started"),
        new("Configure Project", "Tell BirkNext which logical system an environment tests and which imported packages provide its evidence.",
            "After importing, when Recommended Workflow suggests it, or when evidence comes from several packages.", TargetEnvironmentTab("project"), "Open Project", "project-model"),
        new("Set up Target Environment", "Configure the deployed system under test: URL, authentication, discovery, thresholds.",
            "Before any runtime review: frontend, API, integration, performance or end-to-end testing.", "/admin/system-settings?section=target-environments", "Open Target Environments", "target-environment"),
        new("Review Connected Systems", "See what a connected system such as Altinn needs before manual or automated testing, and fix it in priority order.",
            "When a test pack depends on an external system, or Needs attention lists blockers.", TargetEnvironmentTab("external"), "Open Connected Systems", "connected-systems"),
        new("Prepare Test Data", "Register and approve reusable assets — identities, values and XML fixtures — that tests reference by alias.",
            "When readiness reports missing or unapproved Test Data, or a new XML fixture is needed.", "/test-data", "Open Test Data", "test-data"),
        new("Build a performance scenario", "Turn discovered operations into executable steps with runtime values, then check readiness and run.",
            "When an API needs a controlled load test with thresholds and a baseline.", "/performance-test-review", "Open Performance Test Review", "performance-test-review"),
        new("Review Documentation Drift", "Compare authored intent with generated documentation and follow changes across imports of the same project.",
            "After importing a project that has generated documentation, or a new revision of it.", "/documentation-drift", "Open Documentation Drift", "documentation-drift"),
    ];

    /// <summary>The canonical status vocabulary. Wording follows the product UI.</summary>
    public static readonly IReadOnlyList<GuideStatusTerm> StatusTerms =
    [
        new("Configured", "info", "Saved, authoritative configuration — a value you or an approved setup stored.", "Not proof that the value works; verification is separate."),
        new("Detected", "neutral", "A value BirkNext found (source, metadata, Test Pack, observation) and proposes. It is not saved until you apply it.", "Configured. Detection never overwrites an explicit saved value."),
        new("Bound", "info", "A reference to known evidence, such as an imported package bound to a Project role.", "Ready. A bound package can still lack the capability the role needs."),
        new("Ready", "ready", "Every requirement for that one dimension is met.", "Passed or verified. Ready for manual testing is not ready for automated execution."),
        new("Observed", "info", "Runtime or browser evidence exists — for example a sign-in seen in Browser Discovery.", "Verified. Observed evidence has not been checked against verification criteria."),
        new("Verified", "ready", "The evidence satisfied explicit verification criteria.", "Observed or configured."),
        new("Partial", "partial", "Some, but not all, of what the dimension needs is present.", "Failed. Partial evidence is usable where it is complete."),
        new("Blocked", "critical", "Something required is missing, so this activity cannot proceed.", "Review required, which never stops preparation on its own."),
        new("Needs review / Review required", "warning", "A person must look at it: a proposal, a conflict, incomplete evidence or a change.", "Blocked. Review items do not stop manual preparation."),
        new("Missing", "critical", "Required input does not exist yet.", "Not applicable, and not \"no evidence of a problem\"."),
        new("Not configured", "neutral", "The feature or setting works, but nobody has configured it for this environment.", "Unavailable."),
        new("Unknown", "not-assessed", "BirkNext has no configured or reviewed answer — it does not guess.", "Not required."),
        new("Not required", "neutral", "Explicitly configured as not needed.", "Unknown."),
        new("Not verified", "not-assessed", "No verification has been recorded yet.", "Failed verification."),
        new("Unavailable", "neutral", "The BirkNext capability itself is not ready for normal use.", "Not configured. Configuration alone does not make it available."),
        new("Not applicable (N/A)", "neutral", "The review or check does not fit this project.", "Unsupported, which is a tool limitation for something that does apply."),
    ];

    /// <summary>Execution outcomes of a run or a call (Test Execution &amp; Evidence). They describe what happened at runtime, never review readiness.</summary>
    public static readonly IReadOnlyList<GuideStatusTerm> ExecutionOutcomes =
    [
        new("Passed", "ready", "The call was sent, a response was received and every assertion held.", "HTTP 200 alone: a 200 with failed assertions or GraphQL errors is not Passed."),
        new("Failed", "critical", "A response was received and at least one assertion did not hold.", "Error: Failed means the target answered differently from what the test expects."),
        new("Error", "critical", "The call could not complete (connection, TLS, protocol or contract-check error).", "Failed: an Error says nothing about the target's business behaviour."),
        new("Timed out", "critical", "No complete response arrived within the call's time limit.", "Unknown outcome is not safe to retry: a data-changing call may have taken effect."),
        new("Blocked", "warning", "Refused before sending (safety policy, missing confirmation for a data-changing call, authentication unavailable, a dependency that did not pass).", "Failed: nothing was sent, so nothing about the target is known."),
        new("Not assessed", "not-assessed", "Sent and answered, but there was nothing to judge (no assertion, or an assertion that could not be evaluated, such as a response without a documented schema).", "Passed: no assertion is not a success."),
        new("Skipped", "neutral", "Not sent because the run stops at the first failure.", "Blocked: skipping is a run choice, not a refusal."),
        new("Cancelled", "neutral", "Stopped by a person while running.", "Failed or Error."),
        new("Interrupted", "warning", "BirkNext stopped while the call was running or before it started; the outcome of a running call is unknown and it was not sent again.", "Failed: an unknown outcome must be checked at the target before retrying."),
    ];

    /// <summary>Whether a stored result is still evidence for what is current (the Executed against panel). The result itself never changes.</summary>
    public static readonly IReadOnlyList<GuideStatusTerm> ResultCurrency =
    [
        new("Current definitions", "ready", "Every definition the run used is still the current one, unchanged, and BirkNext materialised the calls from them.", "A newer result: this says the old result still applies, not that it was re-run."),
        new("Historical result", "warning", "At least one definition (contract, test revision, Test Data, endpoint binding, suite) has changed since the run. The result stays true for what it ran against.", "Failed: a historical PASS is still a PASS for the version it tested."),
        new("Currency unknown", "not-assessed", "The run has no execution manifest (created before manifests existed) or names references BirkNext cannot verify (a hand-built run or an override).", "Historical or current: neither can be claimed."),
    ];

    public static readonly IReadOnlyList<GuideGlossaryEntry> Glossary =
    [
        new("Project", "The logical system under test, configured per Target Environment. It binds evidence from one or more imported packages; it is not \"the latest ZIP\"."),
        new("Package", "One imported archive (Import Project or Source Analysis upload). An independent evidence provider: source and docs, source only, docs only, contracts, infrastructure or shared source."),
        new("Source snapshot", "An immutable, fingerprinted Source Analysis record of one archive. New analysis creates a new snapshot; history is never rewritten."),
        new("Evidence", "Anything BirkNext can show for a claim: documents, source declarations, configuration, observations, test executions."),
        new("Binding", "A saved reference from a Project role (or consumer) to specific evidence. Changing a binding never deletes the import or snapshot."),
        new("Test Pack", "A built-in description of what a domain test (for example Skolenærvær report intake) needs: evidence roles, systems, Test Data roles."),
        new("Artifact Pack", "A group of project documents inside a repository (for example one Spec-Kit feature folder). Reviews read one pack and its shared artifacts."),
        new("Test Data asset", "A reusable, centrally owned value, identity, payload or file with an alias, a status and a scope. Consumers reference it; they never copy it."),
        new("Fixture", "An approved Test Data payload used by a test — for example an XML report validated against its XSD."),
        new("Contract", "A declared interface: OpenAPI, GraphQL schema, XSD or message contract, kept with its source provenance."),
        new("Runtime evidence", "What was observed or executed against a deployed target, as opposed to what source or configuration declares."),
        new("Observed", "Runtime evidence exists. Not the same as verified."),
        new("Verified", "Evidence satisfied explicit verification criteria."),
        new("Baseline", "A performance run you explicitly promote for comparison. The newest run never becomes the baseline automatically."),
        new("Fingerprint", "A content hash identifying an exact file, archive or revision."),
        new("Applicability", "Whether a review fits the project. Not applicable is not a failure; Unsupported is a tool limitation."),
        new("Execution manifest", "The record of exactly what one test run executed against: contract version, test revision, Test Data version, endpoint binding, suite revision, environment and BirkNext build. Written once with the run and never changed."),
        new("Historical result", "A result whose pinned definitions have since changed. It stays true for what it ran against; it is not evidence for the current definitions."),
        new("Composite source context", "The approved mapping of an end-to-end flow's stages to imported source snapshots (for example Altinn, MU, MM, Skolenærvær, Bufdata), possibly from different projects."),
        new("Attended session", "A browser session in which a person is present and signed in; the Edge Companion acts only in that session and never stores credentials."),
        new("Build version", "Which build of BirkNext itself is running: application version plus channel (Local, CI build, Release) and, for CI, the pipeline run. Not the version of the application you test."),
    ];

    public static readonly IReadOnlyList<GuideTroubleshootingEntry> Troubleshooting =
    [
        new("Project documents are missing in the Explorers", "The documentation package is not bound to the Project, or a role has several candidates and needs a selection.",
            "Bind the documentation role on the Project tab, or choose the artifact in the Explorer.", TargetEnvironmentTab("project"), "Open Project"),
        new("Source Analysis has no snapshot", "Only documents were imported, or the source upload failed.",
            "Import the source package or upload it in Source Analysis. A docs-only project is still valid for document reviews.", "/source-analysis", "Open Source Analysis"),
        new("An Explorer is empty", "No artifact of that role was detected in any bound package. Every role is optional.",
            "Import or bind the package that holds the document, or import the single document in the Explorer.", "/specification-explorer", "Open Specification Explorer"),
        new("Contract source is ambiguous", "Several snapshots or XSD files match, and BirkNext never picks the newest by date.",
            "Choose the snapshot and XSD explicitly, or bind the contract package on the Project tab.", "/schema-studio", "Open Schema Studio"),
        new("Test Data is incomplete", "A required role (reporter, child, XML fixture) has no approved asset.",
            "Register or approve the asset in Test Data; the consumer then references it by alias.", "/test-data", "Open Test Data"),
        new("XML fixture is invalid", "The fixture does not validate against the selected XSD, or a mapping points to a path the XSD no longer has.",
            "Regenerate from the current XSD, review changed mappings, then validate again.", "/test-data", "Open Test Data"),
        new("A Connected System is blocked", "Needs attention lists a blocker: missing execution credentials, missing Test Data, or execution safety not met.",
            "Fix blockers first, in the order shown; review items can wait.", TargetEnvironmentTab("external"), "Open Connected Systems"),
        new("Browser authentication is not observed", "No sign-in from this app was captured by Browser Discovery yet.",
            "Sign in to the app with the Browser Companion running. Observed sign-in is still not verification.", TargetEnvironmentTab("browser"), "Open Browser Discovery"),
        new("Unattended login: credential missing", "No login name and password are stored for the automation identity, or the stored credential cannot be read on this backend.",
            "Edit the unattended login and enter both the login name and the password, then validate.", "/admin/system-settings?section=authentication", "Open Authentication"),
        new("Unattended login: authentication rejected", "The login page refused the login name or password. BirkNext does not retry it, so the account is not locked.",
            "Check the credential of the automation identity, save it again and validate once.", "/admin/system-settings?section=authentication", "Open Authentication"),
        new("Unattended login: MFA detected", "The identity met an MFA, one-time code or conditional access challenge. Unattended login never answers it.",
            "Use attended login for this identity, or configure an automation identity that does not require MFA.", "/admin/system-settings?section=authentication", "Open Authentication"),
        new("Unattended login: target unavailable or success marker not found", "The login URL did not answer, or the URL text/marker that confirms a successful login did not appear.",
            "Check the login target and the success detection, then validate again.", "/admin/system-settings?section=authentication", "Open Authentication"),
        new("Unattended login: browser engine unavailable", "The automation browser could not start on this backend.",
            "Check the browser engine in Frontend Testing Capabilities.", "/admin/system-settings?section=frontend-quality-engines", "Open Frontend Testing Capabilities"),
        new("Performance readiness is blocked", "Target not reachable, TLS not trusted, a step lacks a runtime value, authentication or execution safety is missing.",
            "Open the Readiness tab and resolve the listed checks. Readiness never runs a load test.", "/performance-test-review?tab=readiness", "Open Performance Test Review"),
        new("TLS trust is missing", "The target uses a certificate the test container does not trust.",
            "Trust the certificate authority in the machine-local store shown in Performance Test Engines.", "/admin/system-settings?section=performance-test-engines&focus=container-tls-trust", "Open Performance Test Engines"),
        new("No baseline for comparison", "No run has been promoted to baseline yet.",
            "Promote a completed, comparable run to baseline in History. The latest run is never promoted automatically.", "/performance-test-review?tab=history", "Open Performance Test Review"),
        new("Build provenance is missing or Unknown build", "The build was made without the build-identity step (a CI run that skipped it, or a component built without the version targets), or the commit cannot be read.",
            "Open System Settings → General → Application information and the technical details. Use a package produced by the pipeline; a local build always says Local build and is never a release.", "/admin/system-settings", "Open System Settings"),
        new("Frontend and backend are different builds", "The browser loaded a frontend from another build than the backend it talks to (an old tab, a cached frontend or two packages mixed).",
            "Reload the page; if it persists, start the frontend and backend from the same package. Application information shows both builds.", "/admin/system-settings", "Open System Settings"),
        new("A result is labelled Historical", "A contract, test revision, Test Data version, endpoint binding or suite revision the run used has changed since. The result is still true for what it ran against.",
            "Open the run's Executed against panel to see which dependency moved, then run the current definitions to get a current result.", "/execution-results", "Open Test Execution & Evidence"),
        new("An import reports conflicts or needs revalidation", "The package holds objects that already exist with other content, or environment-bound objects that need a destination environment.",
            "Review each conflict in Backup & Transfer → Import, choose an environment explicitly, then revalidate the imported (disabled) systems and flows before using them.", "/workspace-transfer", "Open Backup & Transfer"),
        new("Generated documentation is unavailable", "The imported project has no generated documentation folders, or they were not captured.",
            "Generated documentation is optional. Re-import a package that contains it if you need Health or Drift.", "/generated-documentation-health", "Open Generated Documentation Health"),
    ];
}
