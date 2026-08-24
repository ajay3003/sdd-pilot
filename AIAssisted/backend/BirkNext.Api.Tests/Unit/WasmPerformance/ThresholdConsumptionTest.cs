using BirkNext.Api.Services.WasmPerformance;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Unit.WasmPerformance;

/// <summary>
/// REAL THRESHOLD EXECUTION TEST
/// Proves configured thresholds actually change analyzer output.
/// Invokes WasmStartupAnalysisService.Analyze() with identical assets but different thresholds.
/// </summary>
public sealed class ThresholdConsumptionTest
{
    [Fact]
    public void StartupAnalysis_HighThreshold_NoExcessiveRequestFinding()
    {
        // Create assets representing 25 startup requests
        var assets = CreateStartupAssets(requestCount: 25);

        var thresholds = new StartupAnalysisThresholds { MaxStartupRequests = 30 };

        var service = new WasmStartupAnalysisService();
        var result = service.Analyze(assets, thresholds);

        // With threshold of 30 requests, 25 requests should NOT trigger finding
        var excessiveRequestFinding = result.Findings.FirstOrDefault(f => f.Id == "STA-007");
        excessiveRequestFinding.Should().BeNull("no finding when under threshold");
    }

    [Fact]
    public void StartupAnalysis_LowThreshold_ExcessiveRequestFinding()
    {
        // Same assets: 25 startup requests
        var assets = CreateStartupAssets(requestCount: 25);

        var thresholds = new StartupAnalysisThresholds { MaxStartupRequests = 20 };

        var service = new WasmStartupAnalysisService();
        var result = service.Analyze(assets, thresholds);

        // With threshold of 20 requests, 25 requests SHOULD trigger finding
        var excessiveRequestFinding = result.Findings.FirstOrDefault(f => f.Id == "STA-007");
        excessiveRequestFinding.Should().NotBeNull("finding when exceeds threshold");
        excessiveRequestFinding.Title.Should().Contain("High number of startup HTTP requests");
    }

    [Fact]
    public void StartupAnalysis_NullThresholds_UsesDefaults()
    {
        var assets = CreateStartupAssets(requestCount: 25);

        // null thresholds → backend defaults (MaxStartupRequests=150)
        var service = new WasmStartupAnalysisService();
        var resultWithNull = service.Analyze(assets, thresholds: null);

        // 25 requests should NOT exceed default of 150
        var finding = resultWithNull.Findings.FirstOrDefault(f => f.Id == "STA-007");
        finding.Should().BeNull("default threshold is 150, 25 is well under");
    }

    [Fact]
    public void StartupAnalysis_DefaultThresholds_EqualsNullThresholds()
    {
        var assets = CreateStartupAssets(requestCount: 75);

        var service = new WasmStartupAnalysisService();

        // Test 1: with null
        var resultNull = service.Analyze(assets, thresholds: null);

        // Test 2: with explicit default
        var resultDefault = service.Analyze(assets, new StartupAnalysisThresholds());

        // Results should be identical
        var findingNull = resultNull.Findings.Where(f => f.Id == "STA-007").Count();
        var findingDefault = resultDefault.Findings.Where(f => f.Id == "STA-007").Count();

        findingNull.Should().Be(findingDefault, "null should equal explicit default");
    }

    private static IReadOnlyList<DiscoveredAsset> CreateStartupAssets(int requestCount)
    {
        var assets = new List<DiscoveredAsset>();

        for (int i = 0; i < requestCount; i++)
        {
            assets.Add(new DiscoveredAsset
            {
                Url = $"https://example.com/asset-{i}.js",
                Type = AssetType.JavaScript,
                ContentLength = 10000,
                DownloadedBytes = 10000,
                StatusCode = 200,
            });
        }

        return assets;
    }
}
