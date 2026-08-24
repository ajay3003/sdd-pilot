using System.Net;
using System.Text.Json;
using BirkNext.Web.Models;
using BirkNext.Web.Services;
using FluentAssertions;
using Xunit;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// REAL FRONTEND HTTP TRANSMISSION TEST
/// Proves thresholds are actually serialized and sent to backend.
/// Invokes BlazorWasmPerformanceReviewService.DiscoverAssetsAsync() with real HttpClient.
/// Captures the outgoing JSON request to verify threshold payload structure and byte→MB conversion.
/// </summary>
public sealed class FrontendQualityReviewThresholdTransmissionTest
{
    private sealed class CapturingHttpMessageHandler : HttpMessageHandler
    {
        public HttpRequestMessage? CapturedRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CapturedRequest = request;

            if (request.Content is not null)
            {
                var content = await request.Content.ReadAsStringAsync(cancellationToken);
                request.Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new WasmAssetDiscoveryResult
                    {
                        TargetUrl = "https://example.com",
                        IsBlazorWasm = false,
                        Assets = [],
                        Findings = [],
                        Recommendations = []
                    }),
                    System.Text.Encoding.UTF8,
                    "application/json")
            };
        }
    }

    [Fact]
    public async Task DiscoverAssetsAsync_WithConfiguredThresholds_SendsThresholdPayloadToBackend()
    {
        var handler = new CapturingHttpMessageHandler();
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.example.com/")
        };

        var service = new BlazorWasmPerformanceReviewService(client);

        var thresholds = new FrontendPerformanceThresholds
        {
            MaxStartupRequests = 17,
            MaxStartupSizeBytes = 5 * 1024 * 1024,        // 5 MB
            MaxFrameworkSizeBytes = 3 * 1024 * 1024,      // 3 MB
            MaxApplicationAssemblySizeBytes = 2 * 1024 * 1024, // 2 MB
            MaxIndividualAssetSizeBytes = 512 * 1024      // 512 KB
        };

        await service.DiscoverAssetsAsync("https://example.com", thresholds);

        handler.CapturedRequest.Should().NotBeNull("request should be captured");
        handler.CapturedRequest!.Content.Should().NotBeNull("request should have content");

        var contentString = await handler.CapturedRequest.Content!.ReadAsStringAsync();
        var doc = JsonDocument.Parse(contentString);
        var root = doc.RootElement;

        // Verify targetUrl is present and correct
        root.GetProperty("targetUrl").GetString().Should().Be("https://example.com");

        // Verify thresholds object exists
        root.TryGetProperty("thresholds", out var thresholdsElement).Should().BeTrue("thresholds should be in payload");

        // Verify MaxStartupRequests is transmitted as-is (not converted)
        thresholdsElement.GetProperty("maxStartupRequests").GetInt32()
            .Should().Be(17, "MaxStartupRequests should be transmitted without conversion");

        // Verify byte→MB conversions
        // 5 MB = 5242880 bytes; Math.Ceiling(5242880 / 1048576.0) = 5.0
        thresholdsElement.GetProperty("maxStartupDownloadMB").GetDouble()
            .Should().Be(5.0, "MaxStartupDownloadMB should be ceiling of (bytes / 1048576.0)");

        // 3 MB = 3145728 bytes; Math.Ceiling(3145728 / 1048576.0) = 3.0
        thresholdsElement.GetProperty("maxFrameworkMB").GetDouble()
            .Should().Be(3.0, "MaxFrameworkMB should be ceiling of (bytes / 1048576.0)");

        // 2 MB = 2097152 bytes; Math.Ceiling(2097152 / 1048576.0) = 2.0
        thresholdsElement.GetProperty("maxApplicationMB").GetDouble()
            .Should().Be(2.0, "MaxApplicationMB should be ceiling of (bytes / 1048576.0)");

        // 512 KB = 524288 bytes; Math.Ceiling(524288 / 1048576.0) = 1.0 (rounds up)
        thresholdsElement.GetProperty("maxIndividualAssetMB").GetDouble()
            .Should().Be(1.0, "MaxIndividualAssetMB should be ceiling of (bytes / 1048576.0)");
    }

    [Fact]
    public async Task DiscoverAssetsAsync_WithNullThresholds_DoesNotIncludeThresholdsInPayload()
    {
        var handler = new CapturingHttpMessageHandler();
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.example.com/")
        };

        var service = new BlazorWasmPerformanceReviewService(client);

        await service.DiscoverAssetsAsync("https://example.com", thresholds: null);

        handler.CapturedRequest.Should().NotBeNull("request should be captured");

        var contentString = await handler.CapturedRequest!.Content!.ReadAsStringAsync();
        var doc = JsonDocument.Parse(contentString);
        var root = doc.RootElement;

        // When thresholds is null, it should either be absent or null
        if (root.TryGetProperty("thresholds", out var thresholdsElement))
        {
            thresholdsElement.ValueKind.Should().Be(JsonValueKind.Null,
                "thresholds should be null when not configured");
        }
    }

    [Fact]
    public async Task DiscoverAssetsAsync_SmallByteValue_CeilingConversionRoundsUp()
    {
        var handler = new CapturingHttpMessageHandler();
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.example.com/")
        };

        var service = new BlazorWasmPerformanceReviewService(client);

        var thresholds = new FrontendPerformanceThresholds
        {
            MaxStartupRequests = 100,
            MaxStartupSizeBytes = 1,           // 1 byte should ceil to 1 MB (not 0)
            MaxFrameworkSizeBytes = 1024 * 512, // 512 KB should ceil to 1 MB
            MaxApplicationAssemblySizeBytes = 1024 * 1024 + 1, // 1 MB + 1 byte should ceil to 2 MB
            MaxIndividualAssetSizeBytes = 1024 * 1024 // exactly 1 MB
        };

        await service.DiscoverAssetsAsync("https://example.com", thresholds);

        var contentString = await handler.CapturedRequest!.Content!.ReadAsStringAsync();
        var doc = JsonDocument.Parse(contentString);
        var thresholdsElement = doc.RootElement.GetProperty("thresholds");

        // 1 byte → Math.Ceiling(1 / 1048576.0) = 1.0
        thresholdsElement.GetProperty("maxStartupDownloadMB").GetDouble()
            .Should().Be(1.0, "1 byte should ceiling to 1 MB");

        // 512 KB → Math.Ceiling(524288 / 1048576.0) = 1.0
        thresholdsElement.GetProperty("maxFrameworkMB").GetDouble()
            .Should().Be(1.0, "512 KB should ceiling to 1 MB");

        // 1 MB + 1 byte → Math.Ceiling(1048577 / 1048576.0) = 2.0
        thresholdsElement.GetProperty("maxApplicationMB").GetDouble()
            .Should().Be(2.0, "1 MB + 1 byte should ceiling to 2 MB");

        // exactly 1 MB → Math.Ceiling(1048576 / 1048576.0) = 1.0
        thresholdsElement.GetProperty("maxIndividualAssetMB").GetDouble()
            .Should().Be(1.0, "exactly 1 MB should be 1 MB");
    }
}
