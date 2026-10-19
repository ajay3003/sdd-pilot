using System.Text.Json;
using System.Text.RegularExpressions;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.Integrations;

/// <summary>
/// Credential and personal-identifier protection for message-flow review configuration, per field: every user-entered value of the test
/// configuration and the documented flow is judged on its own (its content, never the field's name), so a reference field called "Key
/// reference" or a mechanism such as "Bearer JWT" or "id-porten" is accepted, while credential material — a JWT, a bearer token value, a
/// password/secret assignment, a private key or certificate block, a connection string or URL carrying credentials, a SAS signature, a long
/// opaque secret-shaped string, an 11-digit personal identifier — is rejected with the field that holds it. System identifiers and
/// timestamps are not user text and are not judged. The rejected value is never echoed.
/// </summary>
public static class MessageFlowCredentialGuard
{
    public const string Summary = "Credential-like or raw personal-identifier-like values are not accepted. Store references only.";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The configuration's fields as the form names them (section, label), by JSON property name.</summary>
    private static readonly Dictionary<string, (string Section, string Field)> ConfigurationFields = new(StringComparer.Ordinal)
    {
        ["productOrServiceType"] = ("Application", "Altinn product / service type"), ["appIdentifier"] = ("Application", "App identifier"),
        ["baseUrlReference"] = ("Application", "Safe base URL reference"), ["endpointReference"] = ("Application", "Endpoint reference"),
        ["tenantReference"] = ("Authentication", "Tenant reference"), ["clientReference"] = ("Authentication", "Client / app reference"),
        ["documentedScopes"] = ("Authentication", "Documented scopes"), ["configuredScopes"] = ("Authentication", "Configured scopes"),
        ["certificateReference"] = ("Authentication", "Certificate reference"), ["keyReference"] = ("Authentication", "Key reference"),
        ["reporterType"] = ("Reporter & Test Data", "Test reporter type"), ["reporterReference"] = ("Reporter & Test Data", "Test reporter reference"),
        ["syntheticIdentitySetReference"] = ("Reporter & Test Data", "Approved synthetic identity-set reference"),
        ["muChannelReference"] = ("Message transport", "MU channel reference"), ["messageType"] = ("Message transport", "Message type"),
        ["queueReference"] = ("Message transport", "Queue reference"), ["payloadStoreReference"] = ("Message transport", "Payload store reference"),
        ["contractReference"] = ("Contracts & correlation", "Contract reference"), ["contractVersion"] = ("Contracts & correlation", "Contract version"),
        ["receiptMode"] = ("Advanced configuration", "Receipt mode/reference"), ["checkpointConfigurationReference"] = ("Advanced configuration", "Checkpoint configuration reference"),
        ["correlationMappingReference"] = ("Advanced configuration", "Correlation mapping reference"),
        ["documentationReferences"] = ("Advanced configuration", "Official documentation URLs/references"), ["documentationPlatform"] = ("Advanced configuration", "Documentation platform/product"),
    };

    private static readonly (Regex Pattern, string Reason)[] Rules =
    [
        (new(@"eyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.?", RegexOptions.CultureInvariant), "it appears to contain a JSON Web Token"),
        (new(@"(?i)\bbearer\s+(?!jwt\b)[A-Za-z0-9\-._~+/]{16,}=*", RegexOptions.CultureInvariant), "it appears to contain a bearer token value"),
        (new(@"-----BEGIN [A-Z ]+-----", RegexOptions.CultureInvariant), "it appears to contain key or certificate material"),
        (new(@"(?i)\b(password|passwd|pwd|client[_-]?secret|secret|access[_-]?token|refresh[_-]?token|id[_-]?token|private[_-]?key|account[_-]?key|shared[_-]?access[_-]?key|api[_-]?key|shared[_-]?access[_-]?signature)\s*[:=]\s*\S",
            RegexOptions.CultureInvariant), "it appears to assign a password, secret, key or token"),
        (new(@"(?i)https?://[^/\s:@]+:[^/\s@]+@", RegexOptions.CultureInvariant), "the URL carries credentials"),
        (new(@"(?i)[?&](sig|token|access_token|code|key|password|secret)=", RegexOptions.CultureInvariant), "the URL carries a signature, token or key in its query (SAS-like)"),
        (new(@"(?<!\d)\d{11}(?!\d)", RegexOptions.CultureInvariant), "it appears to contain an 11-digit personal identifier"),
    ];

    /// <summary>Every rejected field of the configuration and the documented flow; empty when the package may be stored.</summary>
    public static IReadOnlyList<ConfigurationFieldError> Validate(IntegrationMessageFlowPackage package)
    {
        var errors = new List<ConfigurationFieldError>();
        using (var configuration = JsonDocument.Parse(JsonSerializer.Serialize(package.AltinnConfiguration, Json)))
            foreach (var property in configuration.RootElement.EnumerateObject())
            {
                if (!ConfigurationFields.TryGetValue(property.Name, out var field)) continue;
                foreach (var value in Strings(property.Value))
                    if (Reason(value) is { } reason) { errors.Add(new(field.Section, field.Field, $"altinnConfiguration.{property.Name}", $"{field.Field} was rejected because {reason}.")); break; }
            }
        // The documented flow: its free-text fields (names, descriptions, policies …). Ids, timestamps and enum states are not user text.
        using (var flow = JsonDocument.Parse(JsonSerializer.Serialize(package.Flow, Json)))
            Walk(flow.RootElement, "flow", errors);
        return errors;
    }

    /// <summary>Why a value is credential material, or null when it is safe reference metadata.</summary>
    public static string? Reason(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        foreach (var (pattern, reason) in Rules) if (pattern.IsMatch(value)) return reason;
        return OpaqueSecret(value.Trim()) ? "it looks like a raw secret value rather than a reference" : null;
    }

    /// <summary>A long single token mixing upper case, lower case and digits with secret-alphabet symbols (no dots, no spaces): the shape of a
    /// generated client secret or key. GUIDs, thumbprints, names, URLs and dotted identifiers are references, not secrets.</summary>
    private static bool OpaqueSecret(string value) =>
        value.Length >= 32 && Regex.IsMatch(value, @"^[A-Za-z0-9+/=_~-]+$") && !Guid.TryParse(value, out _)
        && Regex.IsMatch(value, "[a-z]") && Regex.IsMatch(value, "[A-Z]") && Regex.IsMatch(value, "[0-9]");

    private static IEnumerable<string> Strings(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => [element.GetString() ?? ""],
        JsonValueKind.Array => element.EnumerateArray().SelectMany(Strings),
        _ => [],
    };

    private static void Walk(JsonElement element, string path, List<ConfigurationFieldError> errors)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name.Equals("id", StringComparison.OrdinalIgnoreCase) || property.Name.EndsWith("Id", StringComparison.Ordinal)
                        || property.Name.EndsWith("At", StringComparison.Ordinal) || property.Name.EndsWith("On", StringComparison.Ordinal)) continue;
                    Walk(property.Value, $"{path}.{property.Name}", errors);
                }
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray()) Walk(item, $"{path}[{index++}]", errors);
                break;
            case JsonValueKind.String when Reason(element.GetString() ?? "") is { } reason && errors.All(error => error.Path != path):
                errors.Add(new("Message flow", path["flow.".Length..], path, $"The documented flow value at {path["flow.".Length..]} was rejected because {reason}."));
                break;
        }
    }
}
