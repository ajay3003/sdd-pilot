using System.Globalization;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.AltinnTesting;

/// <summary>
/// The App API addresses of one approved app, built from the operation paths of its current contract: the deployed OpenAPI document when it
/// was read, otherwise app-lib's documented default paths (labelled as such). Placeholders are filled only with a positive numeric party id
/// and GUIDs, and every address must stay on the approved app's TT02 host and under its own <c>/{org}/{app}/</c> path — a contract can never
/// point a request elsewhere.
/// </summary>
public sealed class AltinnAppEndpoints
{
    public const string DeployedSource = "Deployed OpenAPI contract";
    public const string DefaultSource = "App-lib default paths (no deployed OpenAPI contract read)";

    private readonly Uri _appBase;
    private readonly string _prefix;
    private readonly IReadOnlyDictionary<string, AltinnAppOperation> _operations;

    private AltinnAppEndpoints(Uri appBase, string appId, IReadOnlyList<AltinnAppOperation> operations, string source)
    {
        _appBase = appBase;
        _prefix = $"/{appId}/";
        _operations = operations.GroupBy(item => item.Key, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        Source = source;
    }

    public string Source { get; }
    public Uri AppBase => _appBase;
    public IReadOnlyList<AltinnAppOperation> Operations => [.. _operations.Values];

    /// <summary>app-lib's documented App API paths, used only when no deployed contract was read.</summary>
    public static IReadOnlyList<AltinnAppOperation> Defaults(string appId) =>
    [
        new(AltinnAppOperationKeys.CreateInstance, "POST", $"/{appId}/instances"),
        new(AltinnAppOperationKeys.GetInstance, "GET", $"/{appId}/instances/{{instanceOwnerPartyId}}/{{instanceGuid}}"),
        new(AltinnAppOperationKeys.CreateData, "POST", $"/{appId}/instances/{{instanceOwnerPartyId}}/{{instanceGuid}}/data"),
        new(AltinnAppOperationKeys.UpdateData, "PUT", $"/{appId}/instances/{{instanceOwnerPartyId}}/{{instanceGuid}}/data/{{dataGuid}}"),
        new(AltinnAppOperationKeys.Validate, "GET", $"/{appId}/instances/{{instanceOwnerPartyId}}/{{instanceGuid}}/validate"),
        new(AltinnAppOperationKeys.ProcessNext, "PUT", $"/{appId}/instances/{{instanceOwnerPartyId}}/{{instanceGuid}}/process/next"),
    ];

    /// <summary>The operations in effect for a contract: the deployed contract's (each required one must be declared), or the defaults without one.</summary>
    public static (IReadOnlyList<AltinnAppOperation> Operations, string Source, IReadOnlyList<string> Missing) For(string appId, AltinnDeployedProcessContract? deployed)
    {
        if (deployed is null) return (Defaults(appId), DefaultSource, []);
        var missing = AltinnAppOperationKeys.Required.Where(key => deployed.Operations.All(item => item.Key != key))
            .Select(key => $"The deployed OpenAPI contract does not declare the {key} operation; BirkNext does not guess its path.").ToArray();
        return (deployed.Operations, DeployedSource, missing);
    }

    public static AltinnAppEndpoints Create(Uri appBase, string appId, IReadOnlyList<AltinnAppOperation> operations, string source) => new(appBase, appId, operations, source);

    public Uri CreateInstance(long partyId) => Build(AltinnAppOperationKeys.CreateInstance, null, null, null, $"instanceOwnerPartyId={Party(partyId)}");
    public Uri Instance(long partyId, Guid instanceGuid) => Build(AltinnAppOperationKeys.GetInstance, partyId, instanceGuid, null, null);
    public Uri CreateData(long partyId, Guid instanceGuid, string dataTypeId) => Build(AltinnAppOperationKeys.CreateData, partyId, instanceGuid, null, $"dataType={Uri.EscapeDataString(dataTypeId)}");
    public Uri UpdateData(long partyId, Guid instanceGuid, Guid dataGuid) => Build(AltinnAppOperationKeys.UpdateData, partyId, instanceGuid, dataGuid, null);
    public Uri Validate(long partyId, Guid instanceGuid) => Build(AltinnAppOperationKeys.Validate, partyId, instanceGuid, null, null);
    public Uri ProcessNext(long partyId, Guid instanceGuid) => Build(AltinnAppOperationKeys.ProcessNext, partyId, instanceGuid, null, null);

    /// <summary>Fills the template's placeholders in order (party, instance, data element) and checks the result stays inside the app.</summary>
    private Uri Build(string key, long? partyId, Guid? instanceGuid, Guid? dataGuid, string? query)
    {
        if (!_operations.TryGetValue(key, out var operation)) throw new AltinnContractException($"The app contract does not declare the {key} operation.");
        var values = new Queue<string>(new[] { partyId is { } party ? Party(party) : null, instanceGuid?.ToString("D"), dataGuid?.ToString("D") }.OfType<string>());
        var segments = operation.PathTemplate.Split('/');
        for (var index = 0; index < segments.Length; index++)
        {
            if (!AltinnDeployedOpenApiReader.IsPlaceholder(segments[index])) continue;
            if (!values.TryDequeue(out var value)) throw new AltinnContractException($"The {key} path has more parameters than BirkNext supplies.");
            segments[index] = value;
        }
        if (values.Count > 0) throw new AltinnContractException($"The {key} path has fewer parameters than BirkNext supplies.");
        var path = string.Join('/', segments);
        if (!path.StartsWith(_prefix, StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal) || path.Contains("//", StringComparison.Ordinal))
            throw new AltinnContractException($"The {key} path is outside the approved app.");
        var uri = new Uri(new Uri(_appBase.GetLeftPart(UriPartial.Authority)), query is null ? path : $"{path}?{query}");
        if (!string.Equals(uri.Host, _appBase.Host, StringComparison.OrdinalIgnoreCase) || uri.Scheme != Uri.UriSchemeHttps)
            throw new AltinnContractException($"The {key} address is not on the approved app host.");
        return uri;
    }

    private static string Party(long partyId) => partyId > 0 ? partyId.ToString(CultureInfo.InvariantCulture) : throw new ArgumentOutOfRangeException(nameof(partyId));
}
