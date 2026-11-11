using System.Text.Json;

namespace BirkNext.Web.Services;

/// <summary>One sidebar entry and how Feature Visibility governs it. <paramref name="RequiredKeys"/>: every one must be on;
/// <paramref name="AnyOfKeys"/>: at least one must be on (shared entries such as Import Project). Both empty = always shown.</summary>
public sealed record SidebarEntry(
    string SectionId, string SectionLabel, NavItem Item,
    IReadOnlyList<string> RequiredKeys, IReadOnlyList<string> AnyOfKeys, string? PrimaryKey,
    bool Shown, string? HiddenReason, FeatureAvailabilityInfo? Availability, DocumentationFeature? Help)
{
    public bool AlwaysShown => RequiredKeys.Count == 0 && AnyOfKeys.Count == 0;
    public string Route => Item.Route;
    public string Label => Item.Label;
}

/// <summary>One configurable switch: its stored (or edited) value, lifecycle, and the sidebar routes it actually gates.</summary>
public sealed record FeatureSwitch(
    string Key, string Label, bool On, bool Platform, FeatureLifecycle Lifecycle, FeatureDeclaration? Declaration,
    IReadOnlyList<string> GatedRoutes, bool AnyEntryShown)
{
    public bool HasEffect => GatedRoutes.Count > 0;
}

/// <summary>A navigation consistency problem found by comparing identities (keys, routes, pages), never counts.</summary>
public sealed record NavigationIssue(string Kind, string Subject, string Detail);

public sealed record FeatureInventory(
    IReadOnlyList<SidebarEntry> Entries,
    IReadOnlyList<FeatureSwitch> Switches,
    IReadOnlyList<ContextualRoute> OtherPages,
    IReadOnlyList<NavigationIssue> Issues)
{
    /// <summary>Configurable switches (platform keys excluded): the backend's Core and Advanced lists.</summary>
    public int SwitchCount => Switches.Count(s => !s.Platform);
    public int PlatformCount => Switches.Count(s => s.Platform);
    public int ShownEntries => Entries.Count(e => e.Shown);
    public int HiddenEntries => Entries.Count(e => !e.Shown);
    public int AlwaysShownEntries => Entries.Count(e => e.AlwaysShown);

    public FeatureSwitch? Switch(string key) => Switches.FirstOrDefault(s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Feature Visibility's inventory, derived from the real sources: the sidebar's rows and their own visibility rules (NavigationCatalog),
/// the configured switches (the backend's lists), the app's routable pages, product availability (FeatureAvailabilityCatalog) and Help
/// (DocumentationCatalog). Which switch gates which entry is found by evaluating each row's rule with that switch off and on, so Menu
/// Synchronization compares identities and cannot drift from the sidebar.
/// </summary>
public static class FeatureVisibilityInventory
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Routable page templates of this app ("@page" directives), lower-case without the leading slash.</summary>
    public static IReadOnlyList<string> AppRoutes { get; } =
        DocumentationCatalog.DiscoverRoutes(typeof(FeatureVisibilityInventory).Assembly).Select(Normalize).Distinct().ToList();

    /// <summary>A sidebar entry's identity: its route including the query (a deep link into a section is its own entry).</summary>
    private static string Identity(string route) => route.Trim('/').ToLowerInvariant();

    public static string Normalize(string route) => route.Split('?')[0].Trim('/').ToLowerInvariant();

    /// <param name="settings">The configured switches (platform, core, advanced) as the backend lists them.</param>
    /// <param name="values">Values to evaluate instead of the stored ones (edit mode); null = stored values.</param>
    /// <param name="routes">Routable page templates; defaults to this app's.</param>
    /// <param name="sections">The sidebar; defaults to <see cref="NavigationCatalog.Sections"/> (other values only in tests).</param>
    public static FeatureInventory Build(EditableFeatureVisibilityDto settings, IReadOnlyDictionary<string, bool>? values = null, IReadOnlyList<string>? routes = null,
        IReadOnlyList<NavSection>? sections = null)
    {
        routes ??= AppRoutes;
        sections ??= NavigationCatalog.Sections;
        var configured = settings.Platform.Select(e => (Entry: e, Platform: true))
            .Concat(settings.Core.Concat(settings.Advanced).Select(e => (Entry: e, Platform: false))).ToList();
        var keys = configured.Select(c => c.Entry.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        bool Current(string key)
        {
            var entry = configured.First(c => c.Entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            return entry.Platform || (values is not null && values.TryGetValue(entry.Entry.Key, out var v) ? v : entry.Entry.Value);
        }
        string LabelOf(string key) => configured.FirstOrDefault(c => c.Entry.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Entry?.Label ?? key;

        var allOn = Probe(keys.ToDictionary(k => k, _ => true));
        var allOff = Probe(keys.ToDictionary(k => k, _ => false));
        var now = Probe(keys.ToDictionary(k => k, Current));
        var withoutKey = keys.ToDictionary(k => k, k => Probe(keys.ToDictionary(x => x, x => !x.Equals(k, StringComparison.OrdinalIgnoreCase))), StringComparer.OrdinalIgnoreCase);
        var onlyKey = keys.ToDictionary(k => k, k => Probe(keys.ToDictionary(x => x, x => x.Equals(k, StringComparison.OrdinalIgnoreCase))), StringComparer.OrdinalIgnoreCase);
        var masters = FeatureVisibilityTaxonomy.Declarations.Where(d => d.MasterSwitch).Select(d => d.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var entries = new List<SidebarEntry>();
        var issues = new List<NavigationIssue>();
        foreach (var section in sections)
        foreach (var item in section.Items)
        {
            var required = keys.Where(k => item.Visible(allOn) && !item.Visible(withoutKey[k])).ToList();
            var anyOf = required.Count == 0 && !item.Visible(allOff) ? keys.Where(k => item.Visible(onlyKey[k])).ToList() : [];
            var primary = required.LastOrDefault(k => !masters.Contains(k)) ?? required.LastOrDefault();
            var shown = item.Visible(now);
            string? reason = null;
            if (!shown)
            {
                var off = required.Where(k => !Current(k)).Select(LabelOf).ToList();
                reason = off.Count > 0 ? $"Off: {string.Join(" and ", off)}"
                    : anyOf.Count > 0 ? $"Off: {string.Join(" and ", anyOf.Select(LabelOf))} (one of them is enough)"
                    : "Hidden by a rule that no switch controls";
            }
            entries.Add(new(section.Id, section.Label, item, required, anyOf, primary, shown, reason,
                FeatureAvailabilityCatalog.Find(item.FeatureId),
                DocumentationCatalog.Features.FirstOrDefault(f => Identity(f.Route) == Identity(item.Route))));

            if (!routes.Contains(Normalize(item.Route)))
                issues.Add(new("Entry opens no page", item.Label, $"The sidebar entry points to /{item.Route}, which no page serves."));
            if (!item.Visible(allOn))
                issues.Add(new("Entry can never be shown", item.Label, "It stays hidden even with every switch on."));
        }

        foreach (var duplicate in entries.GroupBy(e => Identity(e.Route)).Where(g => g.Count() > 1))
            issues.Add(new("Duplicate entry", duplicate.First().Label, $"{duplicate.Count()} sidebar entries open /{duplicate.Key}."));
        foreach (var duplicate in configured.GroupBy(c => c.Entry.Key, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            issues.Add(new("Duplicate switch", duplicate.Key, $"The key is configured {duplicate.Count()} times."));

        var switches = configured.DistinctBy(c => c.Entry.Key, StringComparer.OrdinalIgnoreCase).Select(c =>
        {
            var gated = entries.Where(e => e.RequiredKeys.Concat(e.AnyOfKeys).Contains(c.Entry.Key, StringComparer.OrdinalIgnoreCase)).ToList();
            var declaration = FeatureVisibilityTaxonomy.Declaration(c.Entry.Key);
            return new FeatureSwitch(c.Entry.Key, c.Entry.Label, Current(c.Entry.Key), c.Platform, declaration?.Lifecycle ?? FeatureLifecycle.Implemented, declaration,
                gated.Select(e => e.Route).ToList(), gated.Any(e => e.Shown));
        }).ToList();

        foreach (var s in switches.Where(s => !s.Platform && !s.HasEffect))
            issues.Add(new("Switch gates nothing", s.Label, $"{s.Key} hides no sidebar entry. Wire it to an entry or retire it."));
        foreach (var s in switches.Where(s => FeatureVisibilityTaxonomy.Retired.Any(r => r.Key.Equals(s.Key, StringComparison.OrdinalIgnoreCase))))
            issues.Add(new("Retired switch still configurable", s.Label, $"{s.Key} is retired but the backend still lists it."));
        foreach (var d in FeatureVisibilityTaxonomy.Declarations.Where(d => !keys.Contains(d.Key, StringComparer.OrdinalIgnoreCase)))
            issues.Add(new("Declared switch not configured", d.Key, "The backend does not list this key."));

        var sidebarRoutes = entries.Select(e => Normalize(e.Route)).ToHashSet();
        var classified = FeatureVisibilityTaxonomy.ContextualRoutes.Select(r => Normalize(r.Template)).ToHashSet();
        foreach (var route in routes.Where(r => !sidebarRoutes.Contains(r) && !classified.Contains(r)))
            issues.Add(new("Page without a sidebar entry", "/" + route, "Add it to the sidebar or classify it as an alias, redirect or contextual page."));
        foreach (var page in FeatureVisibilityTaxonomy.ContextualRoutes.Where(r => !routes.Contains(Normalize(r.Template))))
            issues.Add(new("Classified page no longer exists", "/" + page.Template, "Remove it from the classification."));

        return new(entries, switches, FeatureVisibilityTaxonomy.ContextualRoutes.Where(r => routes.Contains(Normalize(r.Template))).ToList(), issues);
    }

    // The sidebar's own rules take the FeatureVisibilityService; a probe is one built from chosen values through the same JSON shape the
    // backend sends (keys the DTO does not know are ignored, so an unknown switch shows up as gating nothing).
    private static FeatureVisibilityService Probe(Dictionary<string, bool> values)
    {
        var service = new FeatureVisibilityService();
        service.ApplyLocalFlags(JsonSerializer.Deserialize<FeatureVisibilityDto>(JsonSerializer.Serialize(values), Json) ?? new());
        return service;
    }
}
