namespace BirkNext.Api.Services.WasmSecurity;

public interface IBlazorWasmSecurityReviewService
{
    Task<WasmSecurityReviewReport> ScanAsync(WasmScanRequest request, CancellationToken ct = default);
}
