using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BirkNext.Api.Filters;

/// <summary>
/// Controller filter restricting access to Development/Test environments only.
/// Used for test fixtures and development-only endpoints.
/// Returns 404 Not Found in Production to hide development infrastructure.
/// </summary>
public sealed class DevelopmentOnlyControllerFilter : IActionFilter
{
    private readonly IWebHostEnvironment _environment;

    public DevelopmentOnlyControllerFilter(IWebHostEnvironment environment)
    {
        _environment = environment;
    }

    public void OnActionExecuting(ActionExecutingContext context)
    {
        // Only allow in Development environment
        if (!_environment.IsDevelopment())
        {
            // Return 404 to hide development infrastructure
            context.Result = new NotFoundResult();
        }
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}
