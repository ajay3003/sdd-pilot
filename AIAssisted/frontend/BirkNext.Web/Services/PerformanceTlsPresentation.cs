using BirkNext.PerformanceTests;

namespace BirkNext.Web.Services;

/// <summary>
/// Container TLS trust wording for testers and copyable text for server owners. Built from public certificate metadata only (subjects, issuers,
/// validity, SHA-256 prefixes, timestamps) — never certificate content, keys, tokens or URL queries.
/// </summary>
public static class PerformanceTlsPresentation
{
    public static string Host(string origin) => Uri.TryCreate(origin, UriKind.Absolute, out var uri) ? uri.Host : origin;

    /// <summary>"Runtime execution blocked" when the container TLS stage failed; null when it did not.</summary>
    public static string? BlockedHeadline(ContainerTlsValidation? validation) =>
        validation is { Passed: false } v && PerformanceNetworkStages.FailedStage(v.ProbeState) == "tls"
            ? v.Chain?.FailureClass == TlsFailureClasses.IncompleteServerChain ? "Execution blocked — target certificate chain is incomplete" : "Runtime execution blocked — TLS trust failed"
            : null;

    /// <summary>Safe certificate details for infrastructure/server owners.</summary>
    public static string CertificateDetails(ContainerTlsValidation validation)
    {
        var chain = validation.Chain;
        var lines = new List<string>
        {
            $"Host: {Host(validation.TargetOrigin)}",
            $"Failure type: {(chain is null ? PerformanceNetworkStages.Classification(validation.ProbeState) : TlsFailureClasses.Label(chain.FailureClass))}",
        };
        if (chain?.Certificates is { Count: > 0 } certs)
        {
            lines.Add($"Leaf subject: {certs[0].Subject}");
            lines.Add($"Leaf issuer: {certs[0].Issuer}");
            lines.Add($"Leaf validity: {certs[0].NotBefore:yyyy-MM-dd} – {certs[0].NotAfter:yyyy-MM-dd}");
            lines.Add($"Leaf SHA-256 (first 16): {certs[0].Sha256}");
            lines.Add($"Certificates sent by the server: {chain.SentByServer} of {certs.Count}");
            if (certs.LastOrDefault(c => c.SelfSigned) is { } root) lines.Add($"Root: {root.Subject}");
        }
        if (chain?.MissingIssuer is { } missing) lines.Add($"Missing intermediate: {missing}");
        if (chain is not null) { lines.Add($"Windows: {chain.HostDetail}"); lines.Add($"k6 container: {PerformanceNetworkStages.Classification(validation.ProbeState)}"); }
        lines.Add($"Validated: {validation.CheckedAt.ToUniversalTime():yyyy-MM-dd HH:mm} UTC");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>A ready-to-send request to the server owner for an incomplete chain (null for other failures).</summary>
    public static string? RemediationSummary(ContainerTlsValidation validation)
    {
        if (validation.Chain is not { FailureClass: TlsFailureClasses.IncompleteServerChain } chain) return null;
        var host = Host(validation.TargetOrigin);
        var missing = chain.MissingIssuer ?? "the issuing intermediate certificate";
        return string.Join(Environment.NewLine,
            $"Host: {host}",
            "Result: TLS validation failed from the k6/Linux container.",
            "Cause: the server certificate chain is incomplete.",
            $"Missing: {missing}",
            $"Windows: {(chain.HostTrusted ? "trust succeeds (Windows retrieves the missing intermediate itself)." : chain.HostDetail)}",
            "Container: trust fails because the server does not provide the complete chain.",
            $"Requested action: configure the TLS termination point for {host} (reverse proxy, ingress, application gateway, load balancer, web server or API gateway) to send the leaf certificate plus the required intermediate certificate(s), then notify QA for revalidation.");
    }

    public static string RepairStateLabel(string state) => state switch
    {
        TlsRepairStates.Applied => "Automatic repair applied",
        TlsRepairStates.Rejected => "Automatic repair rejected",
        TlsRepairStates.Stopped => "Automatic repair stopped",
        TlsRepairStates.Disabled => "Automatic repair off (Manual only)",
        TlsRepairStates.NoLongerNeeded => "No longer needed — server now provides the complete chain",
        _ => "Not eligible for automatic repair",
    };
}
