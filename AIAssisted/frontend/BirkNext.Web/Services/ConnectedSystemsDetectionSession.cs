using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>
/// Detected (not registered) Connected Systems candidates per Target Environment for this browser session, so a detection survives page
/// navigation. Detection is still proposal-only: nothing here is saved to the environment, and accepting stays explicit. Ignored candidates
/// are hidden until the next detection. Keyed by environment id, so a candidate never appears under another environment.
/// </summary>
public sealed class ConnectedSystemsDetectionSession
{
    public sealed record Detection(DateTimeOffset DetectedAt, IReadOnlyList<ExternalIntegrationProposal> Proposals, IReadOnlySet<string> Ignored);

    private readonly Dictionary<string, Detection> _byEnvironment = new(StringComparer.Ordinal);

    public Detection? Get(string environmentId) => _byEnvironment.GetValueOrDefault(environmentId);

    public void Store(string environmentId, IReadOnlyList<ExternalIntegrationProposal> proposals, DateTimeOffset at) =>
        _byEnvironment[environmentId] = new(at, [.. proposals], new HashSet<string>(StringComparer.Ordinal));

    /// <summary>Removes one candidate (accepted, or registered elsewhere); the rest stay.</summary>
    public void Remove(string environmentId, ExternalIntegrationProposal proposal)
    {
        if (_byEnvironment.TryGetValue(environmentId, out var d))
            _byEnvironment[environmentId] = d with { Proposals = [.. d.Proposals.Where(p => p != proposal)] };
    }

    public void Ignore(string environmentId, ExternalIntegrationProposal proposal)
    {
        if (_byEnvironment.TryGetValue(environmentId, out var d))
            _byEnvironment[environmentId] = d with { Ignored = new HashSet<string>(d.Ignored.Append(Key(proposal)), StringComparer.Ordinal) };
    }

    /// <summary>The candidates still to review: not ignored this session.</summary>
    public IReadOnlyList<ExternalIntegrationProposal> Pending(string environmentId) =>
        Get(environmentId) is { } d ? [.. d.Proposals.Where(p => !d.Ignored.Contains(Key(p)))] : [];

    public static string Key(ExternalIntegrationProposal p) => $"{p.ProviderType}|{p.Name}|{p.DetectedFrom}";
}
