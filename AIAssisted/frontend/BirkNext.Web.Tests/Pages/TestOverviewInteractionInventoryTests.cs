using System.Text.Json;
using System.Text.RegularExpressions;

namespace BirkNext.Web.Tests.Pages;

public sealed class TestOverviewInteractionInventoryTests
{
    [Fact]
    public void Testoversikt_test_id_inventory_must_be_updated_when_controls_are_added()
    {
        var root = FindRepositoryRoot();
        var inventoryPath = Path.Combine(root, "AIAssisted", "frontend", "BirkNext.Web.Tests", "TestOverview", "interaction-inventory.json");
        using var inventory = JsonDocument.Parse(File.ReadAllText(inventoryPath));
        var inventoriedSourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in inventory.RootElement.GetProperty("guardedSources").EnumerateArray())
        {
            var relativePath = source.GetProperty("path").GetString()!;
            var expectedIds = source.GetProperty("testIds").EnumerateArray().Select(id => id.GetString()!).ToArray();
            Assert.Equal(expectedIds.Length, expectedIds.Distinct(StringComparer.Ordinal).Count());
            inventoriedSourceIds.UnionWith(expectedIds);
            var sourcePath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var contents = File.ReadAllText(sourcePath);
            var actualIds = Regex.Matches(contents, "data-testid=\\\"([^\\\"]+)\\\"")
                .Select(match => match.Groups[1].Value)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var orderedExpected = expectedIds.Order(StringComparer.Ordinal).ToArray();
            Assert.True(actualIds.SequenceEqual(orderedExpected, StringComparer.Ordinal),
                $"{relativePath} differs from its stable-ID inventory. Missing: {string.Join(", ", orderedExpected.Except(actualIds, StringComparer.Ordinal))}. " +
                $"Unexpected: {string.Join(", ", actualIds.Except(orderedExpected, StringComparer.Ordinal))}. Update interaction-inventory.json and add behavior/scope coverage.");
        }

        var controls = inventory.RootElement.GetProperty("controls").EnumerateArray().ToArray();
        var controlIds = controls.Select(control => control.GetProperty("controlId").GetString()!).ToArray();
        Assert.Equal(controlIds.Length, controlIds.Distinct(StringComparer.Ordinal).Count());
        foreach (var control in controls)
        {
            var controlId = control.GetProperty("controlId").GetString()!;
            Assert.Contains(controlId, inventoriedSourceIds);
            Assert.False(string.IsNullOrWhiteSpace(control.GetProperty("component").GetString()), $"{controlId} has no component contract.");
            Assert.False(string.IsNullOrWhiteSpace(control.GetProperty("type").GetString()), $"{controlId} has no control type.");
            Assert.False(string.IsNullOrWhiteSpace(control.GetProperty("behavior").GetString()), $"{controlId} has no behavior contract.");
            Assert.False(string.IsNullOrWhiteSpace(control.GetProperty("destinationOrTransition").GetString()), $"{controlId} has no transition contract.");
            Assert.NotEmpty(control.GetProperty("applicableScopes").EnumerateArray());
            Assert.NotEmpty(control.GetProperty("requiredParameters").EnumerateArray());
            Assert.NotEmpty(control.GetProperty("automatedTestIds").EnumerateArray());
        }
    }

    /// <summary>
    /// V12 (2026-11-27): every automated test the inventory names — per control and in the control × scope matrix — exists as a test method or test
    /// class in the frontend tests or the isolated browser journeys. An empty matrix cell is allowed (a reported gap); a name that is not a test is not.
    /// </summary>
    [Fact]
    public void Every_test_the_inventory_maps_a_control_and_scope_to_exists()
    {
        var root = FindRepositoryRoot();
        using var inventory = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "AIAssisted", "frontend", "BirkNext.Web.Tests", "TestOverview", "interaction-inventory.json")));
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (var control in inventory.RootElement.GetProperty("controls").EnumerateArray())
            named.UnionWith(control.GetProperty("automatedTestIds").EnumerateArray().Select(e => e.GetString()!));
        if (inventory.RootElement.TryGetProperty("controlScopeCoverage", out var matrix))
            foreach (var control in matrix.EnumerateObject())
                foreach (var scope in control.Value.EnumerateObject())
                {
                    Assert.Matches("^S\\d{2}$", scope.Name);
                    named.UnionWith(scope.Value.EnumerateArray().Select(e => e.GetString()!));
                }
        var sources = new[] { Path.Combine(root, "AIAssisted", "frontend", "BirkNext.Web.Tests"), Path.Combine(root, "AIAssisted", "backend", "BirkNext.Api.Tests", "BrowserVerification") }
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Select(File.ReadAllText).ToList();
        var missing = named.Where(n => !sources.Any(text => Regex.IsMatch(text, $@"\b(void|Task|class)\s+{Regex.Escape(n)}\b"))).Order(StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0, "interaction-inventory.json names tests that do not exist: " + string.Join(", ", missing));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "azure-pipelines.yml"))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate the BirkNext repository root for the Testoversikt interaction inventory.");
    }
}
