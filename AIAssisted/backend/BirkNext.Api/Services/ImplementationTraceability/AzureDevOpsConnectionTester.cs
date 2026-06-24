using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BirkNext.Api.Configuration;
using BirkNext.Api.Models.Admin;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BirkNext.Api.Services.ImplementationTraceability;

public sealed class AzureDevOpsConnectionTester
{
    private readonly HttpClient _http;
    private readonly AzureDevOpsOptions _options;
    private readonly ILogger<AzureDevOpsConnectionTester> _logger;

    public AzureDevOpsConnectionTester(
        HttpClient http,
        IOptions<AzureDevOpsOptions> options,
        ILogger<AzureDevOpsConnectionTester> logger)
    {
        _http    = http;
        _options = options.Value;
        _logger  = logger;

        if (_options.IsConfigured)
            ConfigureAuth();
    }

    public async Task<AzureDevOpsConnectionTestResult> TestAsync(CancellationToken ct = default)
    {
        if (!_options.IsConfigured)
        {
            return new AzureDevOpsConnectionTestResult
            {
                OverallSuccess = false,
                ErrorMessage   = "Azure DevOps is not configured (Enabled=false or PAT is missing).",
                Checks =
                [
                    new() { Name = "Configuration", Success = false, Detail = "Enabled=false or PAT missing." },
                ],
            };
        }

        var checks = new List<AzureDevOpsCheckResult>();

        // ── Check 1: PAT auth + org reachable ──────────────────────────────
        var (authOk, projects, authDetail) = await CheckOrgAsync(ct);
        checks.Add(new AzureDevOpsCheckResult
        {
            Name    = "PAT Authentication & Organization",
            Success = authOk,
            Detail  = authDetail,
        });

        if (!authOk)
        {
            return new AzureDevOpsConnectionTestResult
            {
                OverallSuccess = false,
                ErrorMessage   = authDetail,
                Checks         = checks,
            };
        }

        // ── Check 2: Project exists ─────────────────────────────────────────
        var (projectOk, projectDetail) = CheckProject(projects);
        checks.Add(new AzureDevOpsCheckResult
        {
            Name    = "Project",
            Success = projectOk,
            Detail  = projectDetail,
        });

        // ── Check 3: Repository exists ──────────────────────────────────────
        var (repoOk, repoDetail) = await CheckRepositoryAsync(ct);
        checks.Add(new AzureDevOpsCheckResult
        {
            Name    = "Repository",
            Success = repoOk,
            Detail  = repoDetail,
        });

        var allOk = checks.All(c => c.Success);
        return new AzureDevOpsConnectionTestResult
        {
            OverallSuccess = allOk,
            ErrorMessage   = allOk ? null : checks.FirstOrDefault(c => !c.Success)?.Detail,
            Checks         = checks,
        };
    }

    // ── Check implementations ───────────────────────────────────────────────

    private async Task<(bool Ok, List<string> ProjectNames, string Detail)> CheckOrgAsync(CancellationToken ct)
    {
        var url = $"{_options.OrganizationUrl.TrimEnd('/')}/_apis/projects?api-version=7.1&$top=100";
        try
        {
            using var response = await _http.GetAsync(url, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized ||
                response.StatusCode == HttpStatusCode.Forbidden)
            {
                _logger.LogWarning("ADO test-connection: auth failed ({StatusCode})", (int)response.StatusCode);
                return (false, [], $"Authentication failed (HTTP {(int)response.StatusCode}). Check that the PAT is valid and has Read access.");
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("ADO test-connection: org check returned {StatusCode}", (int)response.StatusCode);
                return (false, [], $"Organization not reachable (HTTP {(int)response.StatusCode}).");
            }

            var json     = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var names    = new List<string>();

            if (doc.RootElement.TryGetProperty("value", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("name", out var n))
                        names.Add(n.GetString() ?? "");
                }
            }

            return (true, names, $"Organization reachable. {names.Count} project(s) visible.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("ADO test-connection: org unreachable — {Message}", ex.Message);
            return (false, [], "Organization URL is not reachable. Check the URL and network access.");
        }
        catch (TaskCanceledException)
        {
            return (false, [], "Request timed out reaching the organization URL.");
        }
    }

    private (bool Ok, string Detail) CheckProject(List<string> visibleProjects)
    {
        if (string.IsNullOrWhiteSpace(_options.Project))
            return (false, "Project name is not configured.");

        var found = visibleProjects.Any(p =>
            string.Equals(p, _options.Project, StringComparison.OrdinalIgnoreCase));

        return found
            ? (true, $"Project '{_options.Project}' found.")
            : (false, $"Project '{_options.Project}' not found in the organization. Check the project name and PAT permissions.");
    }

    private async Task<(bool Ok, string Detail)> CheckRepositoryAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.RepositoryId))
            return (false, "Repository ID/name is not configured.");

        var url = $"{_options.OrganizationUrl.TrimEnd('/')}/{Uri.EscapeDataString(_options.Project)}" +
                  $"/_apis/git/repositories/{Uri.EscapeDataString(_options.RepositoryId)}?api-version=7.1";
        try
        {
            using var response = await _http.GetAsync(url, ct);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return (false, $"Repository '{_options.RepositoryId}' not found in project '{_options.Project}'.");

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("ADO test-connection: repo check returned {StatusCode}", (int)response.StatusCode);
                return (false, $"Repository check failed (HTTP {(int)response.StatusCode}).");
            }

            var json     = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var name     = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;

            return (true, $"Repository '{name ?? _options.RepositoryId}' found.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("ADO test-connection: repo check failed — {Message}", ex.Message);
            return (false, "Repository request failed. Check network access.");
        }
    }

    private void ConfigureAuth()
    {
        var encoded = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{_options.Pat}"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", encoded);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }
}
