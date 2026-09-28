using System.Net.Http.Json;
using System.Text.Json;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.SecurityClassification;

public interface IClassificationLiveProbe
{
    Task<ClassificationLiveEvidence> ProbeAsync(ClassificationTestContext context, ClassificationRunRequest request, IReadOnlyList<ClassificationLevel> levels, CancellationToken ct = default);
}

/// <summary>
/// Safe live security checks against the Person module GraphQL endpoint of an APPROVED DEV/QA test context. Only three fixed query
/// documents are ever sent — profile by id, search by the exact BiRK id, revision log by id — and only for the configured synthetic test
/// children (plus one random, nonexistent id for the anti-disclosure comparison). No mutation, no list or "find all" query, no search for
/// real classified children. The queries ask for ids, levels and counts only (never names or national ids); responses are reduced to
/// derived facts and discarded. Tokens come with the run request, are sent as Authorization headers and are never stored or logged.
/// </summary>
public sealed class GraphQlClassificationProbe(HttpClient http, ILogger<GraphQlClassificationProbe> logger) : IClassificationLiveProbe
{
    public const string Adapter = "Security classification safe checks";
    public const string ProfileQuery = "query SafeProfile($id: UUID!) { hentBarn(barnRegistreringId: $id) { barnRegistreringId sikkerhetsnivaaKode } }";
    public const string SearchQuery = "query SafeSearch($birkId: String) { soekBarn(kriterier: { birkId: $birkId }, side: 1, sideStoerrelse: 5) { totaltAntall resultater { barnRegistreringId } } }";
    public const string AuditQuery = "query SafeAudit($id: UUID!) { hentRevisjonslogg(barnRegistreringId: $id, side: 1, sideStoerrelse: 1) { totaltAntall } }";
    public static readonly string[] AllowedDocuments = [ProfileQuery, SearchQuery, AuditQuery];
    private const int MaxBodyBytes = 256 * 1024;

    /// <summary>Why the live checks cannot run, or null. Production and unknown environment types are refused.</summary>
    public static (IntegrationEvidenceState State, string Reason)? Gate(ClassificationTestContext context, string? environmentType)
    {
        if (string.Equals(environmentType, "Production", StringComparison.OrdinalIgnoreCase))
            return (IntegrationEvidenceState.NotSupported, "Production is never tested.");
        var env = context.Environment?.Trim().ToUpperInvariant();
        if (env is not ("DEV" or "QA")) return (IntegrationEvidenceState.NotConfigured, "No approved DEV/QA security test context is configured.");
        var allowed = env == "DEV" ? new[] { "Local", "Development" } : ["QA", "Test"];
        if (environmentType is null || !allowed.Contains(environmentType, StringComparer.OrdinalIgnoreCase))
            return (IntegrationEvidenceState.NotSupported, $"The test context is for {env}, but the active Target Environment type is {(string.IsNullOrWhiteSpace(environmentType) ? "unknown" : environmentType)}.");
        if (!context.ApprovedByTestLead) return (IntegrationEvidenceState.NotConfigured, "The security test context is not approved by the test lead.");
        if (string.IsNullOrWhiteSpace(context.GraphQlEndpoint)) return (IntegrationEvidenceState.NotConfigured, "No GraphQL endpoint is configured in the test context.");
        if (context.Validate() is { } invalid) return (IntegrationEvidenceState.NotConfigured, invalid);
        if (context.TestChildren.Count == 0) return (IntegrationEvidenceState.NotConfigured, "No synthetic test child is configured.");
        return null;
    }

    public async Task<ClassificationLiveEvidence> ProbeAsync(ClassificationTestContext context, ClassificationRunRequest request, IReadOnlyList<ClassificationLevel> levels, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (Gate(context, request.EnvironmentType) is { } gate) return new ClassificationLiveEvidence { State = gate.State, Reason = gate.Reason, CapturedAt = now };
        var identities = new List<(ClassificationIdentity Identity, string Token)>();
        if (!string.IsNullOrWhiteSpace(request.UnauthorizedToken)) identities.Add((ClassificationIdentity.Unauthorized, request.UnauthorizedToken.Trim()));
        if (!string.IsNullOrWhiteSpace(request.AuthorizedToken)) identities.Add((ClassificationIdentity.Authorized, request.AuthorizedToken.Trim()));
        if (identities.Count == 0) return new ClassificationLiveEvidence { State = IntegrationEvidenceState.NotConfigured, Reason = "No test identity token was supplied for this run (tokens are never stored).", CapturedAt = now };

        var endpoint = new Uri(context.GraphQlEndpoint!.Trim());
        var observations = new List<ClassificationObservation>();
        var nonexistent = Guid.NewGuid();
        foreach (var (identity, token) in identities)
        {
            var missing = await QueryAsync(endpoint, token, ProfileQuery, new { id = nonexistent }, ct);
            foreach (var child in context.TestChildren.OrderBy(c => c.Nivaa))
            {
                var graded = levels.FirstOrDefault(l => l.Nivaa == child.Nivaa)?.KreverGradertTilgang ?? child.Nivaa >= 2;
                var shouldSee = !graded || identity == ClassificationIdentity.Authorized;
                if (child.BarnRegistreringId is { } id)
                {
                    var profile = await QueryAsync(endpoint, token, ProfileQuery, new { id }, ct);
                    observations.Add(ProfileObservation(child.Nivaa, identity, graded, shouldSee, profile, id));
                    if (graded && identity == ClassificationIdentity.Unauthorized)
                        observations.Add(Compare(child.Nivaa, profile, missing));
                    var audit = await QueryAsync(endpoint, token, AuditQuery, new { id }, ct);
                    observations.Add(AuditObservation(child.Nivaa, identity, graded, shouldSee, audit));
                }
                if (child.BirkId is { Length: > 0 } birkId)
                {
                    var search = await QueryAsync(endpoint, token, SearchQuery, new { birkId }, ct);
                    observations.AddRange(SearchObservations(child.Nivaa, identity, graded, shouldSee, search, child.BarnRegistreringId));
                }
            }
        }
        var answered = observations.Count(o => o.State != ClassificationState.NotAvailable);
        logger.LogInformation("Security classification safe checks against {Host}: {Answered} of {Total} observation(s) answered for {Identities} identity label(s) and {Children} test child(ren).",
            endpoint.Host, answered, observations.Count, identities.Count, context.TestChildren.Count);
        return new ClassificationLiveEvidence
        {
            State = answered == 0 ? IntegrationEvidenceState.Unavailable : IntegrationEvidenceState.Available,
            Reason = answered == 0 ? "The endpoint did not answer any safe query." : $"{answered} safe GraphQL query observation(s).",
            Target = $"{endpoint.Scheme}://{endpoint.Authority}", CapturedAt = now, Observations = observations,
        };
    }

    /// <summary>What a GraphQL response showed: whether the field had data, its derived values and the error codes. Never the body.</summary>
    internal sealed record Shape(bool Answered, int? Status, bool HasData, List<Guid> Ids, int? Level, int? Total, List<string> Errors, string? Failure)
    {
        public string Describe() => !Answered ? $"no response ({Failure})" : HasData ? "data returned" : Errors.Count > 0 ? $"no data, error {string.Join("/", Errors)}" : "null (no data, no error)";
        public string Signature => $"{Answered}|{Status}|{HasData}|{string.Join(",", Errors)}";
    }

    private async Task<Shape> QueryAsync(Uri endpoint, string token, string document, object variables, CancellationToken ct)
    {
        if (!AllowedDocuments.Contains(document)) throw new InvalidOperationException("Only the fixed safe query documents may be sent.");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(new { query = document, variables }) };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[MaxBodyBytes];
            var read = 0;
            int n;
            while (read < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(read), ct)) > 0) read += n;
            return Reduce((int)response.StatusCode, System.Text.Encoding.UTF8.GetString(buffer, 0, read));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new Shape(false, null, false, [], null, null, [], ex is TaskCanceledException ? "timed out" : "connection failed");
        }
    }

    internal static Shape Reduce(int status, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var errors = root.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Array
                ? e.EnumerateArray().Select(x => x.TryGetProperty("extensions", out var ext) && ext.TryGetProperty("code", out var code) ? code.GetString() ?? "ERROR" : "ERROR").Distinct().ToList()
                : [];
            var field = root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object ? data.EnumerateObject().Select(p => p.Value).FirstOrDefault() : default;
            var has = field.ValueKind == JsonValueKind.Object;
            var ids = new List<Guid>();
            int? level = null, total = null;
            if (has)
            {
                if (field.TryGetProperty("barnRegistreringId", out var id) && id.TryGetGuid(out var g)) ids.Add(g);
                if (field.TryGetProperty("sikkerhetsnivaaKode", out var lv) && lv.ValueKind == JsonValueKind.Number) level = lv.GetInt32();
                if (field.TryGetProperty("totaltAntall", out var t) && t.ValueKind == JsonValueKind.Number) total = t.GetInt32();
                if (field.TryGetProperty("resultater", out var r) && r.ValueKind == JsonValueKind.Array)
                    ids.AddRange(r.EnumerateArray().Select(x => x.TryGetProperty("barnRegistreringId", out var rid) && rid.TryGetGuid(out var rg) ? rg : Guid.Empty).Where(x => x != Guid.Empty));
            }
            return new Shape(true, status, has, ids, level, total, errors, null);
        }
        catch (JsonException) { return new Shape(true, status, false, [], null, null, ["NON_JSON"], null); }
    }

    private static ClassificationObservation ProfileObservation(int level, ClassificationIdentity identity, bool graded, bool shouldSee, Shape s, Guid id)
    {
        var seen = s.HasData && s.Ids.Contains(id);
        var state = !s.Answered ? ClassificationState.NotAvailable : shouldSee == seen ? ClassificationState.Pass : shouldSee ? (graded ? ClassificationState.Fail : ClassificationState.Warning) : ClassificationState.Fail;
        return new ClassificationObservation
        {
            Nivaa = level, Identity = identity, Surface = ClassificationSurface.DirectProfile,
            TestType = graded ? (identity == ClassificationIdentity.Unauthorized ? ClassificationTestType.Negative : ClassificationTestType.Functional) : ClassificationTestType.Functional,
            State = state, Expected = shouldSee ? "profile returned" : "no data (anti-disclosure: same as a nonexistent child)", Observed = s.Describe() + (s.Level is { } l ? $", level {l}" : ""),
            Detail = state switch
            {
                ClassificationState.Pass when !shouldSee => "Anti-disclosure behavior verified: the graded test child was not returned.",
                ClassificationState.Pass => "Positive control: the test child was returned.",
                ClassificationState.Fail when !shouldSee => "The graded test child's profile was returned to an identity without graded access.",
                ClassificationState.Fail => "Positive control failed: an identity with graded access did not get the graded test child (check the grant, the data, or a source rule that hides graded children).",
                ClassificationState.Warning => "The ungraded test child was not returned — check the identity's general access.",
                _ => "The endpoint did not answer.",
            },
        };
    }

    private static ClassificationObservation Compare(int level, Shape classified, Shape missing) => new()
    {
        Nivaa = level, Identity = ClassificationIdentity.Unauthorized, Surface = ClassificationSurface.NonexistentComparison, TestType = ClassificationTestType.Negative,
        State = !classified.Answered || !missing.Answered ? ClassificationState.NotAvailable : classified.Signature == missing.Signature ? ClassificationState.Pass : ClassificationState.Fail,
        Expected = "identical response for the unauthorized graded child and a random nonexistent id", Observed = $"graded: {classified.Describe()}; nonexistent: {missing.Describe()}",
        Detail = classified.Signature == missing.Signature ? "Indistinguishable by status, data and error codes (timing not measured)." : "The responses differ, so the child's existence can be inferred.",
    };

    private static ClassificationObservation AuditObservation(int level, ClassificationIdentity identity, bool graded, bool shouldSee, Shape s) => new()
    {
        Nivaa = level, Identity = identity, Surface = ClassificationSurface.AuditLog, TestType = identity == ClassificationIdentity.Unauthorized && graded ? ClassificationTestType.Negative : ClassificationTestType.Functional,
        State = !s.Answered ? ClassificationState.NotAvailable : !graded ? ClassificationState.Observed : shouldSee == s.HasData ? ClassificationState.Pass : shouldSee ? ClassificationState.Fail : ClassificationState.Fail,
        Expected = !graded ? "(observed only — revision-log access for ungraded children depends on the general operation)" : shouldSee ? "revision log returned" : "no data (anti-disclosure)",
        Observed = s.Describe(), Detail = !graded ? "Revision-log response recorded." : s.HasData == shouldSee ? "As expected." : shouldSee ? "An identity with graded access got no revision log for the graded test child." : "The revision log of the graded test child was returned to an identity without graded access.",
    };

    private static IEnumerable<ClassificationObservation> SearchObservations(int level, ClassificationIdentity identity, bool graded, bool shouldSee, Shape s, Guid? id)
    {
        var present = id is { } g ? s.Ids.Contains(g) : s.Ids.Count > 0;
        yield return new ClassificationObservation
        {
            Nivaa = level, Identity = identity, Surface = ClassificationSurface.Search, TestType = identity == ClassificationIdentity.Unauthorized && graded ? ClassificationTestType.Negative : ClassificationTestType.Functional,
            State = !s.Answered || !s.HasData && s.Errors.Count > 0 ? ClassificationState.NotAvailable : shouldSee == present ? ClassificationState.Pass : graded || !shouldSee ? ClassificationState.Fail : ClassificationState.Warning,
            Expected = shouldSee ? "test child present in the exact-BiRK-id search" : "test child absent", Observed = s.Answered ? (present ? "present" : "absent") + (s.Errors.Count > 0 ? $" (errors {string.Join("/", s.Errors)})" : "") : s.Describe(),
            Detail = "Search by the test child's exact BiRK id only — never a broad search.",
        };
        if (graded && identity == ClassificationIdentity.Unauthorized)
            yield return new ClassificationObservation
            {
                Nivaa = level, Identity = identity, Surface = ClassificationSurface.SearchTotalCount, TestType = ClassificationTestType.Negative,
                State = s.Total is null ? ClassificationState.NotAvailable : s.Total == 0 ? ClassificationState.Pass : ClassificationState.Fail,
                Expected = "totaltAntall 0 (the hidden child is not counted)", Observed = s.Total is { } t ? $"totaltAntall {t}" : "not reported",
                Detail = s.Total is > 0 ? "The count reveals a record the identity may not see." : "The count does not reveal the hidden test child.",
            };
    }
}
