using System.Reflection;
using System.Text.RegularExpressions;
using BirkNext.Web.Pages;
using Microsoft.AspNetCore.Components;

namespace BirkNext.Web.Services;

/// <summary>Stable documentation identity and the guide anchor that explains a current product capability.</summary>
public sealed record DocumentationFeature(string FeatureId, string DisplayName, string Route, string UserGuideAnchor,
    string Category, bool Required = true, string? ProviderScope = null, string? ExtensionScope = null);
public sealed record DocumentationInventoryItem(string FeatureId, string DisplayName, string Route, string NavigationSection,
    string FeatureCategory, string ApplicableTo, IReadOnlyList<string> CapabilityDependencies, string VisibilityRule,
    bool ProviderSpecific, bool ExtensionSpecific, string? UserGuideSection, bool Documented, string CurrentStatus);
public sealed record DocumentationGuideDetail(string Purpose, string EvidenceAndInputs, string Boundary);
public sealed record SettingsNavItem(string Id, string Label, bool DeveloperOnly = false);
public sealed record SettingsNavGroup(string Label, IReadOnlyList<SettingsNavItem> Items);
public sealed record SourceAnalysisGroup(string Label, IReadOnlyList<string> Areas);
public sealed record SettingsTargetTab(string Key, string Label);
public sealed record DocumentedSetting(string Name, string Purpose, string Safety);
public sealed record DocumentedTargetTab(string Name, string Purpose);

/// <summary>Deterministic documentation metadata and drift checks. IDs are derived from canonical menu routes, never display labels.</summary>
public static class DocumentationCatalog
{
    // Documentation registry is intentionally explicit. A new NavigationCatalog row does not create its own docs entry.
    public static readonly IReadOnlyList<DocumentationFeature> Features = BuildFeatures();
    public static DocumentationGuideDetail DetailFor(string id) => GuideDetails.GetValueOrDefault(id) ??
        new("Open the feature for its project-specific result and current applicability.", "Evidence depends on the feature and selected project context.", "Availability does not prove applicability or a successful assessment.");
    public static IReadOnlyList<DocumentationFeature> CurrentNavigation => NavigationCatalog.Sections.SelectMany(s => s.Items.Select(i => (Section: s, Item: i)))
        .Select(x => new DocumentationFeature(x.Item.FeatureId ?? x.Item.Route.ToLowerInvariant(), x.Item.Label, "/" + x.Item.Route, "", x.Section.Label,
            Required: !x.Item.OptionalByDefault,
            ProviderScope: x.Item.ProviderWhenApplicable, ExtensionScope: x.Item.ReviewId is { } rid ? BirkNext.Technology.ReviewCatalog.Find(rid)?.DomainExtensionId : null)).ToArray();
    public static IReadOnlyList<DocumentationInventoryItem> Inventory => NavigationCatalog.Sections.SelectMany(section => section.Items.Select(item =>
    {
        var id = item.FeatureId ?? item.Route.ToLowerInvariant();
        var doc = Features.FirstOrDefault(f => f.FeatureId.Equals(id, StringComparison.OrdinalIgnoreCase));
        var review = item.ReviewId is { } reviewId ? BirkNext.Technology.ReviewCatalog.Find(reviewId) : null;
        return new DocumentationInventoryItem(id, item.Label, "/" + item.Route, section.Label, section.Label,
            review?.Purpose ?? "Project/workspace feature; applicability depends on available evidence and configuration.",
            review?.Requires.Select(capability => capability.ToString()).ToArray() ?? [],
            "Controlled by the NavigationCatalog visibility rule for this item.", item.ProviderWhenApplicable is not null,
            review?.DomainExtensionId is not null, doc?.UserGuideAnchor, doc is not null,
            review?.DomainExtensionId is not null ? "Extension-scoped" : item.ProviderWhenApplicable is not null ? "Provider-scoped" : "Current feature; project applicability evaluated at runtime");
    })).ToArray();
    public static readonly IReadOnlyList<SettingsNavGroup> SettingsGroups =
    [new("Configuration", [new("general","General"),new("config-health","Configuration Health"),new("feature-visibility","Feature Visibility"),new("platform","Platform")]),
     new("Analysis", [new("target-environments","Target Environments"),new("frontend-quality-engines","Frontend Engine Capabilities"),new("performance-test-engines","Performance Test Engines"),new("ai","AI")]),
     new("Developer", [new("environment-diagnostics","Environment Diagnostics"),new("diagnostics","System Diagnostics"),new("project-compatibility","Project Compatibility"),new("content-integrity","Content Integrity"),new("explorer-text-coverage","Explorer Text Coverage"),new("doc-health","Documentation Health"),new("review-context-validation","ReviewContext Validation",true),new("ui-migration","Runtime Diagnostics"),new("maintenance","Maintenance")])];
    public static IReadOnlyList<string> SettingsSections => SettingsGroups.SelectMany(g => g.Items).Select(i => i.Label).ToArray();
    // Documentation inventories intentionally remain independent from the live UI metadata above.
    public static readonly IReadOnlyList<DocumentedSetting> DocumentedSettings =
    [new("General","Platform-wide settings entry point for administrators.","Configuration is not project quality."),new("Configuration Health","Checks platform configuration readiness.","Does not assess a project."),new("Feature Visibility","Controls which navigation capabilities are shown.","Visibility is not support or applicability."),new("Platform","Installation/platform settings as exposed by the current host.","Changes can affect shared installation behavior."),new("Target Environments","Profiles for target characteristics, authentication, observations and expectations.","Configuration is distinct from verified behavior."),new("Frontend Engine Capabilities","Reports frontend review engine capability/readiness.","Host availability does not prove target coverage."),new("Performance Test Engines","Reports k6, Podman, image, workload and resource-provider readiness.","Capability is not a project test run."),new("AI","Controls optional AI configuration.","AI is not required for core deterministic reviews."),new("Environment Diagnostics","Admin/developer environment checks.","Diagnostic state is not a project finding."),new("System Diagnostics","Host-level system checks.","Not a project quality score."),new("Project Compatibility","Checks generated artifact/source discovery portability in isolated fixtures.","Does not import a project or change current workspace evidence."),new("Content Integrity","Compares generated ZIP Markdown with extracted content and parser completion.","Uses isolated fixtures; it is not a check of current project artifacts."),new("Explorer Text Coverage","Compares supported source blocks with production Explorer parser output.","Missing means the parser output did not account for the block; diagnostics never change selected artifact authority."),new("Documentation Health","Checks structured documentation and navigation consistency.","Does not use AI or prove prose quality."),new("ReviewContext Validation","Developer validation of shared review context semantics.","Not a review or approval."),new("Runtime Diagnostics","Investigates application runtime behavior.","Diagnostic output is not project quality."),new("Maintenance","Local maintenance operations, including reset where enabled.","Reset is irreversible; follow the exact scope confirmation.")];
    public static IReadOnlyList<string> DocumentedSettingsSections => DocumentedSettings.Select(s => s.Name).ToArray();
    public static readonly IReadOnlyList<SettingsTargetTab> TargetEnvironmentTabDefinitions =
    [new("general","General"),new("target","Target Application"),new("auth","Authentication"),new("endpoint","Endpoint Discovery"),new("browser","Browser Discovery"),new("performance","Performance Thresholds"),new("cwv","Core Web Vitals"),new("security","Security Expectations"),new("features","Frontend Review Engines"),new("validation","Validation"),new("integrations","Integrations")];
    public static IReadOnlyList<string> TargetEnvironmentTabs => TargetEnvironmentTabDefinitions.Select(t => t.Label).ToArray();
    public static readonly IReadOnlyList<DocumentedTargetTab> DocumentedTargetTabs =
    [new("General","Identifies the environment profile and its general configuration."),new("Target Application","Configured or detected target application characteristics."),new("Authentication","Authentication configuration, its verification state and the verification prerequisites (HTTPS inspection certificate, Local HTTPS Proxy, dedicated Edge browser)."),new("Endpoint Discovery","Observed network/API endpoint evidence."),new("Browser Discovery","Browser Companion session and raw browser evidence."),new("Performance Thresholds","Explicit performance expectations for target-facing reviews."),new("Core Web Vitals","Frontend Core Web Vitals thresholds."),new("Security Expectations","Approved environment/component security expectations and source consistency evidence."),new("Frontend Review Engines","Per-environment frontend engine activation and policy."),new("Validation","Configuration validation findings."),new("Integrations","Configured integration source of truth consumed by Integration Quality Review.")];
    public static IReadOnlyList<string> DocumentedTargetEnvironmentTabs => DocumentedTargetTabs.Select(t => t.Name).ToArray();
    public static readonly IReadOnlyList<SourceAnalysisGroup> SourceAnalysisGroups =
    [new("Core analysis", ["Overview", "Architecture", "Database", "Observability"]),new("Source evidence", ["Infrastructure", "Configuration", "CI/CD", "Contracts"])];
    public static IReadOnlyList<string> SourceAnalysisDomains => SourceAnalysisGroups.SelectMany(g => g.Areas).ToArray();
    public static readonly IReadOnlyList<string> DocumentedSourceAnalysisDomains =
    ["Overview", "Architecture", "Database", "Observability", "Infrastructure", "Configuration", "CI/CD", "Contracts"];

    private static readonly IReadOnlyDictionary<string, DocumentationGuideDetail> GuideDetails = new Dictionary<string, DocumentationGuideDetail>(StringComparer.OrdinalIgnoreCase)
    {
        ["getting-started"] = new("Shows the three project inputs (documents, source, target) and the next step for them.", "Current workspace documents, the active environment's latest source snapshot, the active Target Environment, review applicability and recorded review state.", "A recommendation is not a required release sequence or approval."),
        ["user-guide"] = new("Reference for current feature ownership and evidence boundaries.", "Product navigation and documented capability metadata.", "Structured health checks cannot prove prose accuracy."),
        ["dashboard"] = new("Aggregates assessed project areas and recent workspace activity.", "Current project and stored review/evidence summaries.", "Unsupported/N/A do not lower quality; coverage differs from quality."),
        ["specification-explorer"] = new("Inspects requirements and specification structure.", "Specification artifact and supported project IDs.", "A requirement or designed test does not prove implementation or execution."),
        ["constitution-explorer"] = new("Presents project rules, principles and standards.", "Constitution artifact.", "Reading rules is not a compliance assessment."),
        ["data-model-explorer"] = new("Inspects declared data entities and relationships.", "Data model artifact.", "Declared schema is not deployed database state."),
        ["plan-explorer"] = new("Presents plan decisions, phases, gates and risks.", "Plan artifact.", "A normalized phase key is not necessarily a unique declaration."),
        ["task-explorer"] = new("Presents work items and requirement relationships.", "Tasks artifact and shared graph evidence.", "A task link does not prove code completion."),
        ["project-import"] = new("Imports one project ZIP once: documents become project artifacts and source becomes a Source Analysis snapshot, linked by one import identity.", "One uploaded ZIP, validated once by the Source Analysis archive reader; documents classified by role, source analyzed into the active Target Environment.", "One import is not one evidence model and not a review: artifacts and source keep separate owners, and imported is not approved."),
        ["sample-projects"] = new("Discovers sample documents recursively, classifies them by artifact role and selects the project context.", "Configured local sample projects (any folder structure and filenames).", "Samples are examples; roles are optional and a missing role is not a project failure; loading does not make every review applicable."),
        ["target-environments"] = new("Shortcut to System Settings › Target Environments: the runtime input (application URL, authentication, endpoints, integrations).", "Target Environment profiles stored for this browser; source snapshots are also kept per environment.", "A configured target enables runtime reviews; it is not a test result and is not needed for document-only work."),
        ["source-analysis"] = new("Creates reusable normalized source evidence.", "Uploaded source archive analyzed into immutable fingerprinted snapshots.", "Source evidence does not prove runtime or deployment behavior."),
        ["technology-coverage"] = new("Shows detected technology and support by capability dimension.", "Source inventory and analyzer/provider support metadata.", "Unsupported is a tool limitation, not a project quality failure."),
        ["environment-analysis"] = new("Reads environment inventory through an available provider.", "Configured provider; current page uses Azure.", "Provider available/detected does not mean BirkNext is Azure-only or the project uses Azure."),
        ["quality/document"] = new("Reviews quality, governance, readiness and standards documentation coverage in project artifacts.", "Shared project artifact repository and SDD/document evidence.", "Source implementation and runtime behavior are outside this review."),
        ["traceability"] = new("Presents requirements, plans, tasks, implementation and test evidence links.", "Loaded artifacts, CodeLinks, source provenance and optional execution records.", "Designed tests differ from executed tests; links are not runtime verification."),
        ["artifact-traceability"] = new("Analyzes cross-artifact requirement coverage.", "Requirement, plan and task artifacts.", "A link graph is evidence of references, not implementation correctness."),
        ["task-alignment"] = new("Checks each task for a reference that resolves to the Specification and suggests test focus.", "Specification and Task artifact, bound by fingerprint.", "A direct spec link is not coverage; no source code is read, so this is not implementation verification."),
        ["implementation-traceability"] = new("Presents work-item-to-change traceability evidence.", "Configured work-item/provider metadata and linked change evidence.", "Presence of a link does not verify the change beyond displayed provenance."),
        ["quality-review"] = new("Reviews project documentation and SDD artifacts through selected quality, governance, documentation-standards and readiness packs.", "Project artifacts in the shared artifact repository; no source snapshot is required.", "Does not assess source implementation or runtime behavior. Standards keyword results describe documentation coverage, not conformance or legal compliance."),
        ["frontend-quality-review"] = new("Interprets frontend quality evidence across configured review domains.", "Target profile, source/configuration and collected browser/runtime evidence.", "Browser evidence is bounded and cannot prove all behavior."),
        ["api-quality-review"] = new("Reviews configured REST/GraphQL API targets and safe query behavior.", "Target endpoints, contracts, auth context and thresholds.", "Mutating operations are gated; do not infer write testing."),
        ["integration-quality-review"] = new("Reviews configured integrations using available source/runtime evidence.", "Integration registry, selected source snapshots and observed evidence.", "Configured is not observed; listed types have unequal support."),
        ["performance-test-review"] = new("Defines and runs controlled performance workloads and comparisons.", "Scenario, workload, thresholds, engine readiness and selected target.", "Latest run is not baseline automatically; comparisons require comparability."),
        ["security-classification-review"] = new("Runs project-specific child security classification checks.", "M2LB extension and its supported project evidence.", "Extension-specific; not a generic security review or runtime verification."),
        ["dependency-review"] = new("Reviews supported dependency inventory/SBOM evidence.", "Source Analysis inventory and supported ecosystem/source mode.", "Unsupported package ecosystems remain tool limitations."),
        ["pipeline-review"] = new("Interprets CI/CD definitions and pipeline structure.", "CI/CD evidence from the selected Source Analysis snapshot.", "A definition does not prove execution, success or deployment."),
        ["critical-e2e-regression"] = new("Runs configured safety-gated critical browser journeys.", "Configured journeys, target and browser/runtime readiness.", "A journey covers only its recorded steps and cannot prove full correctness."),
        ["admin/system-settings"] = new("Configures platform, targets, capabilities and diagnostics.", "Host settings and Target Environment profiles.", "Visibility/configuration is not support, applicability or verification."),
        ["ai-change-auditor"] = new("Optionally reviews AI-generated changes when enabled.", "AI configuration and submitted change context.", "Optional; not a core workflow and not a substitute for deterministic evidence.")
    };

    public static IReadOnlySet<string> DiscoverRoutes(Assembly assembly) => assembly.GetTypes()
        .SelectMany(t => t.GetCustomAttributes<RouteAttribute>().Select(a => NormalizeRoute(a.Template)))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The page route: a deep link's query string or fragment (e.g. a System Settings section) is not part of the route.</summary>
    public static string NormalizeRoute(string route)
    {
        var path = route.Trim();
        var cut = path.IndexOfAny(['?', '#']);
        return "/" + (cut >= 0 ? path[..cut] : path).TrimStart('/').TrimEnd('/');
    }

    public static IReadOnlyList<string>? ReadGuideSource(Assembly assembly)
    {
        using var stream = assembly.GetManifestResourceStream("BirkNext.UserGuide.source");
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return [reader.ReadToEnd()];
    }

    public static IReadOnlyList<DocumentationFinding> Check(
        IEnumerable<DocumentationFeature> features,
        IEnumerable<DocumentationFeature> documented,
        IEnumerable<string> routes,
        IEnumerable<string> anchors,
        IEnumerable<string>? settingsDocs = null,
        IEnumerable<string>? targetTabDocs = null,
        IEnumerable<string>? sourceDomainDocs = null,
        IEnumerable<string>? guideText = null)
    {
        var findings = new List<DocumentationFinding>();
        var current = features.ToArray();
        var docs = documented.ToArray();
        foreach (var duplicate in docs.GroupBy(f => f.FeatureId, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            findings.Add(new("Fail", "Registry", duplicate.Key, null, null, $"Documentation FeatureId '{duplicate.Key}' is duplicated.", "Keep one documentation entry per stable ID."));
        var routeSet = routes.Select(NormalizeRoute).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var source = guideText is null ? null : string.Join("\n", guideText);
        var anchorValues = source is null ? anchors.ToArray() : Regex.Matches(source, "\\bid=\\\"([^\\\"]+)\\\"")
            .Select(m => m.Groups[1].Value).ToArray();
        var anchorSet = anchorValues.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var duplicate in anchorValues.GroupBy(a => a, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            findings.Add(new("Fail", "Anchors", null, null, duplicate.Key, $"User Guide anchor '{duplicate.Key}' is duplicated.", "Assign a unique explicit section ID."));
        foreach (var duplicate in current.GroupBy(f => f.FeatureId, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            findings.Add(new("Fail", "Registry", duplicate.Key, null, null, $"FeatureId '{duplicate.Key}' is duplicated.", "Give each feature one stable ID."));
        foreach (var feature in current)
        {
            var initialFindingCount = findings.Count;
            if (!routeSet.Contains(NormalizeRoute(feature.Route)))
                findings.Add(new("Fail", "Routes", feature.FeatureId, feature.Route, feature.UserGuideAnchor, $"{feature.DisplayName} points to a route that does not exist.", "Update the canonical route metadata."));
            var entry = docs.FirstOrDefault(d => d.FeatureId.Equals(feature.FeatureId, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
                findings.Add(new("Fail", "Feature coverage", feature.FeatureId, feature.Route, feature.UserGuideAnchor, $"{feature.DisplayName} has no User Guide entry.", "Add the feature to the documentation registry and guide."));
            else
            {
                if (!routeSet.Contains(NormalizeRoute(entry.Route))) findings.Add(new("Fail", "Routes", feature.FeatureId, entry.Route, entry.UserGuideAnchor, "The documentation entry points to a route that does not exist.", "Correct the documented route."));
                if (!feature.Route.Equals(entry.Route, StringComparison.OrdinalIgnoreCase)) findings.Add(new("Fail", "Route mismatch", feature.FeatureId, entry.Route, entry.UserGuideAnchor, $"Documentation route differs from the current route {feature.Route}.", "Update the documentation route."));
                if (!anchorSet.Contains(entry.UserGuideAnchor)) findings.Add(new("Fail", "Anchors", feature.FeatureId, entry.Route, entry.UserGuideAnchor, "The User Guide section anchor is missing.", "Add the section or correct its anchor."));
                if (feature.ProviderScope is not null && entry.ProviderScope is null) findings.Add(new("Warning", "Provider scope", feature.FeatureId, entry.Route, entry.UserGuideAnchor, "Provider-specific capability is not identified as provider-specific in documentation metadata.", "Mark the provider scope in the documentation registry."));
                if (feature.ExtensionScope is not null && entry.ExtensionScope is null) findings.Add(new("Warning", "Extension scope", feature.FeatureId, entry.Route, entry.UserGuideAnchor, "Extension capability is not identified as extension-specific in documentation metadata.", "Mark the extension scope in the documentation registry."));
                if (feature.ProviderScope is null && entry.ProviderScope is not null) findings.Add(new("Warning", "Provider scope", feature.FeatureId, entry.Route, entry.UserGuideAnchor, "Documentation describes a provider-specific capability, but current feature metadata has no provider scope.", "Review whether the provider is optional and scoped."));
                if (feature.ExtensionScope is null && entry.ExtensionScope is not null) findings.Add(new("Warning", "Extension scope", feature.FeatureId, entry.Route, entry.UserGuideAnchor, "Documentation marks an extension, but current feature metadata does not identify one.", "Verify extension metadata against the current review registry."));
                if (feature.Required != entry.Required) findings.Add(new("Warning", "Feature visibility", feature.FeatureId, entry.Route, entry.UserGuideAnchor, "Documentation optionality does not match current feature visibility metadata.", "Qualify hidden-by-default features as optional in the guide."));
            }
            if (findings.Count == initialFindingCount)
                findings.Add(new("Pass", "Feature coverage", feature.FeatureId, feature.Route, feature.UserGuideAnchor, $"{feature.DisplayName} has a current route and User Guide entry.", ""));
        }
        if (source is not null)
        {
            foreach (Match link in Regex.Matches(source, "href=\\\"(/[^\\\"#?]+)\\\""))
            {
                var href = link.Groups[1].Value;
                if (!routeSet.Contains(NormalizeRoute(href))) findings.Add(new("Fail", "Cross-links", null, href, null, $"User Guide links to missing route '{href}'.", "Correct the link or register the route."));
            }
            foreach (Match link in Regex.Matches(source, "href=\\\"#([^\\\"]+)\\\""))
            {
                var target = link.Groups[1].Value;
                if (!target.StartsWith('@') && !anchorSet.Contains(target)) findings.Add(new("Fail", "Anchors", null, null, target, $"User Guide links to missing anchor '{target}'.", "Correct the link or add the section ID."));
            }
        }
        foreach (var orphan in docs.Where(d => current.All(f => !f.FeatureId.Equals(d.FeatureId, StringComparison.OrdinalIgnoreCase))))
            findings.Add(new("Warning", "Orphan documentation", orphan.FeatureId, orphan.Route, orphan.UserGuideAnchor, $"{orphan.DisplayName} is documented but is not a current registered feature.", "Remove or mark this entry as historical."));
        CheckCoverage(SettingsSections, settingsDocs, "System Settings", findings);
        CheckCoverage(TargetEnvironmentTabs, targetTabDocs, "Target Environment tabs", findings);
        CheckCoverage(SourceAnalysisDomains, sourceDomainDocs, "Source Analysis domains", findings);
        foreach (var legacy in new[] { "QA Auditor", "Constitution Compliance", "Standards Compliance", "QA Readiness", "Delivery Readiness", "Azure Environment Analysis" })
            if (guideText?.Any(text => text.Contains(legacy, StringComparison.OrdinalIgnoreCase)) == true)
                findings.Add(new("Warning", "Legacy terminology", null, null, null, $"'{legacy}' appears in current guide text.", "Remove the stale current-product reference or label it as migration history."));
        return findings;
    }

    private static void CheckCoverage(IEnumerable<string> expected, IEnumerable<string>? actual, string category, ICollection<DocumentationFinding> findings)
    {
        var actualSet = (actual ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in expected)
            findings.Add(actualSet.Contains(item)
                ? new("Pass", category, null, null, null, $"{item} has a documentation entry.", "")
                : new("Fail", category, null, null, null, $"{item} has no documentation entry.", $"Document {item} in the User Guide."));
    }

    private static IReadOnlyList<DocumentationFeature> BuildFeatures()
    {
        var definitions = new (string Id, string Name, string Route, string Anchor, string Category, string? Provider, string? Extension)[]
        {
            ("getting-started","Recommended Workflow","/getting-started","getting-started","Getting Started",null,null),
            ("user-guide","User Guide","/user-guide","overview","Getting Started",null,null),
            ("dashboard","Dashboard","/dashboard","feature-matrix","Getting Started",null,null),
            ("project-import","Import Project","/project-import","project-import","Project Inputs",null,null),
            ("sample-projects","Sample Projects","/sample-projects","project-inputs","Project Inputs",null,null),
            ("source-analysis","Source Analysis","/source-analysis","source-analysis","Project Inputs",null,null),
            ("target-environments","Target Environments","/admin/system-settings?section=target-environments","project-inputs","Project Inputs",null,null),
            ("specification-explorer","Specification Explorer","/specification-explorer","getting-started","Document Review",null,null),
            ("constitution-explorer","Constitution Explorer","/constitution-explorer","getting-started","Document Review",null,null),
            ("data-model-explorer","Data Model Explorer","/data-model-explorer","getting-started","Document Review",null,null),
            ("plan-explorer","Plan Explorer","/plan-explorer","getting-started","Document Review",null,null),
            ("task-explorer","Task Explorer","/task-explorer","getting-started","Document Review",null,null),
            ("artifact-traceability","Requirements Traceability","/artifact-traceability","traceability","Traceability",null,null),
            ("task-alignment","Implementation Review","/task-alignment","traceability","Traceability",null,null),
            ("implementation-traceability","Implementation Traceability","/implementation-traceability","traceability","Traceability",null,null),
            ("spec-drift","Spec Drift","/spec-drift","traceability","Traceability",null,null),
            ("impact-analysis","Impact Analysis","/impact-analysis","traceability","Traceability",null,null),
            ("traceability","Traceability & Coverage","/traceability","traceability","Traceability",null,null),
            ("traceability/suggestions","Traceability Suggestions","/traceability/suggestions","traceability","Traceability",null,null),
            ("code-traceability","Code Traceability","/code-traceability","traceability","Traceability",null,null),
            ("technology-coverage","Technology Coverage","/technology-coverage","technology-coverage","Source Review",null,null),
            ("dependency-review","Dependency Review","/dependency-review","quality-reviews","Source Review",null,null),
            ("pipeline-review","Pipeline Review","/pipeline-review","pipeline","Source Review",null,null),
            ("environment-analysis","Environment Analysis","/azure-environment","environment-analysis","Source Review","Azure",null),
            ("quality/document","Document Quality Review","/quality/document","quality-reviews","Document Review",null,null),
            ("frontend-quality-review","Frontend Quality Review","/frontend-quality-review","quality-reviews","Quality & Testing",null,null),
            ("api-quality-review","API Quality Review","/api-quality-review","quality-reviews","Quality & Testing",null,null),
            ("integration-quality-review","Integration Quality Review","/integration-quality-review","quality-reviews","Quality & Testing",null,null),
            ("performance-test-review","Performance Test Review","/performance-test-review","testing","Quality & Testing",null,null),
            ("critical-e2e-regression","Critical E2E Regression","/critical-e2e-regression","testing","Quality & Testing",null,null),
            ("security-classification-review","Security Classification","/security-classification-review","quality-reviews","Extensions",null,"M2LB child-security-classification"),
            ("ai-change-auditor","AI Change Review","/ai-change-auditor","optional-capabilities","Optional",null,null),
            ("admin/system-settings","System Settings","/admin/system-settings","system-settings","Admin",null,null)
        };
        var optional = new HashSet<string>(["traceability", "traceability/suggestions", "code-traceability", "ai-change-auditor"], StringComparer.OrdinalIgnoreCase);
        return definitions.Select(d => new DocumentationFeature(d.Id,d.Name,d.Route,d.Anchor,d.Category,Required: !optional.Contains(d.Id),ProviderScope:d.Provider,ExtensionScope:d.Extension)).ToArray();
    }
}

public sealed record DocumentationFinding(string Severity, string Category, string? FeatureId, string? Route,
    string? DocumentSection, string Message, string RecommendedFix);
