using BirkNext.Web.Models;
using System.Net.Http.Json;

namespace BirkNext.Web.Services;

public class SampleProjectsApiService(HttpClient client)
{
    public async Task<List<SampleProjectDto>> GetProjectsAsync()
    {
        var result = await client.GetFromJsonAsync<List<SampleProjectDto>>("api/sample-projects");
        return result ?? [];
    }

    public async Task<SampleProjectsMetaDto?> GetMetaAsync()
    {
        try
        {
            return await client.GetFromJsonAsync<SampleProjectsMetaDto>("api/sample-projects/meta");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>All readable candidate documents of one project; null when the request fails.</summary>
    public async Task<List<SampleDocumentContentDto>?> GetDocumentsAsync(string slug)
    {
        try
        {
            var response = await client.GetAsync($"api/sample-projects/{Uri.EscapeDataString(slug)}/documents");
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<List<SampleDocumentContentDto>>();
        }
        catch
        {
            return null;
        }
    }

    /// <param name="filename">Project-relative document path (for example <c>specs/001-feature/spec.md</c>).</param>
    public async Task<string?> GetFileAsync(string slug, string filename)
    {
        try
        {
            var encoded = Uri.EscapeDataString(filename);
            var response = await client.GetAsync($"api/sample-projects/{Uri.EscapeDataString(slug)}/file?filename={encoded}");
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsStringAsync();
        }
        catch
        {
            return null;
        }
    }
}
