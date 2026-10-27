using System.Text.RegularExpressions;
using BirkNext.Api.Services.AltinnTesting;
using BirkNext.Api.Services.Integrations;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.ExternalIntegrations;

/// <summary>
/// The providers BirkNext knows and their field definitions. Adding a provider means a value here (fields + capabilities), optionally a
/// dedicated handler and UI component — the External Integrations page, Setup Library, import/export and preflight stay unchanged.
/// </summary>
public static class ExternalProviderCatalog
{
    private const string App = "Application", Auth = "Authentication", Conn = "Connection", Refs = "References";
    private static readonly string[] AuthSchemes = ["None", "OAuth2ClientCredentials", "ManagedIdentity", "ApiKeyHeader", "MutualTls"];
    private static readonly string[] AzureAuth = ["ManagedIdentity", "ServicePrincipal", "SharedAccessSignature"];

    public static readonly IReadOnlyList<ExternalProviderDescriptor> Descriptors =
    [
        new(ExternalProviderType.Altinn, "Altinn", "An Altinn app in a test environment (TT02): application, form contract and the unattended ID-porten TEST client.",
        [
            new("environment", "Altinn environment", App, ProviderFieldKind.Choice, true, true, "Test (TT02). Production is never configured.", ["Test", "Development", "QualityAssurance", "Staging"]),
            new("appId", "App identifier (org/app)", App, ProviderFieldKind.Text, true, true, "e.g. buf/skolenaervaer"),
            new("baseUrl", "App base URL", App, ProviderFieldKind.Url, false, true),
            new("formDataType", "Form data type", App, ProviderFieldKind.Text, false, true),
            new("contentType", "Content type", App, ProviderFieldKind.Text, false, true),
            new("clientId", "Unattended client ID", Auth, ProviderFieldKind.Text, false, true, "The registered ID-porten TEST client BirkNext uses — never the browser login client."),
            new("redirectUri", "Registered redirect URI", Auth, ProviderFieldKind.Url, false, true),
            new("scopes", "Scopes", Auth, ProviderFieldKind.List, false, true),
            new("unattendedEnabled", "Automated execution enabled", Auth, ProviderFieldKind.Boolean, false, false, "An explicit per-environment choice; never imported."),
            new("clientSecret", "Client secret", Auth, ProviderFieldKind.Text, false, false, "Session only; never stored or exported.", Secret: true),
            new("integrationId", "Integration (IQR)", Refs, ProviderFieldKind.Text, false, true, "The canonical IQR integration this app feeds (referenced, never copied)."),
        ], ["AltinnAppSubmission", "AltinnReportSubmission"], 1),
        new(ExternalProviderType.RestApi, "REST API", "An HTTP API described by OpenAPI.",
        [
            new("baseUrl", "Base URL", Conn, ProviderFieldKind.Url, true, true),
            new("openApiUrl", "OpenAPI / Swagger URL", Conn, ProviderFieldKind.Url, false, true),
            new("healthEndpoint", "Health endpoint", Conn, ProviderFieldKind.Url, false, true),
            new("contentTypes", "Content types", Conn, ProviderFieldKind.List, false, true),
            new("authScheme", "Auth scheme", Auth, ProviderFieldKind.Choice, false, true, "", AuthSchemes),
            new("clientId", "Client ID / reference", Auth, ProviderFieldKind.Text, false, true),
            new("scopes", "Scopes", Auth, ProviderFieldKind.List, false, true),
            new("clientSecret", "Client secret", Auth, ProviderFieldKind.Text, false, false, "Session only.", Secret: true),
        ], ["HttpApiCall", "OpenApiContract"], 20),
        new(ExternalProviderType.GraphQl, "GraphQL", "A GraphQL endpoint.",
        [
            new("endpoint", "Endpoint", Conn, ProviderFieldKind.Url, true, true),
            new("schemaUrl", "Schema URL", Conn, ProviderFieldKind.Url, false, true),
            new("authScheme", "Auth scheme", Auth, ProviderFieldKind.Choice, false, true, "", AuthSchemes),
            new("clientId", "Client ID / reference", Auth, ProviderFieldKind.Text, false, true),
            new("scopes", "Scopes", Auth, ProviderFieldKind.List, false, true),
            new("clientSecret", "Client secret", Auth, ProviderFieldKind.Text, false, false, "Session only.", Secret: true),
        ], ["GraphQlQuery"], 20),
        new(ExternalProviderType.AzureServiceBus, "Azure Service Bus", "A Service Bus namespace and entity.",
        [
            new("namespace", "Namespace", Conn, ProviderFieldKind.Text, true, true, "e.g. sb-example.servicebus.windows.net"),
            new("entityType", "Entity type", Conn, ProviderFieldKind.Choice, true, true, "", ["Queue", "Topic"]),
            new("entity", "Queue / topic", Conn, ProviderFieldKind.Text, true, true),
            new("subscription", "Subscription", Conn, ProviderFieldKind.Text, false, true),
            new("resourceId", "Resource identifier", Conn, ProviderFieldKind.Text, false, true),
            new("authMode", "Authentication mode", Auth, ProviderFieldKind.Choice, false, true, "", AzureAuth),
            new("clientReference", "Managed identity / client reference", Auth, ProviderFieldKind.Text, false, true),
            new("connectionSecret", "Connection secret", Auth, ProviderFieldKind.Text, false, false, "Session only; a connection string is never stored or exported.", Secret: true),
        ], ["ServiceBusPublish", "ServiceBusConsume"], 20),
        new(ExternalProviderType.AzureEventHub, "Azure Event Hub", "An Event Hubs namespace and hub.",
        [
            new("namespace", "Namespace", Conn, ProviderFieldKind.Text, true, true),
            new("hub", "Hub", Conn, ProviderFieldKind.Text, true, true),
            new("consumerGroup", "Consumer group", Conn, ProviderFieldKind.Text, false, true),
            new("resourceReference", "Resource reference", Conn, ProviderFieldKind.Text, false, true),
            new("checkpointStorage", "Checkpoint / storage reference", Conn, ProviderFieldKind.Text, false, true),
            new("authMode", "Authentication mode", Auth, ProviderFieldKind.Choice, false, true, "", AzureAuth),
            new("connectionSecret", "Connection secret", Auth, ProviderFieldKind.Text, false, false, "Session only.", Secret: true),
        ], ["EventHubPublish", "EventHubConsume"], 20),
        new(ExternalProviderType.Database, "Database", "A database server and database.",
        [
            new("engine", "Engine", Conn, ProviderFieldKind.Choice, true, true, "", ["SqlServer", "PostgreSql", "Other"]),
            new("server", "Server", Conn, ProviderFieldKind.Text, true, true),
            new("database", "Database", Conn, ProviderFieldKind.Text, true, true),
            new("authMode", "Authentication mode", Auth, ProviderFieldKind.Choice, false, true, "", ["ManagedIdentity", "ServicePrincipal", "SqlLogin"]),
            new("clientReference", "Client / login reference", Auth, ProviderFieldKind.Text, false, true),
            new("databaseSecret", "Database secret", Auth, ProviderFieldKind.Text, false, false, "Session only.", Secret: true),
        ], ["DatabaseRead"], 20),
        new(ExternalProviderType.FileSftp, "File / SFTP", "A file share or SFTP host.",
        [
            new("host", "Host", Conn, ProviderFieldKind.Text, true, true),
            new("port", "Port", Conn, ProviderFieldKind.Text, false, true),
            new("path", "Path", Conn, ProviderFieldKind.Text, false, true),
            new("authMode", "Authentication mode", Auth, ProviderFieldKind.Choice, false, true, "", ["Key", "Password"]),
            new("userReference", "User reference", Auth, ProviderFieldKind.Text, false, true),
            new("sftpSecret", "Key / password", Auth, ProviderFieldKind.Text, false, false, "Session only.", Secret: true),
        ], ["FileTransfer"], 20),
        new(ExternalProviderType.GenericHttp, "Generic HTTP", "Any HTTP endpoint without a contract.",
        [
            new("baseUrl", "Base URL", Conn, ProviderFieldKind.Url, true, true),
            new("healthEndpoint", "Health endpoint", Conn, ProviderFieldKind.Url, false, true),
            new("authScheme", "Auth scheme", Auth, ProviderFieldKind.Choice, false, true, "", AuthSchemes),
            new("clientId", "Client ID / reference", Auth, ProviderFieldKind.Text, false, true),
            new("clientSecret", "Client secret", Auth, ProviderFieldKind.Text, false, false, "Session only.", Secret: true),
        ], ["HttpCall"], 20),
        new(ExternalProviderType.Other, "Other", "A system BirkNext has no dedicated provider for (documented reference only).",
        [
            new("endpoint", "Endpoint / reference", Conn, ProviderFieldKind.Text, false, true),
            new("description", "Description", Conn, ProviderFieldKind.Text, false, true),
        ], [], 20),
    ];

    public static ExternalProviderDescriptor Descriptor(ExternalProviderType type) => Descriptors.Single(item => item.Type == type);

    public static bool TryParse(string? value, out ExternalProviderType type)
    {
        type = default;
        var normalized = new string([.. (value ?? "").Where(char.IsLetterOrDigit)]);
        foreach (var descriptor in Descriptors)
            if (string.Equals(descriptor.Type.ToString(), normalized, StringComparison.OrdinalIgnoreCase)
                || string.Equals(new string([.. descriptor.DisplayName.Where(char.IsLetterOrDigit)]), normalized, StringComparison.OrdinalIgnoreCase))
            { type = descriptor.Type; return true; }
        return false;
    }
}

/// <summary>Field rules shared by every provider: kind, choice, URL safety and no credential-looking values in non-secret fields.</summary>
public static partial class ProviderFieldValidation
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9 _.:/@()–\\-]{0,299}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeText();

    public static string? Problem(ProviderFieldDefinition field, string value)
    {
        var text = value.Trim();
        if (text.Length == 0) return null;
        if (field.Secret) return $"{field.Label} is a secret: it is added for the session only and never stored.";
        if (LooksLikeCredential(text)) return $"{field.Label}: a credential-like value is not accepted in a non-secret field.";
        return field.Kind switch
        {
            ProviderFieldKind.Url when !Uri.TryCreate(text, UriKind.Absolute, out var uri) || !(uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)
                => $"{field.Label} must be an absolute https URL (http only on loopback).",
            ProviderFieldKind.Url when Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.UserInfo.Length > 0 || AltinnTestingPolicy.IsProductionHost(uri.Host.ToLowerInvariant()))
                => $"{field.Label} must not contain user info or an Altinn/ID-porten Production host.",
            ProviderFieldKind.Choice when field.Choices is { } choices && !choices.Contains(text, StringComparer.OrdinalIgnoreCase)
                => $"{field.Label} must be one of {string.Join(", ", choices)}.",
            ProviderFieldKind.Boolean when !bool.TryParse(text, out _) => $"{field.Label} must be true or false.",
            ProviderFieldKind.List when text.Split(' ', ',').Any(item => item.Length > 0 && !SafeText().IsMatch(item)) => $"{field.Label} contains an unsupported value.",
            ProviderFieldKind.Text when !SafeText().IsMatch(text) => $"{field.Label} contains unsupported characters.",
            _ => null,
        };
    }

    public static bool LooksLikeCredential(string value) =>
        value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || value.Contains("eyJ", StringComparison.Ordinal)
        || Regex.IsMatch(value, @"(?i)(password|pwd|accountkey|sharedaccesskey|client_secret|sharedaccesssignature)\s*=") || value.Contains("-----BEGIN", StringComparison.Ordinal);

    /// <summary>Every non-secret setting checked against its provider's field definitions; unknown keys are refused.</summary>
    public static IReadOnlyList<string> Validate(ExternalProviderDescriptor descriptor, IReadOnlyDictionary<string, string> settings)
    {
        var problems = new List<string>();
        foreach (var (key, value) in settings)
        {
            var field = descriptor.Fields.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal));
            if (field is null) { problems.Add($"\"{key}\" is not a {descriptor.DisplayName} setting."); continue; }
            if (Problem(field, value) is { } problem) problems.Add(problem);
        }
        return problems;
    }
}

/// <summary>
/// One provider's behaviour behind the generic External Integrations surface: its fields, validation, detection and readiness, the setup
/// requirements it places on the destination, what is portable, and how a portable section is applied. A strategy, not a plugin runtime.
/// </summary>
public interface IExternalProviderHandler
{
    ExternalProviderType Type { get; }
    ExternalProviderDescriptor Descriptor => ExternalProviderCatalog.Descriptor(Type);
    /// <summary>The integration's non-secret settings as they are now (provider-typed storage included).</summary>
    Task<Dictionary<string, string>> CurrentSettingsAsync(ExternalIntegration integration, CancellationToken ct);
    IReadOnlyList<string> Validate(IReadOnlyDictionary<string, string> settings) => ProviderFieldValidation.Validate(Descriptor, settings);
    /// <summary>Resolved fields (state + provenance), readiness and, with <paramref name="detect"/>, discovery proposals. Never writes.</summary>
    Task<ExternalIntegrationView> AssessAsync(ExternalIntegration integration, bool detect, CancellationToken ct);
    /// <summary>What the destination must provide for this integration's setup (references and roles only).</summary>
    Task<IReadOnlyList<SetupDependency>> SetupRequirementsAsync(ExternalIntegration integration, IReadOnlyDictionary<string, string> settings, CancellationToken ct);
    /// <summary>Resolves a provider-specific dependency (e.g. an Altinn form contract), or null when the generic resolver owns the kind.</summary>
    Task<SetupDependencyResolution?> ResolveAsync(string environmentId, SetupDependency dependency, CancellationToken ct) => Task.FromResult<SetupDependencyResolution?>(null);
    /// <summary>Whether merged settings satisfy a capability this provider offers.</summary>
    bool Satisfies(string capability, IReadOnlyDictionary<string, string> settings) =>
        Descriptor.Capabilities.Contains(capability, StringComparer.Ordinal) && Descriptor.Fields.Where(field => field.Required && !field.Secret).All(field => settings.ContainsKey(field.Key));
    /// <summary>Persists provider-typed settings (the generic document is stored by the caller in the same unit of work).</summary>
    Task<IReadOnlyList<string>> ApplyAsync(ExternalIntegration integration, IReadOnlyDictionary<string, string> settings, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
    /// <summary>An integration BirkNext can propose for the environment from deterministic evidence, or none.</summary>
    Task<IReadOnlyList<ExternalIntegrationProposal>> ProposeAsync(string environmentId, IReadOnlyList<ExternalIntegration> existing, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ExternalIntegrationProposal>>([]);
}

/// <summary>
/// Every provider without dedicated behaviour: fields from its descriptor, configured values from the integration document, detection of a
/// single deterministic value from the environment's IQR integration catalog, secret availability for the session.
/// </summary>
public sealed class GenericProviderHandler(ExternalProviderType type, IIntegrationCatalogService catalog, AltinnSessionStore session) : IExternalProviderHandler
{
    public ExternalProviderType Type => type;
    private ExternalProviderDescriptor D => ExternalProviderCatalog.Descriptor(type);

    public Task<Dictionary<string, string>> CurrentSettingsAsync(ExternalIntegration integration, CancellationToken ct) =>
        Task.FromResult(new Dictionary<string, string>(integration.Settings, StringComparer.Ordinal));

    public Task<IReadOnlyList<SetupDependency>> SetupRequirementsAsync(ExternalIntegration integration, IReadOnlyDictionary<string, string> settings, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SetupDependency>>([]);

    public async Task<ExternalIntegrationView> AssessAsync(ExternalIntegration integration, bool detect, CancellationToken ct)
    {
        var detected = detect ? await CatalogValuesAsync(integration.TargetEnvironmentId, ct) : new Dictionary<string, List<string>>();
        var fields = new List<AltinnSettingField>();
        var notes = new List<string>();
        foreach (var field in D.Fields)
        {
            if (field.Secret)
            {
                var available = session.Secret(SecretKey(integration)) is not null;
                fields.Add(new AltinnSettingField
                {
                    Key = field.Key, Group = field.Group, Label = field.Label, Requirement = AltinnRequirement.Automated,
                    State = available ? AltinnSettingState.AvailableForSession : AltinnSettingState.Missing, Source = available ? AltinnSettingSource.Session : AltinnSettingSource.None,
                    SourceLabel = available ? "This backend session (memory only)" : "Not available", Detail = field.Help,
                });
                continue;
            }
            var candidates = new List<AltinnSettingCandidate>();
            if (integration.Settings.TryGetValue(field.Key, out var configured))
                candidates.Add(new(configured, AltinnSettingState.Configured, AltinnSettingSource.TargetEnvironment, integration.Provenance.GetValueOrDefault(field.Key) ?? "Configured manually"));
            if (detected.TryGetValue(field.Key, out var values))
            {
                if (values.Count == 1) candidates.Add(new(values[0], AltinnSettingState.Detected, AltinnSettingSource.IntegrationBinding, "Integration Quality Review"));
                else if (values.Count > 1) notes.Add($"{field.Label}: {values.Count} candidates in Integration Quality Review — choose one; nothing is selected automatically.");
            }
            fields.Add(AltinnEnvironmentService.Resolve(field.Key, field.Group, field.Label, field.Required ? AltinnRequirement.Manual : AltinnRequirement.Optional, candidates, field.Help));
        }

        var required = fields.Where(field => field.Requirement == AltinnRequirement.Manual).ToList();
        var configuration = required.All(field => field.State == AltinnSettingState.Configured) ? new SetupReadinessItem("configuration", "Configuration", SetupReadinessState.Ready, "Required settings configured.")
            : new SetupReadinessItem("configuration", "Configuration", fields.Any(field => field.State == AltinnSettingState.Configured) ? SetupReadinessState.Partial : SetupReadinessState.Blocked,
                $"Missing: {string.Join(", ", required.Where(field => field.State != AltinnSettingState.Configured).Select(field => field.Label))}");
        var secret = fields.FirstOrDefault(field => field.Key.EndsWith("Secret", StringComparison.Ordinal));
        var authentication = secret is null ? new SetupReadinessItem("authentication", "Authentication", SetupReadinessState.NotAssessed, "No secret is used by this provider.")
            : new SetupReadinessItem("authentication", "Authentication", secret.State == AltinnSettingState.AvailableForSession ? SetupReadinessState.Ready : SetupReadinessState.Partial,
                secret.State == AltinnSettingState.AvailableForSession ? "Secret available for this session." : "Secret not available — only automated execution needs it.");
        return new ExternalIntegrationView
        {
            Integration = integration, Fields = fields, Detected = detect, Notes = notes,
            Summary = new(configuration, new("dependencies", "Dependencies", SetupReadinessState.Ready, "No provider dependencies."), authentication,
                new("runtime", "Runtime", SetupReadinessState.NotAssessed, "Not verified."),
                new("execution", "Execution", SetupReadinessState.NotAssessed, "BirkNext has no executor for this provider; it is configuration and evidence only.")),
            Detection = new(fields.Count(f => f.State == AltinnSettingState.Configured), fields.Count(f => f.State == AltinnSettingState.Detected), 0,
                required.Count(f => f.State == AltinnSettingState.Missing), fields.Count(f => f.State == AltinnSettingState.Conflict),
                [.. required.Where(f => f.State is AltinnSettingState.Missing or AltinnSettingState.Conflict).Select(f => $"{f.State}: {f.Label}")]),
        };
    }

    public static string SecretKey(ExternalIntegration integration) => $"integration:{integration.Id}";

    /// <summary>Deterministic catalog values per field (endpoint, namespace, hub…) from the environment's IQR integrations of the matching kind.</summary>
    private async Task<Dictionary<string, List<string>>> CatalogValuesAsync(string environmentId, CancellationToken ct)
    {
        IReadOnlyList<IntegrationDefinition> integrations;
        try { integrations = (await catalog.GetAsync(environmentId, null, null, ct)).Integrations; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return []; }
        var kind = type switch
        {
            ExternalProviderType.AzureServiceBus => IntegrationKind.ServiceBus,
            ExternalProviderType.AzureEventHub => IntegrationKind.EventHub,
            ExternalProviderType.RestApi or ExternalProviderType.GenericHttp or ExternalProviderType.GraphQl => IntegrationKind.HttpApi,
            ExternalProviderType.Database => IntegrationKind.Database,
            ExternalProviderType.FileSftp => IntegrationKind.File,
            _ => (IntegrationKind?)null,
        };
        if (kind is null) return [];
        var matching = integrations.Where(item => item.Kind == kind).ToList();
        var key = type switch
        {
            ExternalProviderType.AzureServiceBus => "entity",
            ExternalProviderType.AzureEventHub => "hub",
            ExternalProviderType.GraphQl => "endpoint",
            ExternalProviderType.Database => "database",
            ExternalProviderType.FileSftp => "path",
            _ => "baseUrl",
        };
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            [key] = [.. matching.Select(item => item.EndpointOrTopic?.Trim()).OfType<string>().Where(value => value.Length > 0).Distinct(StringComparer.Ordinal)],
        };
        if (type is ExternalProviderType.RestApi or ExternalProviderType.GenericHttp)
            result["healthEndpoint"] = [.. matching.Select(item => item.HealthUrl?.Trim()).OfType<string>().Where(value => value.Length > 0).Distinct(StringComparer.Ordinal)];
        if (type == ExternalProviderType.AzureEventHub)
            result["consumerGroup"] = [.. matching.Select(item => item.ConsumerGroup?.Trim()).OfType<string>().Where(value => value.Length > 0).Distinct(StringComparer.Ordinal)];
        return result;
    }
}

/// <summary>
/// Altinn as a provider: its typed settings stay the Target Environment Altinn record (Detect, precedence, browser-vs-unattended separation
/// and manual-vs-automated readiness come from <see cref="AltinnEnvironmentService"/>). At most one Altinn integration per environment.
/// </summary>
public sealed class AltinnProviderHandler(AltinnEnvironmentService altinn, AltinnEnvironmentSettingsStore store, IIntegrationMessageFlowStore messageFlows,
    AltinnAppContractService contracts, AltinnTestingPolicy policy) : IExternalProviderHandler
{
    public ExternalProviderType Type => ExternalProviderType.Altinn;

    public async Task<Dictionary<string, string>> CurrentSettingsAsync(ExternalIntegration integration, CancellationToken ct) =>
        ToSettings(await store.GetAsync(integration.TargetEnvironmentId, ct));

    public static Dictionary<string, string> ToSettings(AltinnEnvironmentSettings saved)
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string key, string? value) { if (!string.IsNullOrWhiteSpace(value)) settings[key] = value.Trim(); }
        Add("environment", saved.Environment?.ToString());
        Add("appId", saved.AppIdentifier);
        Add("baseUrl", saved.AppBaseUrl);
        Add("formDataType", saved.FormDataType);
        Add("contentType", saved.ContentType);
        Add("clientId", saved.Unattended.ClientId);
        Add("redirectUri", saved.Unattended.RedirectUri);
        Add("scopes", saved.Unattended.Scopes.Count == 0 ? null : string.Join(" ", saved.Unattended.Scopes));
        Add("unattendedEnabled", saved.Unattended.Enabled?.ToString().ToLowerInvariant());
        Add("integrationId", saved.IntegrationId);
        return settings;
    }

    public async Task<IReadOnlyList<string>> ApplyAsync(ExternalIntegration integration, IReadOnlyDictionary<string, string> settings, CancellationToken ct)
    {
        string? V(string key) => settings.TryGetValue(key, out var value) ? value : null;
        var (_, problems) = await store.UpdateAsync(integration.TargetEnvironmentId, new AltinnEnvironmentSettingsUpdate
        {
            Environment = V("environment"), AppIdentifier = V("appId"), AppBaseUrl = V("baseUrl"), FormDataType = V("formDataType"), ContentType = V("contentType"),
            ClientId = V("clientId"), RedirectUri = V("redirectUri"), IntegrationId = V("integrationId"),
            Scopes = V("scopes") is { } scopes ? [.. scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)] : null,
            Enabled = V("unattendedEnabled") is { } enabled && bool.TryParse(enabled, out var on) ? on : null,
        }, ct);
        return problems;
    }

    public async Task<ExternalIntegrationView> AssessAsync(ExternalIntegration integration, bool detect, CancellationToken ct)
    {
        var view = await altinn.ViewAsync(integration.TargetEnvironmentId, detect, ct);
        var settings = ToSettings(view.Saved);
        SetupReadinessItem Map(AltinnReadinessItem? item, string key, string label) => item is null ? new(key, label, SetupReadinessState.NotAssessed, "")
            : new(key, label, item.Key == "runtime" && item.State == AltinnReadinessState.Partial ? SetupReadinessState.Observed : item.State switch
            {
                AltinnReadinessState.Ready => SetupReadinessState.Ready,
                AltinnReadinessState.Partial => SetupReadinessState.Partial,
                AltinnReadinessState.Blocked => SetupReadinessState.Blocked,
                _ => SetupReadinessState.NotAssessed,
            }, item.Reason);
        SetupReadinessItem? S(string key, string label) => view.Summary.FirstOrDefault(item => item.Key == key) is { } item ? Map(item, key, label) : null;
        return new ExternalIntegrationView
        {
            Integration = integration with { Settings = settings, EnvironmentName = view.Fields.FirstOrDefault(field => field.Key == "environment")?.Value ?? integration.EnvironmentName },
            Fields = view.Fields, Detected = view.Detected, Notes = view.Notes, Detection = view.Detection,
            Summary = new(S("app", "Configuration") ?? ExternalIntegrationView.Empty("configuration"),
                Map(view.ManualReadiness, "dependencies", "Dependencies") with { Reason = $"Manual testing: {view.ManualReadiness.Reason}" },
                S("authentication", "Authentication") ?? ExternalIntegrationView.Empty("authentication"),
                S("runtime", "Runtime") ?? ExternalIntegrationView.Empty("runtime"), Map(view.AutomatedReadiness, "execution", "Execution")),
        };
    }

    public Task<IReadOnlyList<SetupDependency>> SetupRequirementsAsync(ExternalIntegration integration, IReadOnlyDictionary<string, string> settings, CancellationToken ct)
    {
        var list = new List<SetupDependency>();
        if (settings.TryGetValue("integrationId", out var integrationId)) list.Add(new(SetupDependencyKind.Integration, integrationId, true, Provider: "Altinn"));
        if (settings.TryGetValue("formDataType", out var form)) list.Add(new(SetupDependencyKind.Contract, form, true, Provider: "Altinn"));
        // Reporter and child are roles resolved against Test Data at the destination; the alias travels when one is known, never the identifier.
        var reporter = policy.Options.UnattendedIdPortenTest.Reporters.FirstOrDefault(item => AltinnTestingPolicy.ReporterConfigurationProblem(item) is null)?.Alias;
        var child = policy.Options.SyntheticChildren.FirstOrDefault()?.Alias;
        list.Add(new(SetupDependencyKind.TestData, reporter ?? "Reporter", true, "Reporter"));
        list.Add(new(SetupDependencyKind.TestData, child ?? "Child", true, "Child"));
        return Task.FromResult<IReadOnlyList<SetupDependency>>(list);
    }

    public bool Satisfies(string capability, IReadOnlyDictionary<string, string> settings) =>
        capability is "AltinnAppSubmission" or "AltinnReportSubmission" && settings.ContainsKey("appId") && (capability == "AltinnAppSubmission" || settings.ContainsKey("formDataType"));

    /// <summary>An Altinn form contract resolves against the stored contract evidence (Source Analysis / public metadata); never the newest guess.</summary>
    public async Task<SetupDependencyResolution?> ResolveAsync(string environmentId, SetupDependency dependency, CancellationToken ct)
    {
        if (dependency.Kind != SetupDependencyKind.Contract || dependency.Provider != "Altinn") return null;
        var record = (await messageFlows.GetAsync(environmentId, ct)).AppContract;
        var known = new[] { record?.SourceAnalysis, record?.PublicMetadata, record?.Confirmed?.Snapshot }.OfType<AltinnContractSnapshot>().ToList();
        var ids = known.Select(snapshot => snapshot.FormDataTypeId).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var candidates = known.SelectMany(snapshot => snapshot.FormDataTypeCandidates).Distinct(StringComparer.Ordinal).ToList();
        const string owner = "Integration Quality Review (Altinn contract)", href = "/integration-quality-review?view=evidence";
        if (ids.Contains(dependency.Reference, StringComparer.Ordinal))
            return new(dependency, SetupDependencyState.Resolved, "Resolved from stored contract evidence (Source Analysis / public metadata). Not runtime verified.", owner, href);
        if (candidates.Count(item => item == dependency.Reference) > 0 && ids.Count == 0)
            return new(dependency, SetupDependencyState.Ambiguous, "The app declares several form data types; none is selected automatically.", owner, href);
        return ids.Count > 0
            ? new(dependency, SetupDependencyState.Incompatible, $"The stored contract declares {string.Join(", ", ids)}, not {dependency.Reference}.", owner, href)
            : new(dependency, dependency.Required ? SetupDependencyState.Missing : SetupDependencyState.OptionalMissing, "No Altinn contract evidence in this environment (read public metadata or bind the app source).", owner, href);
    }

    public async Task<IReadOnlyList<ExternalIntegrationProposal>> ProposeAsync(string environmentId, IReadOnlyList<ExternalIntegration> existing, CancellationToken ct)
    {
        if (existing.Any(item => item.ProviderType == ExternalProviderType.Altinn)) return [];
        var view = await altinn.ViewAsync(environmentId, detect: false, ct);
        var app = view.Fields.FirstOrDefault(field => field.Key == "app");
        if (app?.Value is null) return [];
        var sources = string.Join(" + ", view.Fields.Where(field => field.Group == AltinnEnvironmentService.ApplicationGroup && field.Value is not null)
            .Select(field => field.SourceLabel).Distinct(StringComparer.Ordinal));
        return
        [
            new ExternalIntegrationProposal($"Altinn – {app.Value}", ExternalProviderType.Altinn, view.Fields.FirstOrDefault(field => field.Key == "environment")?.Value,
                sources, app.State == AltinnSettingState.Conflict ? "Sources disagree on the app; review before accepting." : "Ready to configure: review the detected values, then accept.",
                ToSettings(view.Saved), app.State == AltinnSettingState.Conflict),
        ];
    }
}

/// <summary>
/// What test packs require of an environment's setup, by capability and role — never the configuration itself. A package that names a test
/// pack carries these into preflight.
/// </summary>
public static class TestPackSetupRequirements
{
    public static IReadOnlyList<SetupDependency> For(string testPack) => testPack.ToLowerInvariant() switch
    {
        "skolenaervaer" =>
        [
            new(SetupDependencyKind.ProviderCapability, "AltinnReportSubmission", true, Provider: "Altinn"),
            new(SetupDependencyKind.TestData, "Reporter", true, "Reporter"),
            new(SetupDependencyKind.TestData, "Child", true, "Child"),
            new(SetupDependencyKind.TestData, "duplicate-report-payload", false, "DuplicatePayload"),
            new(SetupDependencyKind.TestData, "invalid-report-payload", false, "InvalidPayload"),
        ],
        _ => [],
    };
}
