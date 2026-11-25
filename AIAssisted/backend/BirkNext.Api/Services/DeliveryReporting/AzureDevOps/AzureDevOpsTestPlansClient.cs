using System.Net;
using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BirkNext.TestPlanning;

namespace BirkNext.Api.Services.DeliveryReporting.AzureDevOps;

/// <summary>A safe Azure DevOps Test Plans failure in Norwegian. <see cref="Uncertain"/> = a write whose answer was lost: it may have happened.</summary>
public sealed class AzureDevOpsTestPlansException(string message, string code, HttpStatusCode? status = null, bool uncertain = false) : Exception(message)
{
    public string Code { get; } = code;
    public HttpStatusCode? Status { get; } = status;
    public bool Uncertain { get; } = uncertain;
}

/// <summary>A test case work item as the transfer needs it.</summary>
public sealed record AdoWorkItem(int Id, int Rev, string Title, string WorkItemType, string Project, string? WebUrl);

/// <summary>The fields BirkNext writes on a test case.</summary>
public sealed record AdoTestCaseFields(string Title, IReadOnlyList<(string Action, string Expected)> Steps, string? AreaPath, string? AssignedTo, string? Priority, string? Description);

/// <summary>
/// Azure DevOps Test Plans and test case work items over REST (api-version 7.1), on the SAME stored connection as the Delivery Reporting Git client
/// (<see cref="AzureDevOpsGitClient.Client"/>: one credential path, one base address, the same development-only fake-server seam). Reads are
/// plain; every write is one request that is never retried here. A write that fails without an answer from Azure DevOps (timeout, broken
/// connection after sending, a 5xx) is reported as <c>Uncertain</c>, so callers record it and check before sending it again.
/// Required PAT scopes: Test Management (read &amp; write), Work Items (read &amp; write), Project and Team (read).
/// </summary>
public sealed class AzureDevOpsTestPlansClient(AzureDevOpsGitClient git)
{
    private const int MaxPages = 20;
    /// <summary>The tag every test case created by BirkNext carries (used to find a create whose answer was lost).</summary>
    public const string Tag = "BirkNext";

    private static string P(string project) => Uri.EscapeDataString(project);

    private (HttpClient Client, string Organization) Client(Guid connectionId)
    {
        try { return git.Client(connectionId); }
        catch (AzureDevOpsException) { throw new AzureDevOpsTestPlansException("Tilkoblingen mangler eller kan ikke leses. Velg eller legg til tilkoblingen på nytt.", "connection-missing"); }
    }

    /// <summary>The Azure DevOps web address of a page (the same host as the API: dev.azure.com, or the local fake in development).</summary>
    public string Web(string organization, string relative) => new Uri(git.Base(organization), relative).ToString();

    public string PlanUrl(string organization, string project, int planId, int? suiteId = null) =>
        Web(organization, $"{P(project)}/_testPlans/define?planId={planId}" + (suiteId is { } s ? $"&suiteId={s}" : ""));

    public string WorkItemUrl(string organization, string project, int id) => Web(organization, $"{P(project)}/_workitems/edit/{id}");

    private static AzureDevOpsTestPlansException Refused(HttpStatusCode status, string what) => status switch
    {
        HttpStatusCode.Unauthorized => new("Azure DevOps godtok ikke tilgangsnøkkelen (PAT). Tilkoblingen må kontrolleres.", "authentication", status),
        HttpStatusCode.Forbidden => new($"Du har ikke tilgang til {what}. Tilgangsnøkkelen trenger «Test Management» og «Work Items» (lese og skrive).", "forbidden", status),
        HttpStatusCode.NotFound => new($"Fant ikke {what}. Den kan være slettet eller du mangler tilgang.", "not-found", status),
        HttpStatusCode.TooManyRequests => new("Azure DevOps begrenser antall forespørsler. Vent litt og prøv igjen.", "throttled", status),
        _ => new($"Azure DevOps avviste forespørselen om {what} (HTTP {(int)status}).", "refused", status),
    };

    /// <summary>One request. Reads: any failure is certain. Writes: no answer or a 5xx is uncertain (it may have happened).</summary>
    private static async Task<JsonNode?> SendAsync(HttpClient client, HttpMethod method, string path, HttpContent? body, string what, bool write, CancellationToken ct,
        Action<HttpResponseMessage>? headers = null)
    {
        using var request = new HttpRequestMessage(method, AzureDevOpsGitClient.WithVersion(path)) { Content = body };
        HttpResponseMessage response;
        try { response = await client.SendAsync(request, ct); }
        catch (HttpRequestException e) when (write && e.HttpRequestError is not (HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError))
        { throw new AzureDevOpsTestPlansException($"Svaret fra Azure DevOps ble borte for {what}. Vi vet ikke om det ble utført.", "uncertain", uncertain: true); }
        catch (HttpRequestException) { throw new AzureDevOpsTestPlansException("Får ikke kontakt med Azure DevOps. Kontroller nettverket og prøv igjen.", "unreachable"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw write
                ? new AzureDevOpsTestPlansException($"Azure DevOps svarte ikke i tide for {what}. Vi vet ikke om det ble utført.", "uncertain", uncertain: true)
                : new AzureDevOpsTestPlansException("Azure DevOps svarte ikke i tide. Prøv igjen.", "timeout");
        }
        using (response)
        {
            if (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType is "text/html")
                throw new AzureDevOpsTestPlansException("Azure DevOps svarte med en påloggingsside. Tilkoblingen må kontrolleres.", "authentication", HttpStatusCode.Unauthorized);
            if (!response.IsSuccessStatusCode)
            {
                if (write && (int)response.StatusCode >= 500)
                    throw new AzureDevOpsTestPlansException($"Azure DevOps svarte med en feil (HTTP {(int)response.StatusCode}) for {what}. Vi vet ikke om det ble utført.", "uncertain", response.StatusCode, uncertain: true);
                throw Refused(response.StatusCode, what);
            }
            headers?.Invoke(response);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (text.Length == 0) return null;
            try { return JsonNode.Parse(text); }
            catch (JsonException)
            {
                throw write
                    ? new AzureDevOpsTestPlansException($"Azure DevOps ga et uleselig svar for {what}. Vi vet ikke om det ble utført.", "uncertain", uncertain: true)
                    : new AzureDevOpsTestPlansException($"Azure DevOps ga et uleselig svar for {what}.", "unreadable");
            }
        }
    }

    private static StringContent Json(object body, string mediaType = "application/json") =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, new MediaTypeHeaderValue(mediaType));

    /// <summary>All pages of a list (x-ms-continuationtoken), bounded.</summary>
    private static async Task<List<JsonNode>> PagesAsync(HttpClient client, string path, string what, CancellationToken ct)
    {
        var items = new List<JsonNode>();
        string? token = null;
        for (var page = 0; page < MaxPages; page++)
        {
            string? next = null;
            var body = await SendAsync(client, HttpMethod.Get, path + (token is null ? "" : (path.Contains('?') ? "&" : "?") + "continuationToken=" + Uri.EscapeDataString(token)), null, what, false, ct,
                r => next = r.Headers.TryGetValues("x-ms-continuationtoken", out var v) ? v.FirstOrDefault() : null);
            items.AddRange((body?["value"] as JsonArray ?? []).OfType<JsonNode>());
            if (string.IsNullOrEmpty(next) || next == token) break;
            token = next;
        }
        return items;
    }

    public async Task<List<AdoProjectInfo>> ProjectsAsync(Guid connectionId, CancellationToken ct)
    {
        var (client, _) = Client(connectionId);
        var items = await PagesAsync(client, "_apis/projects?$top=100", "prosjektene", ct);
        return [.. items.Select(p => new AdoProjectInfo(p["id"]?.ToString() ?? "", p["name"]?.ToString() ?? "")).Where(p => p.Name.Length > 0)
            .DistinctBy(p => p.Id).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Validation: the organization answers and the project's test plans can be read.</summary>
    public async Task<int> CheckAsync(Guid connectionId, string project, CancellationToken ct) => (await PlansAsync(connectionId, project, ct)).Count;

    private AdoTestPlanInfo Plan(JsonNode p, string organization, string project)
    {
        var id = p["id"]?.GetValue<int>() ?? 0;
        return new(id, p["name"]?.ToString() ?? "", p["state"]?.ToString(), p["rootSuite"]?["id"]?.GetValue<int>() ?? 0, p["areaPath"]?.ToString(), p["iteration"]?.ToString(),
            id > 0 ? PlanUrl(organization, project, id) : null);
    }

    public async Task<List<AdoTestPlanInfo>> PlansAsync(Guid connectionId, string project, CancellationToken ct)
    {
        var (client, organization) = Client(connectionId);
        var items = await PagesAsync(client, $"{P(project)}/_apis/testplan/plans", $"testplanene i {project}", ct);
        return [.. items.Select(p => Plan(p, organization, project)).Where(p => p.Id > 0).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Creates a test plan (one request, never retried). Iteration and Area Path default to the project root.</summary>
    public async Task<AdoTestPlanInfo> CreatePlanAsync(Guid connectionId, string project, string name, string? description, CancellationToken ct)
    {
        var (client, organization) = Client(connectionId);
        var body = new Dictionary<string, object?> { ["name"] = name, ["areaPath"] = project, ["iteration"] = project };
        if (!string.IsNullOrWhiteSpace(description)) body["description"] = description;
        var answer = await SendAsync(client, HttpMethod.Post, $"{P(project)}/_apis/testplan/plans", Json(body), $"testplanen «{name}»", true, ct);
        var plan = answer is null ? null : Plan(answer, organization, project);
        if (plan is not { Id: > 0 })
            throw new AzureDevOpsTestPlansException($"Azure DevOps svarte uten id for testplanen «{name}». Vi vet ikke om den ble opprettet.", "uncertain", uncertain: true);
        return plan;
    }

    private static AdoTestSuiteInfo Suite(JsonNode s) =>
        new(s["id"]?.GetValue<int>() ?? 0, s["name"]?.ToString() ?? "", s["parentSuite"]?["id"]?.GetValue<int>(), s["suiteType"]?.ToString() ?? "staticTestSuite");

    public async Task<List<AdoTestSuiteInfo>> SuitesAsync(Guid connectionId, string project, int planId, CancellationToken ct)
    {
        var (client, _) = Client(connectionId);
        var items = await PagesAsync(client, $"{P(project)}/_apis/testplan/Plans/{planId}/suites", "testgruppene", ct);
        return [.. items.Select(Suite).Where(s => s.Id > 0)];
    }

    /// <summary>Creates a static test suite under the parent suite (one request, never retried).</summary>
    public async Task<AdoTestSuiteInfo> CreateSuiteAsync(Guid connectionId, string project, int planId, int parentSuiteId, string name, CancellationToken ct)
    {
        var (client, _) = Client(connectionId);
        var body = new { suiteType = "staticTestSuite", name, parentSuite = new { id = parentSuiteId } };
        var answer = await SendAsync(client, HttpMethod.Post, $"{P(project)}/_apis/testplan/Plans/{planId}/suites", Json(body), $"testgruppen «{name}»", true, ct);
        var suite = answer is null ? null : Suite(answer);
        if (suite is not { Id: > 0 })
            throw new AzureDevOpsTestPlansException($"Azure DevOps svarte uten id for testgruppen «{name}». Vi vet ikke om den ble opprettet.", "uncertain", uncertain: true);
        return suite with { ParentId = suite.ParentId ?? parentSuiteId };
    }

    /// <summary>The test cases in a suite: work item id and title.</summary>
    public async Task<List<(int Id, string Title)>> SuiteTestCasesAsync(Guid connectionId, string project, int planId, int suiteId, CancellationToken ct)
    {
        var (client, _) = Client(connectionId);
        var items = await PagesAsync(client, $"{P(project)}/_apis/testplan/Plans/{planId}/Suites/{suiteId}/TestCase", "testtilfellene i testgruppen", ct);
        return [.. items.Select(i => (Id: i["workItem"]?["id"]?.GetValue<int>() ?? 0, Title: i["workItem"]?["name"]?.ToString() ?? "")).Where(i => i.Id > 0)];
    }

    /// <summary>A test point with its last result (read-only; phase 2 «Hent resultater»).</summary>
    public sealed record TestPointResult(int PointId, int TestCaseId, string? Configuration, string Outcome, int? RunId, int? ResultId, DateTimeOffset? CompletedAt, string? Tester);

    /// <summary>The suite's test points and each point's last result (points never run have no result id).</summary>
    public async Task<List<TestPointResult>> PointsAsync(Guid connectionId, string project, int planId, int suiteId, CancellationToken ct)
    {
        var (client, _) = Client(connectionId);
        var items = await PagesAsync(client, $"{P(project)}/_apis/testplan/Plans/{planId}/Suites/{suiteId}/TestPoint?includePointDetails=true", "testresultatene i testgruppen", ct);
        static int? Int(JsonNode? n) => n is null ? null : int.TryParse(n.ToString(), out var v) && v > 0 ? v : null;
        return [.. items.Select(p => new TestPointResult(
            Int(p["id"]) ?? 0, Int(p["testCaseReference"]?["id"]) ?? 0, p["configuration"]?["name"]?.ToString(),
            p["results"]?["outcome"]?.ToString() ?? "Unspecified", Int(p["results"]?["lastTestRunId"]), Int(p["results"]?["lastResultId"]),
            DateTimeOffset.TryParse(p["results"]?["lastResultDetails"]?["dateCompleted"]?.ToString(), out var at) ? at : null,
            p["results"]?["lastResultDetails"]?["runBy"]?["displayName"]?.ToString())).Where(p => p.PointId > 0 && p.TestCaseId > 0)];
    }

    public string ResultUrl(string organization, string project, int runId, int resultId) =>
        Web(organization, $"{P(project)}/_testManagement/runs?runId={runId}&_a=resultSummary&resultId={resultId}");

    public async Task AddToSuiteAsync(Guid connectionId, string project, int planId, int suiteId, IReadOnlyCollection<int> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return;
        var (client, _) = Client(connectionId);
        var body = ids.Select(id => new { workItem = new { id } }).ToArray();
        await SendAsync(client, HttpMethod.Post, $"{P(project)}/_apis/testplan/Plans/{planId}/Suites/{suiteId}/TestCase", Json(body), "testgruppen", true, ct);
    }

    /// <summary>The work item, or null when it no longer exists (404).</summary>
    public async Task<AdoWorkItem?> WorkItemAsync(Guid connectionId, int id, CancellationToken ct)
    {
        var (client, organization) = Client(connectionId);
        try
        {
            var w = await SendAsync(client, HttpMethod.Get, $"_apis/wit/workitems/{id}", null, $"testtilfelle {id}", false, ct);
            return w is null ? null : Item(w, organization);
        }
        catch (AzureDevOpsTestPlansException e) when (e.Status == HttpStatusCode.NotFound) { return null; }
    }

    private AdoWorkItem Item(JsonNode w, string organization)
    {
        var id = w["id"]?.GetValue<int>() ?? 0;
        var project = w["fields"]?["System.TeamProject"]?.ToString() ?? "";
        return new(id, w["rev"]?.GetValue<int>() ?? 0, w["fields"]?["System.Title"]?.ToString() ?? "", w["fields"]?["System.WorkItemType"]?.ToString() ?? "", project,
            id > 0 && project.Length > 0 ? WorkItemUrl(organization, project, id) : null);
    }

    /// <summary>Microsoft.VSTS.TCM.Steps: the test steps XML (each text HTML-encoded inside the XML, as Azure DevOps stores it).</summary>
    public static string StepsXml(IReadOnlyList<(string Action, string Expected)> steps)
    {
        // Only the HTML-special characters are encoded, so Norwegian letters stay readable in Azure DevOps (not &#197;).
        static string Html(string text) => "<DIV><P>" + Encode(text).Replace("\r\n", "\n").Replace("\n", "<BR/>") + "</P></DIV>";
        var sb = new StringBuilder($"<steps id=\"0\" last=\"{steps.Count + 1}\">");
        for (var i = 0; i < steps.Count; i++)
        {
            var (action, expected) = steps[i];
            sb.Append($"<step id=\"{i + 2}\" type=\"{(expected.Length > 0 ? "ValidateStep" : "ActionStep")}\">")
              .Append($"<parameterizedString isformatted=\"true\">{SecurityElement.Escape(Html(action))}</parameterizedString>")
              .Append($"<parameterizedString isformatted=\"true\">{SecurityElement.Escape(expected.Length > 0 ? Html(expected) : "")}</parameterizedString>")
              .Append("<description/></step>");
        }
        return sb.Append("</steps>").ToString();
    }

    private static string Encode(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static List<object> Patch(AdoTestCaseFields f, bool create)
    {
        var op = create ? "add" : "replace";
        var ops = new List<object>
        {
            new { op, path = "/fields/System.Title", value = f.Title },
            new { op, path = "/fields/Microsoft.VSTS.TCM.Steps", value = StepsXml(f.Steps) },
        };
        if (!string.IsNullOrWhiteSpace(f.AreaPath)) ops.Add(new { op, path = "/fields/System.AreaPath", value = f.AreaPath });
        if (!string.IsNullOrWhiteSpace(f.AssignedTo)) ops.Add(new { op, path = "/fields/System.AssignedTo", value = f.AssignedTo });
        if (!string.IsNullOrWhiteSpace(f.Priority)) ops.Add(new { op, path = "/fields/Microsoft.VSTS.Common.Priority", value = f.Priority });
        if (!string.IsNullOrWhiteSpace(f.Description)) ops.Add(new { op, path = "/fields/System.Description", value = Encode(f.Description) });
        if (create) ops.Add(new { op = "add", path = "/fields/System.Tags", value = Tag });
        return ops;
    }

    /// <summary>Creates a test case (one request, never retried).</summary>
    public async Task<AdoWorkItem> CreateTestCaseAsync(Guid connectionId, string project, AdoTestCaseFields fields, CancellationToken ct)
    {
        var (client, organization) = Client(connectionId);
        var answer = await SendAsync(client, HttpMethod.Post, $"{P(project)}/_apis/wit/workitems/$Test%20Case", Json(Patch(fields, true), "application/json-patch+json"),
            $"testtilfellet «{fields.Title}»", true, ct);
        var item = answer is null ? null : Item(answer, organization);
        if (item is not { Id: > 0 })
            throw new AzureDevOpsTestPlansException($"Azure DevOps svarte uten id for «{fields.Title}». Vi vet ikke om testtilfellet ble opprettet.", "uncertain", uncertain: true);
        return item;
    }

    /// <summary>Updates a test case only if it is still at <paramref name="expectedRev"/> (JSON Patch "test" on /rev): a remote edit is never overwritten.</summary>
    public async Task<AdoWorkItem> UpdateTestCaseAsync(Guid connectionId, int id, int expectedRev, AdoTestCaseFields fields, CancellationToken ct)
    {
        var (client, organization) = Client(connectionId);
        var ops = new List<object> { new { op = "test", path = "/rev", value = expectedRev } };
        ops.AddRange(Patch(fields, false));
        try
        {
            var answer = await SendAsync(client, HttpMethod.Patch, $"_apis/wit/workitems/{id}", Json(ops, "application/json-patch+json"), $"testtilfelle {id}", true, ct);
            return answer is null ? throw new AzureDevOpsTestPlansException($"Azure DevOps svarte uten innhold for testtilfelle {id}. Vi vet ikke om det ble oppdatert.", "uncertain", uncertain: true)
                : Item(answer, organization);
        }
        catch (AzureDevOpsTestPlansException e) when (!e.Uncertain && e.Status is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            throw new AzureDevOpsTestPlansException($"Testtilfelle {id} er endret i Azure DevOps etter siste overføring. Det ble ikke overskrevet.", "changed-remotely", e.Status);
        }
    }

    /// <summary>Test cases created by BirkNext with this exact title since the time (read-only WIQL) — to check a create whose answer was lost.</summary>
    public async Task<List<int>> FindCreatedAsync(Guid connectionId, string project, string title, DateTimeOffset since, CancellationToken ct)
    {
        var (client, _) = Client(connectionId);
        static string Q(string v) => v.Replace("'", "''");
        var query = "SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = '" + Q(project) + "' AND [System.WorkItemType] = 'Test Case' AND [System.Title] = '" + Q(title)
            + "' AND [System.Tags] CONTAINS '" + Tag + "' AND [System.CreatedDate] >= '" + since.ToUniversalTime().AddMinutes(-2).ToString("yyyy-MM-ddTHH:mm:ssZ") + "'";
        var body = await SendAsync(client, HttpMethod.Post, $"{P(project)}/_apis/wit/wiql?timePrecision=true", Json(new { query }), "søket etter testtilfellet", false, ct);
        return [.. (body?["workItems"] as JsonArray ?? []).Select(w => w?["id"]?.GetValue<int>() ?? 0).Where(i => i > 0)];
    }
}
