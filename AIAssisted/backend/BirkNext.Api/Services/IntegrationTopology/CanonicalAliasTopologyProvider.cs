using BirkNext.Api.Services.CanonicalInterpretation;

namespace BirkNext.Api.Services.IntegrationTopology;

/// <summary>
/// Shared aliases from the canonical interpretation store (explicit, or confirmed by a reviewer in this project): differently named sources of the
/// same system merge exactly as a profile alias would. Proposed and rejected aliases are never applied. The project scope is the environment id.
/// </summary>
public sealed class CanonicalAliasTopologyProvider(CanonicalAliasSource aliases) : ITopologyProvider
{
    public int Order => 0;

    public async Task ContributeAsync(TopologyContext context, TopologyBuilder builder, CancellationToken ct)
    {
        foreach (var group in (await aliases.AliasesAsync(context.EnvironmentId, ct)).GroupBy(a => a.Canonical, StringComparer.Ordinal))
            builder.Alias(group.Key, group.Select(a => a.Alias));
    }
}
