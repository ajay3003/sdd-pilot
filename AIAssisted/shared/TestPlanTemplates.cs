using System.Text.Json.Serialization;

namespace BirkNext.TestPlanning;

// ── Test plan templates (2026-11-26) ───────────────────────────────────────────────────────────────────────────────────────────────────────
// A template is STRUCTURE ONLY: suite names, nesting, order, optional descriptions and test level. Never cases, steps, test data, source links,
// approvals, revisions, Azure DevOps ids or transfer history. A plan created from a template gets new plan-scoped suite ids; changing or archiving
// a template never changes a plan made from it. Built-in templates are read-only; a copy of one is an editable custom template.

/// <summary>One suite of a template; <see cref="Key"/> is only the template's own reference (a plan gets new ids).</summary>
public sealed record TemplateSuite(string Key, string Name, string? ParentKey = null, int Order = 0, ScenarioTestLevel? TestLevel = null, string? Description = null);

public sealed record TestPlanTemplate
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public bool BuiltIn { get; init; }
    public bool Archived { get; init; }
    public List<TemplateSuite> Suites { get; init; } = [];
    public string? CreatedBy { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public string? UpdatedBy { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
    /// <summary>The plan a template was saved from (provenance only; the plan is never changed by the template).</summary>
    public Guid? SavedFromPlan { get; init; }
}

/// <summary>Create or change a custom template: from a plan's structure (<see cref="FromPlan"/>) or an explicit suite list.</summary>
public sealed record SaveTestPlanTemplateRequest(string Name, string? Description, List<TemplateSuite>? Suites = null, Guid? FromPlan = null);

/// <summary>Manage a custom template: rename / change suites (Save), duplicate (any template), archive or restore.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TemplateAction { Archive, Restore, Duplicate }

/// <summary>The three ways to start a test plan in «Ny testplan».</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NewPlanMethod { FromTemplate, Empty, FromSelection }

public static class TestPlanTemplateRules
{
    public const string StandardId = "builtin:standard", E2EId = "builtin:e2e", IntegrationId = "builtin:integration", RequirementsId = "builtin:requirements";
    public const int MaxSuites = 200;

    /// <summary>The built-in templates (read-only): generic structure, no project-specific subsystem names.</summary>
    public static readonly IReadOnlyList<TestPlanTemplate> BuiltIn =
    [
        new()
        {
            Id = StandardId, Name = "Standard testplan", BuiltIn = true, Description = "Ende til ende, integrasjon, komponent og uklassifisert. Legg til undergrupper selv.",
            Suites =
            [
                new("e2e", "Ende til ende", null, 0, ScenarioTestLevel.EndToEnd), new("int", "Integrasjon", null, 1, ScenarioTestLevel.Integration),
                new("comp", "Komponent", null, 2, ScenarioTestLevel.Component), new("unc", "Uklassifisert", null, 3, ScenarioTestLevel.Unclassified),
            ],
        },
        new()
        {
            Id = E2EId, Name = "E2E-testplan", BuiltIn = true, Description = "Forretningsflyter fra ende til ende.",
            Suites = [new("flows", "Forretningsflyter", null, 0, ScenarioTestLevel.EndToEnd), new("other", "Andre forretningsflyter", null, 1, ScenarioTestLevel.EndToEnd)],
        },
        new()
        {
            Id = IntegrationId, Name = "Integrasjonstestplan", BuiltIn = true, Description = "Integrasjoner mellom tjenester og meldingsflyt.",
            Suites =
            [
                new("int", "Integrasjoner", null, 0, ScenarioTestLevel.Integration), new("svc", "Tjenesteintegrasjoner", "int", 0, ScenarioTestLevel.Integration),
                new("msg", "Meldingsflyt", "int", 1, ScenarioTestLevel.Integration),
            ],
        },
        new()
        {
            Id = RequirementsId, Name = "Kravbasert testplan", BuiltIn = true, Description = "Én gruppe per modul; legg brukerhistoriene under.",
            Suites = [new("mod", "Moduler", null, 0, null, "Lag én undergruppe per modul og brukerhistorie.")],
        },
    ];

    /// <summary>Null when the template is valid; else why not (no name, duplicate keys, unknown parent, cycle, too many suites).</summary>
    public static string? Validate(string name, IReadOnlyList<TemplateSuite> suites)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Gi malen et navn.";
        if (suites.Count > MaxSuites) return $"En mal kan ha høyst {MaxSuites} testgrupper.";
        if (suites.Any(s => string.IsNullOrWhiteSpace(s.Name))) return "Alle testgruppene må ha et navn.";
        var drafts = suites.Select(s => new TestSuiteDraft(s.Key, s.Name, "template", null) { ParentId = s.ParentKey }).ToList();
        return PlanStructure.Validate(drafts);
    }

    /// <summary>A template's suites for a NEW plan: new plan-scoped ids (never the template's keys), same nesting, order and level.</summary>
    public static List<TestSuiteDraft> Instantiate(TestPlanTemplate template, Func<string>? newId = null)
    {
        var ids = template.Suites.ToDictionary(s => s.Key, _ => newId?.Invoke() ?? "suite:t:" + Guid.NewGuid().ToString("N")[..12], StringComparer.Ordinal);
        return [.. template.Suites.Select(s => new TestSuiteDraft(ids[s.Key], s.Name.Trim(), "template", null)
        {
            ParentId = s.ParentKey is { } p && ids.TryGetValue(p, out var pid) ? pid : null, Order = s.Order, TestLevel = s.TestLevel,
        })];
    }

    /// <summary>A plan's suite structure as template suites (structure only: no case, step, link, approval or Azure DevOps id is read).</summary>
    public static List<TemplateSuite> FromPlan(TestPlanDraft plan)
    {
        var keys = plan.Suites.Select((s, i) => (s.Id, Key: $"s{i + 1}")).ToDictionary(x => x.Id, x => x.Key, StringComparer.Ordinal);
        return [.. PlanStructure.Flatten(plan.Suites).Select(x => new TemplateSuite(keys[x.Suite.Id], x.Suite.Name, x.Suite.ParentId is { } p && keys.TryGetValue(p, out var pk) ? pk : null,
            x.Suite.Order, x.Suite.TestLevel))];
    }
}
