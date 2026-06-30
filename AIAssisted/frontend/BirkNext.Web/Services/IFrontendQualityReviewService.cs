using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public interface IFrontendQualityReviewService
{
    FrontendQualityReviewReport BuildReport(
        string targetUrl,
        WasmSecurityReviewReport? securityReport,
        WasmPerformanceReviewReport? performanceReport);
}
