using BirkNext.Api.Services.CanonicalInterpretation;
using BirkNext.Api.Services.IntegrationJourneys;
using BirkNext.Api.Services.TestData;
using BirkNext.Interpretation;
using BirkNext.Scopes;

namespace BirkNext.Api.Services.ScopeRegistry;

/// <summary>Checks scope member ids against their owning catalogs (used before a draft is saved and to show stale members).</summary>
public interface IScopeMemberResolver
{
    Task<IReadOnlyList<ScopeMemberStatus>> CheckAsync(IReadOnlyList<ScopeMember> members, string? catalogScope, CancellationToken ct);
}

/// <summary>
/// The member catalogs of the Scope Registry as thin adapters over the owning stores — nothing is copied:
/// systems/services, requirements, user stories, features, scenarios and contracts from the latest canonical interpretation of the active Target
/// Environment; Test Data assets (immutable asset id, never values) from the Test Data catalog; journeys from the registered Integration Journey
/// packs. Kinds with no catalog that can confirm an id (relationship, acceptance criterion, test suite, test case) cannot be added.
/// </summary>
public sealed class ScopeMemberCatalogService(CanonicalInterpretationService canonical, TestDataCatalog? testData = null,
    IIntegrationJourneyPackRegistry? journeys = null) : IScopeMemberResolver
{
    public const string CanonicalModule = "Canonical interpretation (Source Analysis)";

    /// <summary>The canonical entity types a member kind may reference.</summary>
    public static IReadOnlyList<CanonicalEntityType>? CanonicalTypes(ScopeMemberKind kind) => kind switch
    {
        ScopeMemberKind.System => [CanonicalEntityType.System, CanonicalEntityType.Service],
        ScopeMemberKind.Requirement => [CanonicalEntityType.Requirement],
        ScopeMemberKind.Scenario => [CanonicalEntityType.Scenario],
        ScopeMemberKind.UserStory => [CanonicalEntityType.UserStory],
        ScopeMemberKind.Feature => [CanonicalEntityType.Feature],
        ScopeMemberKind.Contract => [CanonicalEntityType.Contract],
        _ => null,
    };

    public static bool HasCatalog(ScopeMemberKind kind) => CanonicalTypes(kind) is not null || kind is ScopeMemberKind.TestDataAsset or ScopeMemberKind.Journey;

    public async Task<ScopeCatalogPage> SearchAsync(ScopeMemberKind kind, string? catalogScope, string? search, int skip, int take, CancellationToken ct)
    {
        take = Math.Clamp(take, 1, 100);
        skip = Math.Max(0, skip);
        if (CanonicalTypes(kind) is { } types)
        {
            if (string.IsNullOrWhiteSpace(catalogScope))
                return new(kind, [], 0, skip, take) { Notices = ["Choose an active Target Environment: this catalog comes from its interpretation of the imported source."] };
            var page = await canonical.LatestEntitiesAsync(catalogScope, types, search, skip, take, ct);
            if (page is null)
                return new(kind, [], 0, skip, take) { Notices = ["No interpretation exists for the active Target Environment yet. Run Analyze relationships in Relationship Review."] };
            return new(kind, [.. page.Items.Select(e => new ScopeCatalogItem
            {
                Kind = kind, TargetId = e.Key, Label = e.DisplayName, Description = Describe(e), SourceModule = CanonicalModule,
                State = e.ResolutionMethod, SubKind = e.Type.ToString(), CatalogScope = catalogScope,
            })], page.Total, skip, take);
        }
        if (kind == ScopeMemberKind.TestDataAsset)
        {
            if (testData is null) return new(kind, [], 0, skip, take) { Available = false, Notices = ["The Test Data catalog is not available in this host."] };
            var assets = TestDataItems().Where(i => Matches(i, search)).ToList();
            return new(kind, [.. assets.Skip(skip).Take(take)], assets.Count, skip, take)
            {
                Notices = TestDataItems().Count() < (testData.View().Assets.Count) ? ["Assets without an immutable asset id (legacy aliases) cannot be scope members until they get one."] : [],
            };
        }
        if (kind == ScopeMemberKind.Journey)
        {
            if (journeys is null) return new(kind, [], 0, skip, take) { Available = false, Notices = ["Integration Journeys are not available in this host."] };
            var items = JourneyItems().Where(i => Matches(i, search)).ToList();
            return new(kind, [.. items.Skip(skip).Take(take)], items.Count, skip, take);
        }
        return new(kind, [], 0, skip, take)
        {
            Available = false,
            Notices = [$"No catalog can confirm {Plural(kind)} yet, so they cannot be added (BirkNext never stores an id it cannot check)."],
        };
    }

    public async Task<IReadOnlyList<ScopeMemberStatus>> CheckAsync(IReadOnlyList<ScopeMember> members, string? catalogScope, CancellationToken ct)
    {
        var result = new List<ScopeMemberStatus>();
        var canonicalMembers = new List<ScopeMember>();
        foreach (var m in members)
        {
            if (CanonicalTypes(m.Kind) is not null) { canonicalMembers.Add(m); continue; }
            if (m.Kind == ScopeMemberKind.TestDataAsset)
                result.Add(testData is null ? new(m.Key, ScopeMemberCheck.NotCheckable, "The Test Data catalog is not available here.")
                    : TestDataItems().FirstOrDefault(i => i.TargetId == m.TargetId) is { } asset ? new(m.Key, ScopeMemberCheck.Current, "In the Test Data catalog.", asset.Label)
                    : new(m.Key, ScopeMemberCheck.Missing, "No Test Data asset has this asset id."));
            else if (m.Kind == ScopeMemberKind.Journey)
                result.Add(journeys is null ? new(m.Key, ScopeMemberCheck.NotCheckable, "Integration Journeys are not available here.")
                    : JourneyItems().FirstOrDefault(i => i.TargetId == m.TargetId) is { } journey ? new(m.Key, ScopeMemberCheck.Current, "A registered journey.", journey.Label)
                    : new(m.Key, ScopeMemberCheck.Missing, "No registered journey has this id."));
            else result.Add(new(m.Key, ScopeMemberCheck.NotCheckable, $"No catalog can confirm {Plural(m.Kind)}."));
        }
        foreach (var group in canonicalMembers.GroupBy(m => m.CatalogScope ?? catalogScope))
        {
            var scope = group.Key;
            if (string.IsNullOrWhiteSpace(scope))
            {
                result.AddRange(group.Select(m => new ScopeMemberStatus(m.Key, ScopeMemberCheck.NotCheckable, "No active Target Environment to check it against.")));
                continue;
            }
            if (!string.IsNullOrWhiteSpace(catalogScope) && !string.Equals(scope, catalogScope, StringComparison.Ordinal))
            {
                result.AddRange(group.Select(m => new ScopeMemberStatus(m.Key, ScopeMemberCheck.OtherContext,
                    "Chosen from another Target Environment's interpretation; switch to that environment to check it.")));
                continue;
            }
            var names = await canonical.LatestEntityNamesAsync(scope, [.. group.Select(m => m.TargetId)], ct);
            foreach (var m in group)
            {
                var typeOk = CanonicalTypes(m.Kind)!.Any(t => m.TargetId.StartsWith(t + "|", StringComparison.Ordinal));
                result.Add(names is null ? new(m.Key, ScopeMemberCheck.NotCheckable, "No interpretation exists for this Target Environment yet.")
                    : !typeOk ? new(m.Key, ScopeMemberCheck.Missing, $"This id is not a {Singular(m.Kind)}.")
                    : names.TryGetValue(m.TargetId, out var name) ? new(m.Key, ScopeMemberCheck.Current, "In the latest interpretation.", name)
                    : new(m.Key, ScopeMemberCheck.Missing, "Not in the latest interpretation (removed, renamed or never present). Review before changing the scope."));
            }
        }
        var order = members.Select((m, i) => (m.Key, i)).GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.First().i);
        return [.. result.OrderBy(r => order.GetValueOrDefault(r.Key))];
    }

    IEnumerable<ScopeCatalogItem> TestDataItems() => testData!.View().Assets.Where(a => !string.IsNullOrWhiteSpace(a.AssetId))
        .OrderBy(a => a.Alias, StringComparer.OrdinalIgnoreCase).Select(a => new ScopeCatalogItem
        {
            Kind = ScopeMemberKind.TestDataAsset, TargetId = a.AssetId!, Label = string.IsNullOrWhiteSpace(a.Label) ? a.Alias : $"{a.Label} ({a.Alias})",
            Description = $"{a.Kind}{(a.Environment is { Length: > 0 } env ? $" · {env} only" : " · any environment")}",
            SourceModule = "Test Data", State = $"{a.Approval} · {a.Validation}", SubKind = a.Kind.ToString(),
        });

    IEnumerable<ScopeCatalogItem> JourneyItems() => journeys!.Packs.SelectMany(p => p.Journeys.OrderBy(j => j.Order).Select(j => new ScopeCatalogItem
    {
        Kind = ScopeMemberKind.Journey, TargetId = $"{p.PackId}:{j.JourneyId}", Label = j.DisplayName,
        Description = string.IsNullOrWhiteSpace(j.Description) ? p.DisplayName : $"{p.DisplayName} · {j.Description}",
        SourceModule = "Integration Journeys", State = "Registered journey", SubKind = p.PackId,
    }));

    static bool Matches(ScopeCatalogItem i, string? search) => string.IsNullOrWhiteSpace(search)
        || i.Label.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) || (i.Description ?? "").Contains(search.Trim(), StringComparison.OrdinalIgnoreCase);

    static string Describe(CanonicalEntityView e) => e.Type switch
    {
        CanonicalEntityType.Service => "Service",
        CanonicalEntityType.System => "System",
        _ => e.SourceIdentity,
    };

    public static string Singular(ScopeMemberKind kind) => kind switch
    {
        ScopeMemberKind.UserStory => "user story", ScopeMemberKind.AcceptanceCriterion => "acceptance criterion", ScopeMemberKind.TestSuite => "test suite",
        ScopeMemberKind.TestCase => "test case", ScopeMemberKind.TestDataAsset => "Test Data asset", _ => kind.ToString().ToLowerInvariant(),
    };

    public static string Plural(ScopeMemberKind kind) => kind switch
    {
        ScopeMemberKind.UserStory => "user stories", ScopeMemberKind.AcceptanceCriterion => "acceptance criteria", _ => Singular(kind) + "s",
    };
}
