using System.Text.Json.Serialization;

namespace BirkNext.Web.Models;

public sealed class DiscoveryEvidence
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum EvidenceType
    {
        StructuredConfig,   // From config file (appsettings.json, .env, etc.)
        HtmlReference,      // From HTML (script tag, meta tag, config object)
        WasmAsset,          // From deployed WASM asset/bundle
        HintExtractor,      // From source project hints
        SafeProbe,          // From safe endpoint verification
        ConventionalCandidate
    }

    [JsonPropertyName("type")]
    public EvidenceType Type { get; set; }

    [JsonPropertyName("locationCategory")]
    public string LocationCategory { get; set; } = ""; // appsettings.json, HTML, /swagger/v1/swagger.json, etc.

    [JsonPropertyName("value")]
    public string? Value { get; set; } // Non-sensitive discovered value

    [JsonPropertyName("confidence")]
    public DetectionConfidence Confidence { get; set; }

    [JsonPropertyName("targetField")]
    public string? TargetField { get; set; } // Which field this evidence supports (RestBaseUrl, GraphQlEndpoint, etc.)

    public EndpointEvidenceStatus Status { get; set; } = EndpointEvidenceStatus.Candidate;
    public EndpointProbeStatus ProbeStatus { get; set; } = EndpointProbeStatus.NotPerformed;
    public int? HttpStatus { get; set; }
    public string? ContentType { get; set; }
    public OpenApiResourceKind OpenApiKind { get; set; } = OpenApiResourceKind.Unknown;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EndpointEvidenceStatus { Candidate, Observed, Confirmed }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EndpointProbeStatus { NotPerformed, ResponseReceived, Blocked, Timeout, Failed, SizeLimitExceeded }
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OpenApiResourceKind { Unknown, SwaggerUi, OpenApiDocument }
