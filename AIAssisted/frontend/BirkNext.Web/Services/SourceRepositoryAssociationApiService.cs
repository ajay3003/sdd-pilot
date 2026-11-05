namespace BirkNext.Web.Services;

/// <summary>Remembered "same repository" declarations (backend api/source-repository-associations). Declaring happens with a confirmed change-review
/// run ("Remember for future comparisons"); revoking ends a declaration for future comparisons without deleting it.</summary>
public interface ISourceRepositoryAssociationApiService
{
    Task<string?> RevokeAsync(Guid id, CancellationToken ct = default);
}

public sealed class SourceRepositoryAssociationApiService(HttpClient http) : ISourceRepositoryAssociationApiService
{
    public async Task<string?> RevokeAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.PostAsync($"api/source-repository-associations/{id}/revoke", null, ct);
            return response.IsSuccessStatusCode ? null : $"The declaration could not be revoked ({(int)response.StatusCode}).";
        }
        catch (Exception ex) when (BackendRequestClassifier.IsRequestFailure(ex)) { return "The backend could not be reached; the declaration is unchanged."; }
    }
}
