using BirkNext.Web.Models;
using System.Net.Http.Json;

namespace BirkNext.Web.Services;

public class SampleProjectsApiService(HttpClient client)
{
    public async Task<List<SampleProjectDto>> GetProjectsAsync()
    {
        try
        {
            var result = await client.GetFromJsonAsync<List<SampleProjectDto>>("api/sample-projects");
            return result ?? [];
        }
        catch
        {
            return [];
        }
    }

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
