namespace BirkNext.Api.Services.ApiQuality;

/// <summary>A stored contract version for API Quality Review. <see cref="Json"/> is the OpenAPI document as JSON (YAML converted), null when the
/// version has no document text (imported from a Source Analysis snapshot) or is a GraphQL schema.</summary>
public sealed record StoredContract(string Key, int Version, string Protocol, string Text, string? Json);

/// <summary>Reads stored contract versions ("key@vN", or "key" for the latest) from the contract store, so AQR uses the same contract evidence as
/// contract tests instead of a second copy.</summary>
public interface IStoredContractSource
{
    Task<StoredContract?> GetAsync(string contractRef, CancellationToken ct);
}
