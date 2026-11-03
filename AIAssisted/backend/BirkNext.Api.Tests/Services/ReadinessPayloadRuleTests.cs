using System.Collections;
using System.Reflection;
using BirkNext.Integrations;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services;

/// <summary>
/// Readiness snapshots carry references and summaries, never evidence blobs. A readiness response is loaded on every review page visit; one
/// Source Analysis snapshot is megabytes, so a readiness contract that can reach one grows with the evidence instead of with the decision
/// (IQR readiness was 20 MB from eight of them). Evidence is fetched by id when a person opens it.
/// </summary>
public sealed class ReadinessPayloadRuleTests
{
    /// <summary>Full evidence payloads: each is fetched by id, never embedded in a readiness contract.</summary>
    private static readonly Type[] EvidenceBlobs =
    [
        typeof(IqrSourceSnapshot),
        typeof(BirkNext.SourceArchitecture.ArchitectureSnapshot),
        typeof(BirkNext.DatabaseArchitecture.DatabaseArchitectureSnapshot),
        typeof(BirkNext.SourceDomains.SourceEvidenceDomainsSnapshot),
        typeof(BirkNext.TestEvidence.SourceTestInventory),
    ];

    public static TheoryData<string> ReadinessContracts()
    {
        var data = new TheoryData<string>();
        foreach (var type in typeof(IntegrationReviewReadiness).Assembly.GetTypes()
                     .Where(t => t.IsPublic && !t.IsGenericTypeDefinition && t.Name.EndsWith("Readiness", StringComparison.Ordinal) && (t.IsClass || t.IsValueType))
                     .OrderBy(t => t.FullName, StringComparer.Ordinal))
            data.Add(type.FullName!);
        return data;
    }

    [Theory]
    [MemberData(nameof(ReadinessContracts))]
    public void A_readiness_contract_never_reaches_a_full_evidence_payload(string typeName)
    {
        var type = typeof(IntegrationReviewReadiness).Assembly.GetType(typeName)!;
        Reachable(type).Should().BeEmpty($"{type.Name} is a readiness contract: it carries references and summaries; evidence is fetched by id");
    }

    [Fact]
    public void The_rule_sees_the_contracts_it_guards()
    {
        ReadinessContracts().Select(row => (string)row[0]).Should().Contain(typeof(IntegrationReviewReadiness).FullName!);
        Reachable(typeof(Holder)).Should().ContainSingle(path => path.EndsWith(nameof(IqrSourceSnapshot), StringComparison.Ordinal), "the walk finds a blob inside a list");
    }

    [Fact]
    public void A_snapshot_summary_keeps_counts_and_caps_the_limitations_it_carries()
    {
        var snapshot = new IqrSourceSnapshot { Archive = new("repo.zip", new string('a', 64), 1), Limitations = [.. Enumerable.Range(0, 12).Select(i => $"limitation {i}")] };

        var summary = IqrSourceSnapshotSummary.From(snapshot);

        summary.Id.Should().Be(snapshot.Id);
        summary.LimitationCount.Should().Be(12);
        summary.Limitations.Should().HaveCount(IqrSourceSnapshotSummary.MaxLimitations);
    }

    private sealed class Holder { public List<IqrSourceSnapshot> Items { get; init; } = []; }

    /// <summary>Property paths from <paramref name="root"/> to an evidence blob (through lists, dictionaries and nullable wrappers).</summary>
    private static List<string> Reachable(Type root)
    {
        var found = new List<string>();
        var seen = new HashSet<Type>();
        void Walk(Type type, string path)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (EvidenceBlobs.Contains(type)) { found.Add($"{path} → {type.Name}"); return; }
            if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) || type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true && !type.IsGenericType) return;
            if (type.IsArray) { Walk(type.GetElementType()!, path + "[]"); return; }
            if (type.IsGenericType && typeof(IEnumerable).IsAssignableFrom(type))
            {
                foreach (var argument in type.GetGenericArguments()) Walk(argument, path + "[]");
                return;
            }
            if (!seen.Add(type)) return;
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0))
                Walk(property.PropertyType, $"{path}.{property.Name}");
        }
        Walk(root, root.Name);
        return found;
    }
}
