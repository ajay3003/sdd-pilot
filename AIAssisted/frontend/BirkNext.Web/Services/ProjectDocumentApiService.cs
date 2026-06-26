using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace BirkNext.Web.Services;

public class ProjectDocumentApiService(HttpClient client)
{
    public async Task<string?> GetAsync(string kind)
    {
        try
        {
            var response = await client.GetAsync($"api/project-documents/{kind}");
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return null;
            if (!response.IsSuccessStatusCode) return null;
            var dto = await response.Content.ReadFromJsonAsync<DocumentContentDto>();
            return dto?.Content;
        }
        catch
        {
            return null;
        }
    }

    public async Task SaveAsync(string kind, string content)
    {
        try
        {
            await client.PutAsJsonAsync($"api/project-documents/{kind}", new { content });
        }
        catch { }
    }

    public async Task<IReadOnlyList<DocumentSummaryDto>> GetSummaryAsync()
    {
        try
        {
            var result = await client.GetFromJsonAsync<List<DocumentSummaryDto>>("api/project-documents");
            return result ?? [];
        }
        catch
        {
            return [];
        }
    }
}

public class DocumentContentDto
{
    [JsonPropertyName("documentKind")] public string DocumentKind { get; set; } = "";
    [JsonPropertyName("content")] public string Content { get; set; } = "";
}

public class DocumentSummaryDto
{
    [JsonPropertyName("documentKind")] public string DocumentKind { get; set; } = "";
    [JsonPropertyName("contentLengthChars")] public int ContentLengthChars { get; set; }
    [JsonPropertyName("updatedUtc")] public DateTimeOffset UpdatedUtc { get; set; }
}
