using BirkNext.Api.Services.LocalHttpsProxy;
using BirkNext.Integrations;
using BirkNext.LocalHttpsProxy;

namespace BirkNext.Api.Services.ActiveEventTesting.Observation;

/// <summary>
/// Which authenticated identity a downstream read uses. Domain verifiers never own credentials: the identity resolves to a credential held
/// by the shared authenticated gateway (today: the browser session observed by the Local HTTPS Proxy). A backend machine identity would be
/// another implementation of this seam; none exists yet, so verifiers that need one stay Unavailable.
/// </summary>
public interface IDownstreamVerificationIdentitySource
{
    AuthenticatedReviewIdentity? Resolve(ActiveEventTrustedTarget target, out string reason);
}

/// <summary>A domain's read-only check: the URL to read (built from the event's synthetic/derived identity) and the identity it expects.</summary>
public sealed record DownstreamReadContract(string Url, string ExpectedIdentity);

/// <summary>
/// Shared base for read-only downstream verifiers. It executes through <see cref="IAuthenticatedReviewGateway"/> (GET only, approved hosts,
/// sanitized results, no bodies kept, no token ever seen by the verifier) and maps the HTTP outcome to the neutral result:
/// 2xx Verified, 404 until the window ends NotVerified, 401/403 or no authenticated context Unavailable, 300/409 Ambiguous, anything else
/// UnexpectedResult. A domain extension supplies only <see cref="Applies"/> and <see cref="Contract"/> — never an HTTP client.
/// </summary>
public abstract class GatewayReadDownstreamVerifier(IAuthenticatedReviewGateway gateway, IDownstreamVerificationIdentitySource identities, TimeProvider clock,
    TimeSpan pollInterval) : IActiveEventDownstreamVerifier
{
    protected abstract string Source { get; }
    protected abstract bool Applies(ActiveEventScenarioDescriptor scenario);
    protected abstract DownstreamReadContract? Contract(ActiveEventTrustedTarget target, ActiveEventScenarioDescriptor scenario, ActiveEventCorrelation correlation, out string reason);

    public bool CanVerify(ActiveEventTrustedTarget target, ActiveEventScenarioDescriptor scenario, out string reason)
    {
        if (!Applies(scenario)) { reason = "Not a scenario of this verifier."; return false; }
        if (identities.Resolve(target, out reason) is null) return false;
        reason = "";
        return true;
    }

    public async Task<ActiveEventDownstreamResult> VerifyAsync(ActiveEventTrustedTarget target, ActiveEventScenarioDescriptor scenario,
        ActiveEventCorrelation correlation, TimeSpan window, CancellationToken ct)
    {
        if (identities.Resolve(target, out var identityReason) is not { } identity)
            return Result(ActiveEventDownstreamOutcome.Unavailable, null, null, identityReason);
        if (Contract(target, scenario, correlation, out var contractReason) is not { } contract)
            return Result(ActiveEventDownstreamOutcome.Unavailable, null, null, contractReason);

        var deadline = clock.GetUtcNow() + window;
        while (true)
        {
            var outcome = await gateway.ExecuteRestAsync(identity, "GET", contract.Url, ct);
            if (!outcome.Executed)
                return Result(ActiveEventDownstreamOutcome.Unavailable, contract.ExpectedIdentity, null, $"The authenticated read could not run: {outcome.Message}");
            var status = outcome.Result!.StatusCode;
            switch (status)
            {
                case >= 200 and < 300:
                    return Result(ActiveEventDownstreamOutcome.Verified, contract.ExpectedIdentity, contract.ExpectedIdentity, $"The expected record {contract.ExpectedIdentity} was read (HTTP {status}).");
                case 401 or 403:
                    return Result(ActiveEventDownstreamOutcome.Unavailable, contract.ExpectedIdentity, null, $"The read was not authorized (HTTP {status}).");
                case 300 or 409:
                    return Result(ActiveEventDownstreamOutcome.Ambiguous, contract.ExpectedIdentity, null, $"The read did not identify one record (HTTP {status}).");
                case 404:
                    if (ct.IsCancellationRequested || clock.GetUtcNow() >= deadline)
                        return Result(ActiveEventDownstreamOutcome.NotVerified, contract.ExpectedIdentity, null, $"The expected record {contract.ExpectedIdentity} was not found within {window.TotalSeconds:0} s.");
                    break;
                default:
                    return Result(ActiveEventDownstreamOutcome.UnexpectedResult, contract.ExpectedIdentity, null, $"Unexpected read result (HTTP {status}).");
            }
            try { await Task.Delay(pollInterval, clock, ct); }
            catch (OperationCanceledException) { /* the next read records the cancellation */ }
        }
    }

    private ActiveEventDownstreamResult Result(ActiveEventDownstreamOutcome outcome, string? expected, string? observed, string reason) => new()
    {
        Outcome = outcome, ExpectedIdentity = expected, ObservedIdentity = observed, Reason = reason, EvidenceSummary = Source,
        Limitations = ["Read-only status check through the shared authenticated gateway; record content is not inspected."],
    };
}
