using BirkNext.Api.Services.Integrations;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.AltinnTesting;

/// <summary>
/// What the Skolenærvær/Altinn setup is made of and where each part comes from, so configured values, values discovered from the deployed
/// app, confirmed policy evidence and per-run input are never confused. Current values are shown as current evidence with their source, not
/// as permanent facts. Secrets are reported as available or missing, reporter identifiers as configured or missing — never by value.
/// </summary>
public sealed class AltinnConfigurationOverviewService(IIntegrationMessageFlowStore messageFlows, AltinnTestingPolicy policy, AltinnAppContractService contracts)
{
    public const string TargetConfiguration = "Target configuration";
    public const string DeployedMetadata = "Deployed metadata";
    public const string LocalConfiguration = "Local backend configuration";
    public const string SecretSource = "Secret · local environment";
    public const string PolicyEvidence = "Confirmed policy evidence";
    public const string RunInput = "Run input";

    public async Task<AltinnConfigurationOverview> GetAsync(string environmentId, CancellationToken ct)
    {
        var package = await messageFlows.GetAsync(environmentId, ct);
        return Build(environmentId, package.AltinnConfiguration, contracts.Evaluate(environmentId, package.AltinnConfiguration.AppIdentifier, package.AppContract), package.AppContract);
    }

    public AltinnConfigurationOverview Build(string environmentId, AltinnTestConfiguration config, AltinnAppContractView contract, AltinnAppContractRecord? record)
    {
        var items = new List<AltinnConfigurationItem>();
        var approved = policy.ApprovedApp(config.AppIdentifier, out var appReason);
        items.Add(new("app", "Altinn app", contract.ConfiguredAppId, approved is null ? string.IsNullOrWhiteSpace(config.AppIdentifier) ? "Missing" : "Not approved" : "Approved",
            TargetConfiguration, approved is null ? appReason : "Integration Quality Review app identifier, approved by the backend (AltinnTesting:ApprovedApps). A different app needs an explicit configuration change."));
        items.Add(new("environment", "Altinn environment", config.Environment.ToString(), AltinnTestingPolicy.AltinnEnvironmentBlock(config) is null ? "TT02" : "Blocked",
            TargetConfiguration, AltinnTestingPolicy.AltinnEnvironmentBlock(config) ?? "Test (TT02); Production is refused by name on every request."));
        string? host = null, hostProblem = null;
        try { host = approved is { } app ? policy.Endpoints.AppBase(app.Org, app.App).Host : null; }
        catch (AltinnContractException ex) { hostProblem = ex.Message; }
        items.Add(new("host", "TT02 app host", host, hostProblem is not null ? "Blocked" : approved is null ? "Missing" : "Derived",
            "Derived from the approved app id and the configured TT02 apps domain (AltinnTesting:Endpoints:Test)",
            hostProblem ?? "Built only from the approved org/app; deployed metadata is read from it and never redirects BirkNext to another host. Configured, not verified."));

        var live = record?.PublicMetadata is { } published && published.AppId == contract.ConfiguredAppId ? published : null;
        var deployed = live?.DeployedProcess;
        var tasks = contract.Process.PlannedTasks.Count > 0 ? contract.Process.PlannedTasks : deployed?.Tasks ?? [];
        items.Add(new("process-task", "Process task", tasks.Count == 0 ? null : string.Join(" → ", tasks.Select(AltinnProcessContractEvaluator.Describe)),
            contract.Process.Status switch
            {
                AltinnProcessContractStatus.ConfirmedForCurrentOperation => "Confirmed for current operation",
                AltinnProcessContractStatus.Partial => "Partial",
                AltinnProcessContractStatus.Conflict => "Conflict",
                _ => "Not confirmed",
            },
            contract.Process.Sources.Count == 0 ? "No process evidence" : string.Join(" + ", contract.Process.Sources),
            contract.Process.Confirmed ? "Discovered from the deployed contract; the live instance must be at this task before process/next." : string.Join(" ", contract.Process.Reasons.DefaultIfEmpty(contract.Process.Summary))));
        var dataTypeSource = contract.Parts.FirstOrDefault(part => part.Part == AltinnContractPartKind.FormDataType)?.Source;
        items.Add(new("datatype", "Form data type", contract.FormDataTypeId, contract.FormDataTypeId is null ? "Not confirmed" : "Discovered",
            dataTypeSource switch { AltinnContractSourceKind.SourceAnalysisSnapshot => "Source Analysis (app source)", AltinnContractSourceKind.PublicTt02Metadata => DeployedMetadata, _ => "No source" },
            contract.FormDataTypeId is null
                ? contract.Parts.FirstOrDefault(part => part.Part == AltinnContractPartKind.FormDataType)?.Detail ?? "No single form data type."
                : $"Task {(live?.FormDataTaskId ?? record?.SourceAnalysis?.FormDataTaskId) ?? "not stated"} · {contract.SubmissionContentType ?? "no usable content type"}"
                  + (live?.FormDataMaxCount is { } max ? $" · at most {max} per instance" : "")));
        items.Add(new("operations", "App API operations", contract.Operations.Count == 0 ? null : $"{contract.Operations.Count} operations", contract.Operations.Count == AltinnAppOperationKeys.Required.Length ? "Resolved" : "Incomplete",
            string.IsNullOrEmpty(contract.OperationsSource) ? "No app" : contract.OperationsSource,
            string.Join(" · ", contract.Operations.Select(operation => $"{operation.Method} {operation.PathTemplate}"))));
        items.Add(new("contract", "Deployed contract", live is null ? null : $"{Short(live.Fingerprint)} · app-lib {deployed?.AppLibVersion ?? "unknown"}",
            contract.Freshness switch
            {
                AltinnContractFreshness.Current => "Current",
                AltinnContractFreshness.RefreshRecommended => "Refresh recommended",
                AltinnContractFreshness.Changed => "Changed",
                _ => "Not read",
            }, DeployedMetadata, contract.FreshnessDetail));

        var confirmation = policy.AppPolicy(config.AppIdentifier, record?.ObservedPolicy, out var policyDetail);
        items.Add(new("policy", "App authorization policy", record?.ObservedPolicy?.Sha256 is { } observed ? $"sha256 {Short(observed)}" : null,
            confirmation is not null ? "Confirmed" : policyDetail.Contains("changed since it was confirmed", StringComparison.Ordinal) ? "Changed · needs confirmation" : "Not confirmed",
            PolicyEvidence, policyDetail));

        var clientId = policy.ClientId(config, out var clientSource, out var clientProblem);
        items.Add(new("client", "ID-porten TEST client", null, clientProblem is not null ? "Conflict" : clientId is null ? "Missing" : "Configured", clientSource,
            clientProblem ?? (clientId is null ? "No client id (Integration Quality Review → Altinn test configuration, or backend AltinnTesting:UnattendedIdPortenTest:ClientId)." : "Configured; replaceable without a code change.")));
        items.Add(new("redirect", "Redirect URI", null, policy.UnattendedRedirect() is null ? "Missing" : "Configured", LocalConfiguration,
            policy.UnattendedRedirect() is null ? "AltinnTesting:UnattendedIdPortenTest:RedirectUri: https, or http on loopback." : "Must equal the URI registered for the client (value not shown)."));
        items.Add(new("secret", "Client secret", null, policy.UnattendedSecretAvailable ? "Available" : "Missing", SecretSource,
            policy.UnattendedSecretAvailable ? "Available to the backend (value never shown)." : "Not available to the backend (environment variable)."));
        var scopes = config.ConfiguredScopes.Where(scope => !string.IsNullOrWhiteSpace(scope)).Select(scope => scope.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        var openid = scopes.Contains("openid", StringComparer.Ordinal);
        items.Add(new("scopes", "Scopes", scopes.Length == 0 ? null : string.Join(", ", scopes), scopes.Length == 0 ? "Missing" : openid ? $"{scopes.Length} configured" : "Missing openid",
            "Integration Quality Review → Altinn test configuration",
            (openid ? "" : "The current authentication requires openid. ") + "Not checked against the client registration: BirkNext cannot see it."));
        var childDeclared = config.ApprovedSyntheticData && !string.IsNullOrWhiteSpace(config.SyntheticIdentitySetReference);
        items.Add(new("child", "Synthetic child", null, childDeclared ? "Declared" : "Missing", $"{RunInput} + approved identity set (IQR)",
            childDeclared ? "Entered per run; must differ from the reporter. Declared ≠ verified: whether Person knows the child is not checked." : "No approved synthetic identity set is declared."));
        if (contract.ImportedOpenApi is { } imported)
            items.Add(new("imported", "Imported OpenAPI", $"{Short(imported.Fingerprint)} · app-lib {imported.DeployedProcess?.AppLibVersion ?? "unknown"}", "Not live-verified", "Imported OpenAPI",
                contract.ImportedComparedToLive is { } compared
                    ? compared.Impact == AltinnContractChangeImpact.NoChange ? "Matches the live deployed contract. Offline evidence only; never used to execute." : $"Differs from the live deployed contract ({compared.Changes.Count} difference(s)). Offline evidence only; never used to execute."
                    : "No live contract to compare with. Offline evidence only; never used to execute."));

        var reporters = policy.Options.UnattendedIdPortenTest.Reporters.Select(reporter => new AltinnReporterConfigurationView(
            reporter.Alias, string.IsNullOrWhiteSpace(reporter.Label) ? reporter.Alias : reporter.Label, !string.IsNullOrWhiteSpace(reporter.TestIdPersonIdentifier),
            reporter.AltinnProfileConfirmed, reporter.AltinnRole, AltinnTestingPolicy.ReporterConfigurationProblem(reporter))).ToArray();
        return new AltinnConfigurationOverview { EnvironmentId = environmentId, Items = items, Reporters = reporters };
    }

    private static string Short(string value) => value.Length > 12 ? $"{value[..12]}…" : value;
}
