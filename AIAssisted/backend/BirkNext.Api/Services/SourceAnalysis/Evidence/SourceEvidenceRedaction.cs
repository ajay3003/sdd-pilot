using System.Text.RegularExpressions;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Integrations;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.SourceAnalysis.Evidence;

/// <summary>
/// The one sensitivity rule set of the evidence domains (configuration, Terraform variables/settings, pipeline variables, Helm values,
/// Kubernetes manifests, contract examples). Conservative: a key that names a password, secret, token, key, connection string, private key,
/// certificate or authorization value is sensitive whatever its value; a value shaped like a credential is sensitive whatever its key.
/// Sensitive values are classified (Connection string / Secret / Secret reference) and never returned — not even partially or hashed.
/// </summary>
internal static class SourceEvidenceRedaction
{
    private static Regex R(string pattern) => new(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // Key segment (leaf) names that always hold sensitive values.
    private static readonly Regex SensitiveLeaf = R(@"(password|passwd|passphrase|pwd|secret|token|api[-_]?key|access[-_]?key|account[-_]?key|shared[-_]?access|sas|signing[-_]?key|private[-_]?key|client[-_]?certificate|certificate|cert|pfx|credentials?|authorization|connection[-_]?string|connstr|pat|subscription[-_]?key|instrumentation[-_]?key|master[-_]?key|encryption[-_]?key|username|user[-_]?id)$");
    private static readonly Regex SensitiveAnywhere = R(@"(password|passwd|secret|privatekey|private_key|connectionstring|apikey|api_key|accesstoken|access_token|clientsecret|client_secret)");
    private static readonly Regex ConnectionSection = R(@"(^|[:._])(connectionstrings?|connection_strings?)([:._]|$)");

    private static readonly Regex ConnectionShape = R(@"(^|;)\s*(server|data source|host|endpoint|accountname|defaultendpointsprotocol|user id|uid|username|database|initial catalog)\s*=.*;");
    private static readonly Regex CredentialShape = R(@"(accountkey|sharedaccesskey|sharedaccesssignature|password|pwd|sig|secret|token|apikey|client_secret)\s*=\s*[^;&\s]+");
    private static readonly Regex Pem = R(@"-----BEGIN [A-Z ]*(PRIVATE KEY|CERTIFICATE)-----");
    private static readonly Regex Jwt = new(@"\beyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{4,}", RegexOptions.Compiled);
    private static readonly Regex LongOpaque = new(@"^[A-Za-z0-9+/=_\-]{32,}$", RegexOptions.Compiled);
    private static readonly Regex Guid = new(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.Compiled);
    private static readonly Regex SecretReferenceShape = R(@"(@Microsoft\.KeyVault\(|^kv:|keyvault|vault\.azure\.net/secrets|\$\{\{\s*secrets\.|\bsecretKeyRef\b|aws:secretsmanager|projects/[^/]+/secrets/)");
    private static readonly Regex PlaceholderShape = new(@"^(\$\{[^}]+\}|\$\([^)]+\)|#\{[^}]+\}#?|__[A-Za-z0-9_]+__|\{\{[^}]+\}\}|<[^>]+>|%[A-Za-z0-9_]+%)$", RegexOptions.Compiled);
    private static readonly Regex SafeToken = new(@"^[A-Za-z0-9][A-Za-z0-9._\-/:@ ]{0,119}$", RegexOptions.Compiled);

    public static bool SensitiveKey(string key)
    {
        var leaf = Leaf(key);
        return SensitiveLeaf.IsMatch(leaf) || SensitiveAnywhere.IsMatch(leaf.Replace("-", "").Replace("_", "")) || ConnectionSection.IsMatch(key);
    }

    public static string Leaf(string key)
    {
        var trimmed = Regex.Replace(key, @"[:.]\d+$", "");
        var parts = trimmed.Split([':', '.', '/'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? key : parts[^1].Replace("__", "");
    }

    public static bool SecretShaped(string? value) => !string.IsNullOrEmpty(value) && (Pem.IsMatch(value) || Jwt.IsMatch(value) || CredentialShape.IsMatch(value)
        || IntegrationRuntimeEvidenceSettings.LooksLikeSecret(value) || (LongOpaque.IsMatch(value) && !Guid.IsMatch(value) && value.Any(char.IsDigit) && value.Any(char.IsLetter)));

    public static bool ConnectionShaped(string? value) => !string.IsNullOrEmpty(value) && ConnectionShape.IsMatch(value + ";");

    public static bool SecretReference(string? value) => !string.IsNullOrEmpty(value) && SecretReferenceShape.IsMatch(value.Trim());

    public static bool Placeholder(string? value) => !string.IsNullOrEmpty(value) && PlaceholderShape.IsMatch(value.Trim());

    /// <summary>Classifies a raw value. The raw value never leaves this method; only a kind, a sensitivity and (for safe kinds) a preview do.</summary>
    public static (ConfigurationValueKind Kind, ConfigurationSensitivity Sensitivity, string? Preview) Classify(string key, string? raw)
    {
        var value = raw?.Trim() ?? "";
        if (value.Length == 0) return (ConfigurationValueKind.Empty, SensitiveKey(key) ? ConfigurationSensitivity.Sensitive : ConfigurationSensitivity.None, null);
        if (SecretReference(value) && !ConnectionShaped(value)) return (ConfigurationValueKind.SecretReference, ConfigurationSensitivity.SecretReference, SecretReferenceLabel(value));
        if (ConnectionShaped(value) || (ConnectionSection.IsMatch(key) && !Placeholder(value)))
            return (ConfigurationValueKind.ConnectionString, ConfigurationSensitivity.Sensitive, null);
        if (SensitiveKey(key) || SecretShaped(value))
            return Placeholder(value) ? (ConfigurationValueKind.Placeholder, ConfigurationSensitivity.Sensitive, null) : (ConfigurationValueKind.Secret, ConfigurationSensitivity.Sensitive, null);
        if (Placeholder(value)) return (ConfigurationValueKind.Placeholder, ConfigurationSensitivity.None, Safe(value));
        if (value is "true" or "false" or "True" or "False") return (ConfigurationValueKind.Boolean, ConfigurationSensitivity.None, value.ToLowerInvariant());
        if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _) && value.Length <= 24)
            return (ConfigurationValueKind.Number, ConfigurationSensitivity.None, value);
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "sb" or "amqp" or "amqps" or "ws" or "wss" or "grpc" or "redis" or "rediss")
            return string.IsNullOrEmpty(uri.UserInfo) ? (ConfigurationValueKind.Url, ConfigurationSensitivity.None, $"{uri.Scheme}://{uri.Authority}{uri.AbsolutePath.TrimEnd('/')}")
                : (ConfigurationValueKind.Secret, ConfigurationSensitivity.Sensitive, null);
        if (Guid.IsMatch(value)) return (ConfigurationValueKind.Identifier, ConfigurationSensitivity.None, value.ToLowerInvariant());
        if (ArchitectureSafeEntity(key, value) is { } entity) return (ConfigurationValueKind.EntityName, ConfigurationSensitivity.None, entity);
        if (value.StartsWith('[') || value.StartsWith('{')) return (ConfigurationValueKind.Collection, ConfigurationSensitivity.None, null);
        return SafeToken.IsMatch(value) && value.Length <= 80 ? (ConfigurationValueKind.Text, ConfigurationSensitivity.None, Safe(value)) : (ConfigurationValueKind.Text, ConfigurationSensitivity.None, null);
    }

    /// <summary>A safe literal for an infrastructure/pipeline setting value: bool, number or short identifier token. Otherwise null.</summary>
    public static string? SafeLiteral(string key, string? raw)
    {
        var (kind, sensitivity, preview) = Classify(key, raw);
        return sensitivity != ConfigurationSensitivity.None ? null : kind is ConfigurationValueKind.Boolean or ConfigurationValueKind.Number or ConfigurationValueKind.Url
            or ConfigurationValueKind.EntityName or ConfigurationValueKind.Text or ConfigurationValueKind.Identifier ? preview : null;
    }

    private static string? ArchitectureSafeEntity(string key, string value) => SourceArchitecture.ArchitectureText.SafeValue(key, value) is { } safe && !safe.Contains("://") ? safe : null;

    private static string SecretReferenceLabel(string value) => value switch
    {
        _ when value.Contains("KeyVault", StringComparison.OrdinalIgnoreCase) || value.Contains("vault.azure.net", StringComparison.OrdinalIgnoreCase) => "Key Vault reference",
        _ when value.Contains("secrets.", StringComparison.OrdinalIgnoreCase) => "Pipeline secret reference",
        _ when value.Contains("secretKeyRef", StringComparison.OrdinalIgnoreCase) => "Kubernetes secret reference",
        _ when value.Contains("secretsmanager", StringComparison.OrdinalIgnoreCase) || value.Contains("/secrets/", StringComparison.OrdinalIgnoreCase) => "Cloud secret manager reference",
        _ => "Variable reference",
    };

    /// <summary>A display-safe label (paths, names): redacted and character-restricted by the archive reader's rule.</summary>
    public static string Safe(string value) => IqrSourceArchiveReader.SafeLabel(value);

    /// <summary>A display-safe path/route/glob/version label: the same redaction, but keeps the characters such labels need (* ** {id} ~> @2 ^).
    /// Only for structural labels (trigger filters, API routes, task names, version constraints) — never for values.</summary>
    public static string SafePath(string value)
    {
        var safe = LocalHttpsProxy.SensitiveDataRedactor.RedactText(DependencyReview.DependencyEvidenceRedaction.Redact(value));
        safe = Regex.Replace(safe, @"(?i)(SECRET_SENTINEL\w*|(?:password|clientsecret|accesskey|sharedaccesskey|token)[_=][^/\s]+)", "[redacted]");
        safe = Regex.Replace(safe, @"[^\p{L}\p{N}_./<>\[\]{}*~^=@$() `:+?,\-#!|]", "_");
        return safe.Length > 300 ? safe[..300] : safe;
    }
}
