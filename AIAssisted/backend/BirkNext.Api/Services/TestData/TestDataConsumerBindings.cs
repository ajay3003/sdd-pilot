using BirkNext.TestData;

namespace BirkNext.Api.Services.TestData;

/// <summary>One Test Data need a consumer declares: its stable key, how the consumer uses the data, whether it is required, and the requirement
/// the consumer itself resolves with (the same one its runs use).</summary>
public sealed record TestDataConsumerNeed(string Key, string Usage, bool Required, TestDataDataType DataType, TestDataRequirement Requirement, string? Description = null);

/// <summary>A consumer (e.g. a Test Pack integration) and the Test Data it consumes.</summary>
public sealed record TestDataConsumerDeclaration(TestDataConsumer Consumer, IReadOnlyList<TestDataConsumerNeed> Needs);

/// <summary>Consumers that declare the Test Data they consume (implemented by owners outside the generic Test Data model).</summary>
public interface ITestDataConsumerDeclarations
{
    IReadOnlyList<TestDataConsumerDeclaration> Consumers { get; }
}

/// <summary>
/// Persists what declared consumers consume, so usage is counted from stable references — never from names, roles or suggestions:
/// <list type="bullet">
/// <item>each declared need becomes a registered requirement (deterministic id, registered once);</item>
/// <item>a need without a usage is bound to the asset the consumer's own resolver chooses — only when that choice is deterministic (exactly one
/// qualifying approved asset, or one default) — by its stable asset id;</item>
/// <item>an existing usage is never re-bound: a reference to an asset that later disappears stays as it is and shows as unresolved, and a choice
/// changed in Test Data is kept.</item>
/// </list>
/// Idempotent: running it again changes nothing.
/// </summary>
public static class TestDataConsumerBindings
{
    public const string ResolvedByConsumer = "Resolved by the consumer (the only qualifying approved asset, or its default)";

    public static string RequirementId(TestDataConsumer consumer, TestDataConsumerNeed need) => $"declared:{consumer.ConsumerId}:{need.Key}";

    public static bool Ensure(TestDataRegistryStore registry, TestDataCatalog catalog, IEnumerable<ITestDataConsumerDeclarations> declarations, DateTimeOffset now)
    {
        var changed = false;
        var requirements = registry.Requirements().Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var bound = registry.Usages().Where(u => u.RequirementId is not null).Select(u => u.RequirementId!).ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<TestDataAsset>? assets = null;
        foreach (var declaration in declarations.SelectMany(d => d.Consumers))
            foreach (var need in declaration.Needs)
            {
                var id = RequirementId(declaration.Consumer, need);
                if (requirements.Add(id))
                {
                    registry.AddRequirement(new ConsumerTestDataRequirement
                    {
                        Id = id, Consumer = declaration.Consumer, Usage = need.Usage, Required = need.Required, RequestedDataType = need.DataType,
                        Description = need.Description, RegisteredAt = now, Status = TestDataRequirementStatus.Missing,
                    });
                    changed = true;
                }
                if (bound.Contains(id)) continue;
                if (catalog.Resolve([need.Requirement]).Role(need.Requirement.Role) is not { State: TestDataResolutionState.Resolved, AssetAlias: { } alias }) continue;
                assets ??= catalog.Assets();
                if (assets.FirstOrDefault(a => string.Equals(a.Alias, alias, StringComparison.Ordinal) && a.Approval == TestDataApproval.Approved) is not { } asset) continue;
                registry.Bind(new TestDataUsageReference
                {
                    Id = $"use-{Guid.NewGuid():N}"[..16], AssetId = asset.Id, Consumer = declaration.Consumer, Usage = need.Usage, EnvironmentId = asset.Environment,
                    RequirementId = id, Mapping = ResolvedByConsumer, BoundAt = now, Status = TestDataUsageStatus.Resolved,
                });
                bound.Add(id);
                changed = true;
            }
        return changed;
    }
}
