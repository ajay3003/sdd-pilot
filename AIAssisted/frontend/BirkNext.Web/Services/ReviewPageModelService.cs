using BirkNext.Web.Models.Review;
using System.Net.Http.Json;

namespace BirkNext.Web.Services;

/// <summary>
/// Frontend service that loads ReviewPageModels from the backend API.
/// </summary>
public class ReviewPageModelService(HttpClient httpClient)
{
    public async Task<ReviewPageModel?> GetDashboardModelAsync()
    {
        try
        {
            return await httpClient.GetFromJsonAsync<ReviewPageModel>("api/review-page-model/dashboard");
        }
        catch
        {
            return null;
        }
    }

    public async Task<ReviewPageModel?> GetConstitutionExplorerModelAsync()
    {
        try
        {
            return await httpClient.GetFromJsonAsync<ReviewPageModel>("api/review-page-model/constitution-explorer");
        }
        catch
        {
            return null;
        }
    }

    public async Task<ReviewPageModel?> GetDataModelExplorerModelAsync()
    {
        try
        {
            return await httpClient.GetFromJsonAsync<ReviewPageModel>("api/review-page-model/data-model-explorer");
        }
        catch
        {
            return null;
        }
    }

    public async Task<ReviewPageModel?> GetPlanExplorerModelAsync()
    {
        try
        {
            return await httpClient.GetFromJsonAsync<ReviewPageModel>("api/review-page-model/plan-explorer");
        }
        catch
        {
            return null;
        }
    }

    public async Task<ReviewPageModel?> GetTaskExplorerModelAsync()
    {
        try
        {
            return await httpClient.GetFromJsonAsync<ReviewPageModel>("api/review-page-model/task-explorer");
        }
        catch
        {
            return null;
        }
    }

}
