using System.Text.RegularExpressions;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public static class TaskSpecAlignmentService
{
    private sealed record SpecItem(string Id, string Title, SpecItemKind Kind);
    private sealed record ParsedTask(string TaskId, string Title, string RawText, IReadOnlyList<string> ReferencedIds);
    private enum SpecItemKind { Requirement, UserStory, SuccessCriterion }

    private static readonly char[] WordSplitChars = [' ', '\t', '-', '_', '.', '/', '\\', '(', ')', '[', ']'];
    private const StringComparison OIC = StringComparison.OrdinalIgnoreCase;
    private const StringSplitOptions NONE = StringSplitOptions.RemoveEmptyEntries;

    // ── Technical-only terms ─────────────────────────────────────────────────
    // Tasks matching these (with no behavioural terms) are Technical Only
    // regardless of spec keyword score — prevents false Linked classification.
    private static readonly string[] TechnicalOnlyTerms =
    [
        "nuget", "npm", "yarn", "add package", "package reference", "csproj",
        "project init", "scaffold new", "boilerplate",
        "build script", "ci/cd", "pipeline yaml", "dockerfile", "podman",
        "refactor ", "rename ", "move class", "clean up", "cleanup",
        "serilog setup", "logging infrastructure", "configure logging", "logging setup",
        "ef migration", "database migration", "migration scaffold",
        "test infrastructure", "test helper ", "test util", "test project setup",
        "bump version", "upgrade sdk", "update packages",
        "readme", "documentation comment",
        "program.cs", "appsettings.json", "appsettings", "di registration",
        "error handling middleware", "exception filter setup",
        "create solution", "create project", "new solution", "solution structure",
        "build setup", "project setup", "configure packages", "configure nuget",
        "styling ", " styling", "colour scheme", "color scheme", ".css ",
        "connection string", "connectionstring",
        "github actions", "azure devops", "ci pipeline",
    ];

    // ── Important infrastructure configuration ───────────────────────────────
    // Technical tasks that configure significant external dependencies.
    // These are still TechnicalOnly but warrant Medium Risk (not Low Risk).
    private static readonly string[] InfrastructureConfigTerms =
    [
        "servicebus", "service bus", "azure service bus",
        "auth url", "auth base url", "authority", "audience", "issuer",
        "microsoft graph", "graph api", "msgraph",
        "oauth", "openidconnect", "openid connect",
        "database connection", "sql connection", "redis connection",
        "api key", "client secret", "client id",
        "keyvault", "key vault", "certificate thumbprint",
        "msal", "entra id", "azure ad", "tenant id",
    ];

    // ── Behavioural terms ────────────────────────────────────────────────────
    // Tasks matching these introduce externally visible behaviour.
    // "service bus" / "message queue" are intentionally excluded here — they
    // appear in both config and behavioural contexts; infrastructure config tasks
    // are caught by the TechnicalOnly early-exit path before behavioural scoring.
    private static readonly string[] BehavioralTerms =
    [
        "endpoint", "api route", "rest api", "http get", "http post", "http put",
        "graphql query", "graphql mutation", "graphql subscription",
        "publish event", "event publisher", "event consumer", "event handler",
        "message consumer", "message handler",
        "authorization policy", "permission check", "access control rule",
        "validation rule", "business rule", "business logic",
        "user flow", "user journey", "user can",
        "database entity", "data model", "schema migration",
        "ui screen", "ui page", "ui form", "ui component", "ui action",
        "webhook", "external integration",
        "register operation", "operation type",
        "audit log", "audit trail",
        "mask field", "hide sensitive",
        "kode 6", "kode 7", "kode6", "kode7",
    ];

    // ── Area detection ────────────────────────────────────────────────────────
    private static readonly (AffectedArea Area, string[] Keywords)[] AreaDefs =
    [
        (AffectedArea.Security, ["security", "kode6", "kode7", "kode 6", "kode 7", "classification",
            "sensitive", "gradert", "gradertilgang", "visibility rule", "securityclassification",
            "krevergradert", "invisibl"]),
        (AffectedArea.Authorization, ["permission", "access control", "role", "grant", "tilgang",
            "autorisasjon", "policy", "authorize", "forbidden", "unauthorized", "authorization"]),
        (AffectedArea.Search, ["search", "søk", "filter", "pagination", "lookup", "findperson",
            "personsearch", "searchperson"]),
        (AffectedArea.Profile, ["profile", "profil", "persondata", "identitet", "national id",
            "fnr", "dnr", "fodselsnummer", "fødselsnummer", "personprofile", "personinfo", "mask"]),
        (AffectedArea.AccessManagement, ["access management", "grant access", "expiry", "self-assign",
            "tildeling", "tilgangstyring", "oppfølger", "selfassign"]),
        (AffectedArea.ReferenceData, ["reference data", "referansedata", "koderegister", "koder",
            "active values", "deactivated", "historikk", "referencecode", "referencevalue",
            "kjoenntype", "kjønntype", "kjoenn", "kjønn",
            "barntype", "barnstatus", "barnstatustype", "barntypekode",
            "kodetype", "kodeverk", "kodeverdi", "kodegruppe", "enumvalue"]),
        (AffectedArea.Ingestion, ["ingest", "upsert", "duf", "freg", "idempotent", "importbatch",
            "syncperson", "ingestion"]),
        (AffectedArea.DomainEvents, ["domain event", "publish event", "hendelse", "event bus",
            "eventbus", "event consumer", "event handler", "event publisher", "domainevent"]),
        (AffectedArea.Audit, ["audit", "auditlog", "audit trail", "immutable", "sporbarhet"]),
        (AffectedArea.OperationRegistration, ["operation registration", "register operation",
            "seven operations", "operasjonsregistrering", "operationregist"]),
        (AffectedArea.HealthMonitoring, ["health check", "healthcheck", "metric", "monitor",
            "dashboard", "availability", "helsesjekk", "readiness", "liveness"]),
        (AffectedArea.Infrastructure, ["infrastructure", "scaffold", "config", "di registration",
            "middleware", "logging", "test infra", "program.cs", "appsettings",
            "solution", "project setup", "build", "packages", "nuget", "connection string"]),
        (AffectedArea.BusinessRules, [
            "business rule", "businessrule", "statusregel", "forretningsregel",
            "statustransition", "state transition", "statetransition", "statusovergang",
            "transitionservice", "transitionhandler", "transitionrule",
            "eligibility", "calculation rule", "derivation", "rule engine", "domain rule"]),
        (AffectedArea.Workflow, [
            "workflow", "arbeidsflyt", "prosessflyt",
            "workflowstep", "processstep", "orchestrat",
            "statemachine", "state machine", "workflowservice", "processflow"]),
        (AffectedArea.Validation, [
            "validation", "validator", "valider", "validering",
            "fluentvalidation", "fluent validation", "input validation",
            "schema validation", "domain validation", "validationservice"]),
        (AffectedArea.Testing, [
            "tests", "testclass", "test class", "testfixture", "test fixture",
            "unittest", "unit test", "integrationtest", "integration test",
            "testbuilder", "testfactory", "test factory", "testdata", "mockservice"]),
        (AffectedArea.ExceptionHandling, [
            "exception", "exceptionhandler", "exception handler",
            "notfoundexception", "validationexception", "domainexception",
            "errorhandler", "error handler", "problem details",
            "errorresponse", "error response", "globalexception"]),
    ];

    // ── Impact level per area ─────────────────────────────────────────────────
    private static ImpactLevel AreaImpact(AffectedArea a) => a switch
    {
        AffectedArea.Security
            or AffectedArea.Authorization
            or AffectedArea.DomainEvents
            or AffectedArea.Audit
            or AffectedArea.OperationRegistration => ImpactLevel.High,
        AffectedArea.Search
            or AffectedArea.Profile
            or AffectedArea.AccessManagement
            or AffectedArea.ReferenceData
            or AffectedArea.Ingestion
            or AffectedArea.HealthMonitoring
            or AffectedArea.BusinessRules
            or AffectedArea.Workflow => ImpactLevel.Medium,
        _ => ImpactLevel.Low,
    };

    // ── Recommended tests by area ─────────────────────────────────────────────
    private static readonly Dictionary<AffectedArea, string[]> AreaTests = new()
    {
        [AffectedArea.Security] =
        [
            "Kode 6/7 visibility regression — names must not appear in results",
            "Unauthorized search/profile access negative test",
            "Permission boundary test — correct 403 responses",
        ],
        [AffectedArea.Authorization] =
        [
            "Grant access validation",
            "Expiry date enforcement",
            "Self-assignment rejection",
            "Audit trail for grants",
        ],
        [AffectedArea.Search] =
        [
            "Search by name",
            "Search by national ID",
            "Filter combination coverage",
            "Pagination correctness",
            "Response-time check",
        ],
        [AffectedArea.Profile] =
        [
            "Profile field visibility per classification",
            "National ID masking for Kode 6/7",
            "Status history display",
        ],
        [AffectedArea.AccessManagement] =
        [
            "Grant access flow end-to-end",
            "Expiry date enforced at access time",
            "Self-assignment rejection",
            "Audit trail for grants",
        ],
        [AffectedArea.ReferenceData] =
        [
            "Active reference values returned",
            "Deactivated values excluded from active lists",
            "Historical records display old values",
        ],
        [AffectedArea.Ingestion] =
        [
            "Idempotent upsert — duplicate ingestion is safe",
            "Invalid or incomplete records handled gracefully",
            "DUF to fødselsnummer upgrade flow",
            "Metrics validation after ingestion",
        ],
        [AffectedArea.DomainEvents] =
        [
            "Event published with correct payload schema",
            "No personal data (PII) in event payload",
            "Idempotent consumer behaviour",
            "Session ordering preserved",
        ],
        [AffectedArea.Audit] =
        [
            "Audit event published on state change",
            "No local audit table — events go to shared trail",
            "Immutable audit trail integration test",
        ],
        [AffectedArea.OperationRegistration] =
        [
            "Exactly seven operations registered",
            "Health endpoint waits for registration completion",
        ],
        [AffectedArea.HealthMonitoring] =
        [
            "Health check endpoint responds 200 OK",
            "Metrics collection active",
            "Dashboard visibility",
        ],
        [AffectedArea.Infrastructure] =
        [
            "Application starts with expected configuration",
            "Health endpoint reports dependency status",
            "Missing or invalid configuration fails safely at startup",
            "No secrets or sensitive data in application logs",
            "All required service connections succeed",
        ],
        [AffectedArea.BusinessRules] =
        [
            "Business rule produces expected output for known scenarios",
            "Edge cases and boundary conditions covered",
            "Invalid transition or rule violation is rejected correctly",
            "Regression test for any changed calculation or transition",
        ],
        [AffectedArea.Workflow] =
        [
            "State transition sequence is correct",
            "Invalid transitions are rejected",
            "Workflow completes end-to-end for valid input",
            "Concurrent state changes handled safely",
        ],
        [AffectedArea.Validation] =
        [
            "Valid input is accepted without error",
            "Invalid input is rejected with correct error message",
            "Boundary values handled correctly",
            "Required fields enforced",
        ],
        [AffectedArea.Testing] =
        [
            "All test cases compile and run successfully",
            "Test coverage is adequate for the component under test",
        ],
        [AffectedArea.ExceptionHandling] =
        [
            "Exception thrown in expected error scenario",
            "Exception carries correct message and error code",
            "Global handler catches and formats response correctly",
            "No sensitive data exposed in error responses",
        ],
    };

    // ── Regexes ───────────────────────────────────────────────────────────────
    private static readonly Regex SpecIdRe = new(
        @"\b(FR|NFR|US|SC|AC|UC|TS|REQ)-?(\d{1,4})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TaskLineRe = new(
        @"^T(\d{2,4})\s*[-–.:)]\s*(.+)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex CheckboxRe = new(
        @"^\s*[-*]\s+\[[ xX]\]\s+(.+)$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex CamelCaseRe = new(
        @"\b([A-Z][a-z]+(?:[A-Z][a-z]+)+)\b",
        RegexOptions.Compiled);

    // ── Public API ────────────────────────────────────────────────────────────

    public static AlignmentReport Analyse(string specText, string tasksText)
    {
        var specItems = ParseSpec(specText);
        var specKeywords = ExtractSpecKeywords(specText);
        var tasks = ParseTasks(tasksText);

        var findings = tasks
            .Select(t => ClassifyTask(t, specItems, specKeywords))
            .ToList();

        return new AlignmentReport
        {
            TotalTasks = findings.Count,
            LinkedTasks = findings.Count(f => f.Status == AlignmentStatus.Linked),
            TechnicalOnlyTasks = findings.Count(f => f.Status == AlignmentStatus.TechnicalOnly),
            NeedsReviewTasks = findings.Count(f => f.Status == AlignmentStatus.NeedsReview),
            PossibleDeviations = findings.Count(f => f.Status == AlignmentStatus.PossibleDeviation),
            HighImpactTasks = findings.Count(f => f.ImpactLevel == ImpactLevel.High),
            MediumImpactTasks = findings.Count(f => f.ImpactLevel == ImpactLevel.Medium),
            LowImpactTasks = findings.Count(f => f.ImpactLevel == ImpactLevel.Low),
            UnknownImpactTasks = findings.Count(f => f.ImpactLevel == ImpactLevel.Unknown),
            RegressionCandidates = findings.Count(f => f.IsRegressionCandidate),
            Findings = findings,
        };
    }

    // ── Spec parsing ──────────────────────────────────────────────────────────

    private static List<SpecItem> ParseSpec(string specText)
    {
        var items = new List<SpecItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (pattern, kind) in new (string, SpecItemKind)[]
        {
            (@"\b(FR|NFR|REQ)-?(\d{1,4})\b([^\n]*)", SpecItemKind.Requirement),
            (@"\b(US|UC)-?(\d{1,4})\b([^\n]*)", SpecItemKind.UserStory),
            (@"\b(SC|AC)-?(\d{1,4})\b([^\n]*)", SpecItemKind.SuccessCriterion),
        })
        {
            foreach (Match m in Regex.Matches(specText, pattern, RegexOptions.IgnoreCase))
            {
                var id = $"{m.Groups[1].Value.ToUpperInvariant()}-{m.Groups[2].Value.PadLeft(2, '0')}";
                if (!seen.Add(id)) continue;
                var ctx = m.Groups[3].Value.Trim(' ', ':');
                var raw = string.IsNullOrEmpty(ctx) ? id : $"{id} {ctx}";
                items.Add(new SpecItem(id, raw[..Math.Min(120, raw.Length)], kind));
            }
        }

        return items;
    }

    private static HashSet<string> ExtractSpecKeywords(string specText)
    {
        var kw = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Markdown headings
        foreach (Match m in Regex.Matches(specText, @"^#{1,6}\s+(.+)$", RegexOptions.Multiline))
            foreach (var word in m.Groups[1].Value.Split(WordSplitChars, NONE))
            {
                var w = word.Trim('*', '`', '#', '\r', '.', ':');
                if (w.Length >= 6) kw.Add(w);
            }

        // Bold text
        foreach (Match m in Regex.Matches(specText, @"\*\*([^*\n]{2,60})\*\*"))
        {
            kw.Add(m.Groups[1].Value.Trim());
            foreach (var word in m.Groups[1].Value.Split(' ', NONE))
            {
                var w = word.Trim('`', '.', ':');
                if (w.Length >= 6) kw.Add(w);
            }
        }

        // Inline code spans
        foreach (Match m in Regex.Matches(specText, @"`([^`\n]{2,80})`"))
            kw.Add(m.Groups[1].Value.Trim());

        // CamelCase identifiers from entire spec
        foreach (Match m in CamelCaseRe.Matches(specText))
        {
            kw.Add(m.Value);
            foreach (var part in SplitCamelCase(m.Value))
                if (part.Length >= 5) kw.Add(part);
        }

        // Significant words from requirement lines
        foreach (Match m in Regex.Matches(specText, @"\b(?:FR|US|NFR|SC|AC|REQ)-?\d+[^\n]*", RegexOptions.IgnoreCase))
            foreach (var word in m.Value.Split(' ', NONE))
            {
                var w = word.Trim('.', ':', ',', ';', '(', ')');
                if (w.Length >= 6 && !Regex.IsMatch(w, @"^\d+$")) kw.Add(w);
            }

        return kw;
    }

    // ── Task parsing ──────────────────────────────────────────────────────────

    private static List<ParsedTask> ParseTasks(string tasksText)
    {
        var tasks = new List<ParsedTask>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in TaskLineRe.Matches(tasksText))
        {
            var id = $"T{m.Groups[1].Value.PadLeft(3, '0')}";
            if (!seen.Add(id)) continue;
            tasks.Add(new ParsedTask(id, m.Groups[2].Value.Trim(), m.Value.Trim(), ExtractSpecRefs(m.Value)));
        }

        if (tasks.Count == 0)
        {
            var idx = 0;
            foreach (Match m in CheckboxRe.Matches(tasksText))
            {
                idx++;
                var id = $"T{idx:D3}";
                tasks.Add(new ParsedTask(id, m.Groups[1].Value.Trim(), m.Value.Trim(), ExtractSpecRefs(m.Value)));
            }
        }

        return tasks;
    }

    private static IReadOnlyList<string> ExtractSpecRefs(string text)
    {
        var refs = new List<string>();
        foreach (Match m in SpecIdRe.Matches(text))
        {
            var norm = $"{m.Groups[1].Value.ToUpperInvariant()}-{m.Groups[2].Value.PadLeft(2, '0')}";
            if (!refs.Any(r => r.Equals(norm, OIC)))
                refs.Add(norm);
        }
        return refs;
    }

    // ── Classification ────────────────────────────────────────────────────────

    private static TaskFinding ClassifyTask(
        ParsedTask task, List<SpecItem> specItems, HashSet<string> specKeywords)
    {
        var lower = task.RawText.ToLowerInvariant();

        // Pre-compute signals
        var isTechnical = TechnicalOnlyTerms.Any(t => lower.Contains(t));
        var hasBehavioral = BehavioralTerms.Any(t => lower.Contains(t));
        var isImportantConfig = InfrastructureConfigTerms.Any(t => lower.Contains(t));

        // ── EARLY EXIT: Technical-only tasks ──────────────────────────────────
        // Front-loading this prevents accidental keyword matches from elevating
        // setup/config tasks to Spec Linked. A task with clear technical signals
        // and no explicit behavioural terms cannot be Spec Linked.
        if (isTechnical && !hasBehavioral)
        {
            var allAreas = DetectAreas(lower);
            var areas = FilterAreasForTechnical(allAreas, lower);

            // Important infrastructure config (auth URLs, service bus, external APIs)
            // warrants Medium Risk even though it is still TechnicalOnly.
            var risk = isImportantConfig ? AlignmentRisk.Medium : AlignmentRisk.Low;
            var impactLevel = isImportantConfig ? ImpactLevel.Medium : ImpactLevel.Low;
            var riskReason = isImportantConfig ? BuildImportantConfigReason(areas) : string.Empty;

            var tests = BuildTests(areas, 5);

            return new TaskFinding
            {
                TaskId = task.TaskId,
                Title = task.Title,
                Status = AlignmentStatus.TechnicalOnly,
                Risk = risk,
                Reason = "Task is infrastructure, setup, or technical scaffolding with no externally visible behavior.",
                RecommendedAction = "No spec update required — verify this task does not introduce user-visible behavior.",
                Confidence = 0.80,
                AffectedAreas = areas,
                RecommendedTests = tests,
                ImpactLevel = impactLevel,
                RiskReason = riskReason,
                IsRegressionCandidate = false,
            };
        }

        // ── Score-based matching for potentially behavioural tasks ─────────────

        var score = 0;
        var reasons = new List<string>();

        // Direct spec ID references
        var matched = task.ReferencedIds
            .Select(r => specItems.FirstOrDefault(s => s.Id.Equals(r, OIC)))
            .OfType<SpecItem>()
            .ToList();

        foreach (var item in matched)
        {
            score += item.Kind switch
            {
                SpecItemKind.Requirement => 8,
                SpecItemKind.UserStory => 7,
                SpecItemKind.SuccessCriterion => 5,
                _ => 4,
            };
        }
        if (matched.Count > 0)
            reasons.Add($"direct ref: {string.Join(", ", matched.Select(m => m.Id))}");

        // CamelCase component matches from task title
        var entityMatches = new List<string>();
        foreach (Match m in CamelCaseRe.Matches(task.Title))
        {
            if (specKeywords.Contains(m.Value) && !entityMatches.Any(e => e.Equals(m.Value, OIC)))
            {
                entityMatches.Add(m.Value);
                score += 6;
                continue;
            }
            foreach (var part in SplitCamelCase(m.Value))
            {
                if (part.Length >= 5 && specKeywords.Contains(part)
                    && !entityMatches.Any(e => e.Equals(part, OIC)))
                {
                    entityMatches.Add(part);
                    score += 4;
                }
            }
        }
        if (entityMatches.Count > 0)
            reasons.Add($"entity: {string.Join(", ", entityMatches.Take(3))}");

        // Plain title word matches
        var kwMatches = new List<string>();
        foreach (var word in task.Title.Split(WordSplitChars, NONE))
        {
            if (word.Length >= 6 && specKeywords.Contains(word)
                && !entityMatches.Any(e => e.Equals(word, OIC))
                && !kwMatches.Any(k => k.Equals(word, OIC)))
            {
                kwMatches.Add(word);
                score += 2;
            }
        }
        if (kwMatches.Count > 0)
            reasons.Add($"keyword: {string.Join(", ", kwMatches.Take(3))}");

        // Area detection
        var areas2 = DetectAreas(lower);

        // Small bonus for areas confirmed in spec
        foreach (var (area, keywords) in AreaDefs)
        {
            if (!areas2.Contains(area)) continue;
            if (keywords.Any(kw => specKeywords.Any(sk => sk.Equals(kw, OIC))))
                score++;
        }

        var hasAnySpecKw = specKeywords.Any(kw => kw.Length >= 6 && lower.Contains(kw.ToLowerInvariant()));

        // ── Classify ──────────────────────────────────────────────────────────
        AlignmentStatus status;
        AlignmentRisk risk2;
        string reason, action;
        double confidence;

        if (score >= 6)
        {
            status = AlignmentStatus.Linked;
            risk2 = AlignmentRisk.Low;
            confidence = Math.Min(0.50 + score * 0.045, 0.95);
            reason = $"Strong spec coverage — {string.Join("; ", reasons)}.";
            action = "No action required — task is covered by the specification.";
        }
        else if (score >= 2)
        {
            status = AlignmentStatus.NeedsReview;
            risk2 = AlignmentRisk.Medium;
            confidence = Math.Min(0.38 + score * 0.06, 0.68);
            reason = $"Partial spec match — {string.Join("; ", reasons)}. Coverage may be incomplete.";
            action = "Verify spec coverage and link to a requirement or add a new user story.";
        }
        else if (hasBehavioral)
        {
            if (hasAnySpecKw)
            {
                status = AlignmentStatus.NeedsReview;
                risk2 = AlignmentRisk.Medium;
                confidence = 0.50;
                reason = "Task introduces behavior related to specification concepts, but no direct link was found.";
                action = "Link this task to an existing requirement, or add a new user story and acceptance scenario.";
            }
            else
            {
                status = AlignmentStatus.PossibleDeviation;
                risk2 = AlignmentRisk.High;
                confidence = 0.75;
                reason = "Task introduces or changes behavior (endpoint, event, permission, business rule, or UI action) with no matching specification item.";
                action = "Update spec.md to document this behavior, or link the task to an existing requirement.";
            }
        }
        else
        {
            // Non-technical, non-behavioral, no clear spec signal
            status = AlignmentStatus.NeedsReview;
            risk2 = AlignmentRisk.Medium;
            confidence = 0.40;
            reason = "Task could not be matched to any specification item. Manual review recommended.";
            action = "Review the task against the specification and link or add coverage as needed.";
        }

        // Downgrade AlignmentRisk when every detected area is Low-impact.
        // Ensures Testing, Validation, and ExceptionHandling tasks don't show
        // Medium Risk in the Spec Alignment tab when they have no real risk signal.
        if (risk2 == AlignmentRisk.Medium && status != AlignmentStatus.PossibleDeviation
            && areas2.Count > 0 && areas2.All(a => AreaImpact(a) == ImpactLevel.Low))
        {
            risk2 = AlignmentRisk.Low;
        }

        var impactLevel2 = ComputeImpactLevel(status, areas2);
        var tests2 = BuildTests(areas2, 6);
        var riskReason2 = GenerateRiskReason(status, risk2, areas2);
        var isReg = ComputeIsRegressionCandidate(risk2, areas2, tests2.Count);

        return new TaskFinding
        {
            TaskId = task.TaskId,
            Title = task.Title,
            Status = status,
            Risk = risk2,
            Reason = reason,
            RecommendedAction = action,
            Confidence = confidence,
            Matches = matched.Select(m => new SpecMatch
            {
                ItemId = m.Id,
                Title = m.Title,
                MatchType = m.Kind switch
                {
                    SpecItemKind.Requirement => SpecMatchType.Requirement,
                    SpecItemKind.UserStory => SpecMatchType.UserStory,
                    SpecItemKind.SuccessCriterion => SpecMatchType.SuccessCriterion,
                    _ => SpecMatchType.None,
                },
            }).ToList(),
            AffectedAreas = areas2,
            RecommendedTests = tests2,
            ImpactLevel = impactLevel2,
            MatchReason = BuildMatchReason(score, reasons),
            RiskReason = riskReason2,
            IsRegressionCandidate = isReg,
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static List<AffectedArea> DetectAreas(string lowerText)
    {
        var areas = new List<AffectedArea>();
        foreach (var (area, keywords) in AreaDefs)
            if (keywords.Any(kw => lowerText.Contains(kw)))
                areas.Add(area);
        return areas;
    }

    // For technical tasks, exclude domain-behaviour areas that appear only
    // because of incidental keyword overlap (e.g. "service bus" → DomainEvents
    // when the task is really just configuring a connection string).
    private static List<AffectedArea> FilterAreasForTechnical(List<AffectedArea> areas, string lowerText)
    {
        var filtered = areas
            .Where(a => a is AffectedArea.Infrastructure
                           or AffectedArea.HealthMonitoring
                           or AffectedArea.Testing
                           or AffectedArea.ExceptionHandling)
            .ToList();

        if (areas.Contains(AffectedArea.Authorization)
            && (lowerText.Contains("auth") || lowerText.Contains("oauth") || lowerText.Contains("jwt")))
            filtered.Add(AffectedArea.Authorization);

        if (areas.Contains(AffectedArea.Security) && lowerText.Contains("security"))
            filtered.Add(AffectedArea.Security);

        // Always ensure Infrastructure is present for setup tasks
        if (!filtered.Contains(AffectedArea.Infrastructure))
            filtered.Add(AffectedArea.Infrastructure);

        return filtered;
    }

    private static ImpactLevel ComputeImpactLevel(AlignmentStatus status, List<AffectedArea> areas)
    {
        if (status == AlignmentStatus.PossibleDeviation) return ImpactLevel.High;
        if (status == AlignmentStatus.TechnicalOnly) return ImpactLevel.Low;
        if (areas.Count == 0) return ImpactLevel.Unknown;
        return areas.Select(AreaImpact).Min();
    }

    private static List<string> BuildTests(List<AffectedArea> areas, int maxCount) =>
        areas.Where(a => AreaTests.ContainsKey(a))
             .SelectMany(a => AreaTests[a])
             .Distinct()
             .Take(maxCount)
             .ToList();

    private static string BuildMatchReason(int score, List<string> reasons)
    {
        if (reasons.Count == 0) return string.Empty;
        return $"{string.Join("; ", reasons)} (score: {score})";
    }

    private static string BuildImportantConfigReason(List<AffectedArea> areas)
    {
        var parts = new List<string> { "Configures external service dependencies." };
        if (areas.Contains(AffectedArea.Authorization))
            parts.Add("Includes authentication or authorization endpoint configuration.");
        if (areas.Contains(AffectedArea.Security))
            parts.Add("Touches security-relevant configuration.");
        parts.Add("Incorrect configuration may affect runtime behavior, health checks, or startup.");
        parts.Add("Does not directly change authorization rules or business logic.");
        return string.Join(" ", parts);
    }

    private static string GenerateRiskReason(
        AlignmentStatus status, AlignmentRisk risk, List<AffectedArea> areas)
    {
        if (risk == AlignmentRisk.Low) return string.Empty;

        var parts = new List<string>();

        if (status == AlignmentStatus.PossibleDeviation)
            parts.Add("Introduces behavior with no specification coverage.");

        foreach (var area in areas.Take(4))
        {
            var exp = AreaRiskExplanation(area);
            if (!string.IsNullOrEmpty(exp)) parts.Add(exp);
        }

        if (parts.Count == 0 && risk == AlignmentRisk.Medium)
            parts.Add("Partial specification match — verify coverage before release.");

        return string.Join(" ", parts);
    }

    private static string AreaRiskExplanation(AffectedArea area) => area switch
    {
        AffectedArea.Security =>
            "Implements or affects Kode 6/7 visibility rules.",
        AffectedArea.Authorization =>
            "Affects permission evaluation or access control behavior.",
        AffectedArea.Audit =>
            "Affects audit trail guarantees.",
        AffectedArea.DomainEvents =>
            "Affects domain event contracts or message publication.",
        AffectedArea.Profile =>
            "Handles personal identity data — national IDs or masking rules.",
        AffectedArea.Search =>
            "Affects what appears in search results or how filtering works.",
        AffectedArea.AccessManagement =>
            "Affects access grant flows, expiry, or self-assignment rules.",
        AffectedArea.OperationRegistration =>
            "Affects operation registration required for access control completion.",
        AffectedArea.Ingestion =>
            "Affects data ingestion pipeline or idempotency guarantees.",
        AffectedArea.ReferenceData =>
            "Affects reference data values or active/inactive state filtering.",
        AffectedArea.HealthMonitoring =>
            "Affects health reporting and external dependency visibility.",
        AffectedArea.BusinessRules =>
            "Implements business rules or state transition logic.",
        AffectedArea.Workflow =>
            "Affects process flow or workflow state transitions.",
        AffectedArea.Validation =>
            "Domain validation — verify boundary and rejection behavior.",
        AffectedArea.Testing =>
            "Test code — verify test coverage is adequate.",
        AffectedArea.ExceptionHandling =>
            "Error/exception handling — verify error scenarios and response format.",
        _ => string.Empty,
    };

    private static bool ComputeIsRegressionCandidate(
        AlignmentRisk risk, List<AffectedArea> areas, int testCount) =>
        risk == AlignmentRisk.High
        || areas.Any(a => a is AffectedArea.Security or AffectedArea.Authorization
                              or AffectedArea.Audit or AffectedArea.DomainEvents)
        || (testCount > 0 && risk == AlignmentRisk.Medium);

    private static IEnumerable<string> SplitCamelCase(string s) =>
        Regex.Matches(s, @"[A-Z][a-z]+").Select(m => m.Value);
}
