using System.Text.RegularExpressions;
using BirkNext.Api.Services.Integrations.ApplicationMessaging;
using BirkNext.Integrations;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BirkNext.Api.Services.Integrations.Scim;

/// <summary>
/// Classifies the uploaded SCIM specification's requirements against the analyzed source (Implemented / Partially implemented / Documented
/// only / Not found / Cannot assess) and maps the repository's own tests to the behaviours that matter. The requirement TEXT comes from the
/// specification; the STATUS comes only from source facts — a specification statement is never treated as implemented behaviour.
/// </summary>
public static class ScimRequirementRules
{
    private delegate (ScimRequirementStatus Status, string Evidence, IEnumerable<SourceLocation> Locations) Rule(Context context);

    private sealed record Context(List<ScimSourceFact> Facts, List<ScimOperation> Operations, List<ScimEventContract> Events, string Text)
    {
        public ScimSourceFact? F(string id) => Facts.FirstOrDefault(f => f.Id == id);
        public bool Has(string id, params ScimEvidenceState[] states) => F(id) is { } f && (states.Length == 0 || states.Contains(f.State));
        public IEnumerable<SourceLocation> L(params string[] ids) => ids.Select(F).OfType<ScimSourceFact>().SelectMany(f => f.Locations);
    }

    private static readonly Dictionary<string, Rule> Rules = new(StringComparer.Ordinal)
    {
        ["FR-001"] = c =>
        {
            var path = c.F("scim-base-path")?.Detail is { } d ? Regex.Match(d, @"MapGroup\(""([^""]+)""\)").Groups[1].Value : null;
            var expected = Regex.Match(c.Text, @"`(/[^`]+)`").Groups[1].Value;
            return path is null ? (ScimRequirementStatus.NotFound, "No SCIM route group.", [])
                : expected.Length > 0 && !string.Equals(expected, path, StringComparison.OrdinalIgnoreCase) ? (ScimRequirementStatus.PartiallyImplemented, $"The source maps {path}, the specification names {expected}.", c.L("scim-base-path"))
                : (ScimRequirementStatus.Implemented, $"Mapped at {path}.", c.L("scim-base-path"));
        },
        ["FR-002"] = c =>
        {
            var required = new[] { ("POST", "/Users"), ("GET", "/Users"), ("GET", "/Users/{"), ("PATCH", "/Users/{"), ("DELETE", "/Users/{") };
            var missing = required.Where(r => !c.Operations.Any(o => o.Method == r.Item1 && (r.Item2.EndsWith('{') ? o.Path.Contains(r.Item2, StringComparison.OrdinalIgnoreCase) : o.Path.EndsWith(r.Item2, StringComparison.OrdinalIgnoreCase)))).Select(r => $"{r.Item1} {r.Item2}").ToList();
            var patchLimits = new[] { "scim-patch-string-boolean", "scim-patch-pathless" }.Where(id => c.Has(id)).Select(id => c.F(id)!.Title.ToLowerInvariant()).ToList();
            if (missing.Count > 0) return (ScimRequirementStatus.PartiallyImplemented, $"Missing: {string.Join(", ", missing)}.", c.L("scim-detected"));
            return patchLimits.Count > 0
                ? (ScimRequirementStatus.PartiallyImplemented, $"All five operations are mapped and PATCH parses PatchOp, but only Replace + path active + a JSON boolean: {string.Join("; ", patchLimits)}.", c.L("scim-patch-parser"))
                : (ScimRequirementStatus.Implemented, "All five operations are mapped; PATCH parses PatchOp.", c.L("scim-patch-parser"));
        },
        ["FR-003"] = c => c.Has("scim-auth-provisioning-secret", ScimEvidenceState.NeedsReview) && c.Has("scim-auth-required")
            ? (ScimRequirementStatus.PartiallyImplemented, "Requests are authenticated before the handler (RequireAuthorization; a missing or invalid token gets 401 and cannot change state or publish) — but with Entra ID JWT validation and required appid/oid claims, not a provisioning Bearer secret. The secret option is never read.", c.L("scim-auth-required", "scim-auth-provisioning-secret"))
            : c.Has("scim-auth-required") ? (ScimRequirementStatus.Implemented, c.F("scim-auth-required")!.Detail, c.L("scim-auth-required")) : (ScimRequirementStatus.NotFound, "No authorization requirement on the SCIM routes.", []),
        ["FR-004"] = _ => (ScimRequirementStatus.CannotAssess, "Public reachability through the platform gateway is outside the analyzed application source.", []),
        ["FR-005"] = c => c.Has("scim-list")
            ? (ScimRequirementStatus.Implemented, c.F("scim-list")!.Detail + (c.Has("scim-list-unsupported-filter") ? " Other filters are ignored rather than rejected." : ""), c.L("scim-list"))
            : (ScimRequirementStatus.NotFound, "No paginated list handler.", []),
        ["FR-006"] = c => Event(c, "BrukerAktivert"),
        ["FR-007"] = c => Event(c, "BrukerDeaktivert"),
        ["FR-008"] = c => c.Operations.Where(o => o.Method is "POST" or "PATCH" or "DELETE").All(o => o.Behaviour.Any(b => b.StartsWith("Publishes", StringComparison.Ordinal)))
            ? (ScimRequirementStatus.Implemented, "Every write handler awaits the publish inside the request, before the response; there is no background publish." + (c.Has("scim-disabled-publisher") ? " When Service Bus is disabled by configuration the fallback publisher skips the send and the request still succeeds." : ""), c.L("scim-order-publish-before-commit", "scim-disabled-publisher"))
            : (ScimRequirementStatus.PartiallyImplemented, "Not every write handler publishes.", []),
        ["FR-009"] = c =>
        {
            var fields = c.Events.SelectMany(e => e.BodyFields).Select(f => f.ToLowerInvariant()).ToHashSet();
            var missing = new[] { "hendelsesid", "entraobjectid", "tidsstempel", "kildereferanse" }.Where(f => !fields.Contains(f)).ToList();
            if (missing.Count > 0) return (ScimRequirementStatus.PartiallyImplemented, $"Missing body fields: {string.Join(", ", missing)}.", c.Events.Select(e => e.Location).OfType<SourceLocation>());
            return c.Has("scim-message-id-mismatch")
                ? (ScimRequirementStatus.PartiallyImplemented, "The body carries HendelsesId, EntraObjectId, Tidsstempel and KildeReferanse, but the transport MessageId / HendelsesId property is a different GUID; Tidsstempel is the adapter's clock, not the time in Entra.", c.L("scim-message-id-mismatch"))
                : (ScimRequirementStatus.Implemented, "All fields are present.", c.Events.Select(e => e.Location).OfType<SourceLocation>());
        },
        ["FR-010"] = c =>
        {
            var gaps = new[] { "scim-post-generated-id", "scim-order-publish-before-commit", "scim-strategy-replays-publish", "scim-event-id-per-attempt" }.Where(id => c.Has(id)).Select(id => c.F(id)!.Title).ToList();
            return !c.Has("scim-idempotent-noop") ? (ScimRequirementStatus.NotFound, "No no-op guard for a repeated state.", [])
                : gaps.Count > 0 ? (ScimRequirementStatus.PartiallyImplemented, $"A repeat of an already-applied request is a no-op (no event). Not idempotent in these paths: {string.Join("; ", gaps)}.", c.L("scim-idempotent-noop", "scim-post-generated-id", "scim-order-publish-before-commit"))
                : (ScimRequirementStatus.Implemented, "Repeated identical requests are no-ops.", c.L("scim-idempotent-noop"));
        },
        ["FR-011"] = c => c.Has("scim-idempotent-noop") ? (ScimRequirementStatus.Implemented, "KjentBruker keeps EntraObjectId and the last active state; the handlers compare against it.", c.L("scim-idempotent-noop")) : (ScimRequirementStatus.NotFound, "No stored-state comparison.", []),
        ["FR-012"] = c => c.Has("scim-list") ? (ScimRequirementStatus.Implemented, "GET /Users returns all KjentBruker rows (active and inactive) with their active state, paginated.", c.L("scim-list")) : (ScimRequirementStatus.NotFound, "No list handler.", []),
        ["FR-013"] = c => c.Has("scim-retry-policy") ? (ScimRequirementStatus.Implemented, c.F("scim-retry-policy")!.Detail, c.L("scim-retry-policy")) : (ScimRequirementStatus.NotFound, "No retry policy around the publish.", []),
        ["FR-014"] = _ => (ScimRequirementStatus.DocumentedOnly, "A sender cannot dead-letter: failed sends surface as 5xx. No Azure Monitor alert on dead-letter depth is defined in the analyzed source (alerts would live in infrastructure).", []),
        ["FR-015"] = _ => (ScimRequirementStatus.CannotAssess, "Request isolation is ASP.NET Core's per-request scope; not separately tested.", []),
        ["FR-016"] = c => c.Has("scim-keyvault", ScimEvidenceState.NotFound) ? (ScimRequirementStatus.NotFound, "No Key Vault read and no provisioning secret in use (authentication is Entra JWT).", []) : (ScimRequirementStatus.Implemented, "Key Vault referenced.", c.L("scim-keyvault")),
        ["FR-018"] = c => (ScimRequirementStatus.CannotAssess, (c.Has("scim-auth-provisioning-secret", ScimEvidenceState.NeedsReview) ? "No provisioning secret is read, so none can be logged. " : "") + (c.F("scim-logging")?.Detail ?? ""), c.L("scim-logging")),
        ["FR-019"] = c => c.F("scim-logging") is { } log && log.Detail.Contains(" 0 in the SCIM operation handlers", StringComparison.Ordinal)
            ? (ScimRequirementStatus.PartiallyImplemented, "Published events are logged by the publisher; received SCIM requests are not logged by the handlers; errors reach the log only through the framework.", c.L("scim-logging"))
            : (ScimRequirementStatus.Implemented, c.F("scim-logging")?.Detail ?? "", c.L("scim-logging")),
        ["FR-020"] = c => c.F("scim-metrics") is { } m
            ? (m.State == ScimEvidenceState.SourceVerified ? ScimRequirementStatus.Implemented : ScimRequirementStatus.PartiallyImplemented, m.Detail + " No dead-letter count or Service Bus connectivity metric.", c.L("scim-metrics"))
            : (ScimRequirementStatus.NotFound, "No metrics.", []),
        ["FR-021"] = c => c.Has("scim-health-checks", ScimEvidenceState.SourceVerified) ? (ScimRequirementStatus.Implemented, c.F("scim-health-checks")!.Detail, c.L("scim-health-checks"))
            : c.Has("scim-health-checks", ScimEvidenceState.Partial) ? (ScimRequirementStatus.PartiallyImplemented, c.F("scim-health-checks")!.Detail, c.L("scim-health-checks"))
            : (ScimRequirementStatus.NotFound, (c.F("scim-health-checks")?.Detail ?? "No health checks.") + (c.Has("scim-health-ready-empty") ? " The readiness endpoint reports Healthy without checking a dependency." : ""), c.L("scim-health-checks", "scim-health-ready-empty")),
        ["FR-022"] = c => c.Has("scim-keyvault", ScimEvidenceState.NotFound) ? (ScimRequirementStatus.NotFound, "No Key Vault read at startup, so no fail-fast on Key Vault.", []) : (ScimRequirementStatus.CannotAssess, "Key Vault is referenced; fail-fast is not assessed.", c.L("scim-keyvault")),
        ["FR-023"] = c => c.Has("scim-order-publish-before-commit")
            ? (ScimRequirementStatus.PartiallyImplemented, "A SQL failure propagates and ASP.NET Core returns 500 (no false 2xx). But the event is published BEFORE the KjentBruker write is durable, so a commit failure leaves an event on the topic for a state that was never stored.", c.L("scim-order-publish-before-commit"))
            : c.Has("scim-order-commit-before-publish") ? (ScimRequirementStatus.Implemented, "The write is committed before the publish.", c.L("scim-order-commit-before-publish")) : (ScimRequirementStatus.CannotAssess, "Order of write and publish not established.", []),
    };

    private static (ScimRequirementStatus, string, IEnumerable<SourceLocation>) Event(Context c, string type) =>
        c.Events.FirstOrDefault(e => e.EventType == type) is { } e
            ? (ScimRequirementStatus.Implemented, $"{type} is published to {e.Topic ?? "(unresolved)"} by {string.Join(", ", e.Operations)}.", e.Location is { } l ? [l] : [])
            : (ScimRequirementStatus.NotFound, $"{type} is not published by any SCIM handler.", []);

    public static (List<ScimRequirement> Requirements, string? Source) Classify(List<SourceFile> documents, List<ScimSourceFact> facts, List<ScimOperation> operations, List<ScimEventContract> events)
    {
        var requirements = new List<ScimRequirement>();
        var spec = documents.FirstOrDefault(d => d.Path.EndsWith("/spec.md", StringComparison.OrdinalIgnoreCase) && d.Path.Contains("scim", StringComparison.OrdinalIgnoreCase) && d.Content.Contains("FR-0", StringComparison.Ordinal));
        if (spec is not null)
            foreach (Match match in Regex.Matches(spec.Content, @"^\s*-\s+\*\*(FR-\d+)\*\*:\s*(.+?)(?=^\s*-\s+\*\*|^\s*\*\*|^\s*#|\z)", RegexOptions.Multiline | RegexOptions.Singleline))
            {
                var id = match.Groups[1].Value;
                var text = Regex.Replace(match.Groups[2].Value, @"\s+", " ").Trim();
                var context = new Context(facts, operations, events, text);
                var (status, evidence, locations) = Rules.TryGetValue(id, out var rule) ? rule(context) : (ScimRequirementStatus.CannotAssess, "BirkNext has no rule for this requirement.", []);
                requirements.Add(new ScimRequirement
                {
                    Id = id, Text = text.Length > 320 ? text[..320] + "…" : text, Status = status, Evidence = evidence,
                    SpecLocation = new SourceLocation(spec.Path, ScimSourceAnalyzer.Line(spec.Content, match.Value.Trim())), Locations = locations.Distinct().Take(4).ToList(),
                });
            }
        var contract = documents.FirstOrDefault(d => d.Path.EndsWith("scim-http-api.md", StringComparison.OrdinalIgnoreCase));
        if (contract is not null)
        {
            void Add(string id, string needle, string text, ScimRequirementStatus status, string evidence, IEnumerable<SourceLocation> locations)
            {
                if (!contract.Content.Contains(needle, StringComparison.Ordinal)) return;
                requirements.Add(new ScimRequirement { Id = id, Text = text, Status = status, Evidence = evidence, SpecLocation = new SourceLocation(contract.Path, ScimSourceAnalyzer.Line(contract.Content, needle)), Locations = locations.Distinct().Take(4).ToList() });
            }
            var ctx = new Context(facts, operations, events, "");
            Add("CONTRACT-MessageId", "matches `MessageId`", "HendelsesId application property matches MessageId (event contract).",
                ctx.Has("scim-message-id-mismatch") ? ScimRequirementStatus.NotFound : ScimRequirementStatus.Implemented,
                ctx.Has("scim-message-id-mismatch") ? "MessageId and the HendelsesId property match each other, but neither is the body's HendelsesId." : "Matches.", ctx.L("scim-message-id-mismatch"));
            Add("CONTRACT-404", "404 Not Found** — if user not in KjentBrukere", "PATCH/DELETE of an unknown user returns 404.",
                ctx.Has("scim-unknown-user-upsert") ? ScimRequirementStatus.NotFound : ScimRequirementStatus.Implemented,
                ctx.Has("scim-unknown-user-upsert") ? ctx.F("scim-unknown-user-upsert")!.Detail : "404 returned.", ctx.L("scim-unknown-user-upsert"));
            Add("CONTRACT-Health", "`GET /health`", "Health endpoint with database and servicebus checks.",
                ctx.Has("scim-health-checks", ScimEvidenceState.SourceVerified) ? ScimRequirementStatus.Implemented : ScimRequirementStatus.NotFound,
                ctx.F("scim-health-mapping")?.Detail + " " + ctx.F("scim-health-checks")?.Detail, ctx.L("scim-health-checks", "scim-health-mapping"));
        }
        return (requirements, spec?.Path ?? contract?.Path);
    }

    // ── The analyzed repository's own tests ─────────────────────────────────────────────────────────────────────────

    private sealed record Scenario(string Name, Func<string, bool> Match, bool UsesPublisher, bool UsesAuth);

    private static readonly Scenario[] Scenarios =
    [
        new("Invalid token rejected (401, no event)", n => n.Contains("Token") && (n.Contains("401") || n.Contains("Unauthorized")), true, true),
        new("Missing token rejected (401, no event)", n => (n.Contains("Missing") || n.Contains("NoToken") || n.Contains("WithoutToken")) && (n.Contains("401") || n.Contains("Unauthorized")), true, true),
        new("Create user (POST, active) publishes BrukerAktivert", n => n.Contains("Post") && (n.Contains("New") || n.Contains("Create")), true, false),
        new("Reactivation (inactive → active) publishes BrukerAktivert", n => n.Contains("Inactive") && (n.Contains("ActiveTrue") || n.Contains("Activate")), true, false),
        new("Deactivation via PATCH active=false publishes BrukerDeaktivert", n => n.Contains("Patch") && n.Contains("ActiveFalse"), true, false),
        new("Deactivation via DELETE publishes BrukerDeaktivert", n => n.Contains("Delete") || n.Contains("Deactivate"), true, false),
        new("Repeated identical request publishes no duplicate", n => n.Contains("Twice") || n.Contains("Times") || n.Contains("Idempotent") || n.Contains("NoOp"), true, false),
        new("POST without id (server-assigned id)", n => n.Contains("WithoutId") || n.Contains("NoId") || n.Contains("MissingId"), false, false),
        new("PATCH value sent as a string", n => n.Contains("Patch") && n.Contains("String"), false, false),
        new("PatchOp without Operations → 4xx, no change", n => n.Contains("MissingOperations") || n.Contains("NoOperations") || n.Contains("NullOperations"), false, false),
        new("Malformed PATCH value is ignored (no change)", n => n.Contains("Malformed") || n.Contains("BadRequest") || n.Contains("InvalidPayload"), false, false),
        new("SQL failure → 5xx, no false success", n => (n.Contains("Sql") || n.Contains("Database") || n.Contains("Db")) && (n.Contains("Fail") || n.Contains("Unavailable") || n.Contains("500") || n.Contains("Throws")), false, false),
        new("Service Bus publish failure → 5xx after retries", n => (n.Contains("Publish") || n.Contains("ServiceBus")) && (n.Contains("Fail") || n.Contains("Exhaust") || n.Contains("Unavailable") || n.Contains("Throws")), false, false),
        new("Transient publish failure then success", n => n.Contains("Retry"), false, false),
        new("Publish succeeded, commit failed (retry duplicates)", n => (n.Contains("Commit") || n.Contains("Save")) && n.Contains("Fail"), false, false),
        new("Health reflects a dependency failure", n => (n.Contains("Health") || n.Contains("Ready")) && (n.Contains("Unhealthy") || n.Contains("Fail") || n.Contains("Down") || n.Contains("503")), false, false),
        new("Key Vault unavailable at startup fails fast", n => n.Contains("KeyVault"), false, false),
    ];

    internal static List<ScimTestCoverage> TestCoverage(List<ScimSourceAnalyzer.Code> tests, List<ScimSourceFact> facts)
    {
        var methods = tests.SelectMany(c => c.Root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => m.AttributeLists.SelectMany(a => a.Attributes).Any(a => a.Name.ToString() is "Fact" or "Theory"))
            .Select(m => $"{(m.Parent as ClassDeclarationSyntax)?.Identifier.Text}.{m.Identifier.Text}")).ToList();
        var fakeAuth = tests.Any(c => c.Root.DescendantNodes().OfType<ClassDeclarationSyntax>().Any(t => t.BaseList?.ToString().Contains("AuthenticationHandler", StringComparison.Ordinal) == true));
        var fakePublisher = tests.Any(c => c.Root.DescendantNodes().OfType<ClassDeclarationSyntax>().Any(t => t.BaseList?.ToString().Contains("EventPublisher", StringComparison.Ordinal) == true));
        var coverage = Scenarios.Select(s =>
        {
            var matched = methods.Where(m => s.Match(m[(m.IndexOf('.') + 1)..])).ToList();
            var fake = matched.Count > 0 && (s.UsesAuth && fakeAuth || s.UsesPublisher && fakePublisher);
            var note = matched.Count == 0 ? "No test in the analyzed repository."
                : s.UsesAuth && fakeAuth ? "Runs with a test authentication handler that replaces the Entra JWT scheme and the SCIM policy: the production appid/oid policy is not exercised."
                : s.UsesPublisher && fakePublisher ? "Service Bus is replaced by an in-memory fake publisher (events captured, no transport)."
                : "";
            return new ScimTestCoverage { Scenario = s.Name, State = matched.Count == 0 ? ScimTestCoverageState.NotTested : fake ? ScimTestCoverageState.TestedWithFake : ScimTestCoverageState.Tested, Tests = matched.Take(6).ToList(), Note = note };
        }).ToList();
        var ready = methods.Where(m => m.Contains("Ready", StringComparison.Ordinal) || m.Contains("Health", StringComparison.Ordinal)).ToList();
        if (ready.Count > 0)
            coverage.Add(new ScimTestCoverage
            {
                Scenario = "Health endpoints", State = ScimTestCoverageState.Tested, Tests = ready,
                Note = facts.Any(f => f.Id == "scim-health-ready-empty") ? "The readiness test asserts \"Healthy\", but no dependency check is registered — it cannot detect an unreachable SQL Server or Service Bus, whatever its name says." : "",
            });
        return coverage;
    }
}
