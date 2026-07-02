using BirkNext.Api.Models.Admin;
using FluentAssertions;
using System.Text.Json;

namespace BirkNext.Api.Tests.Unit;

public class EnvironmentDiagnosticsSerializationTests
{
    [Fact]
    public void StatusEnum_SerializesToString_NotNumeric()
    {
        // Arrange
        var check = new EnvironmentDiagnosticCheck
        {
            Name = "Test Check",
            Status = EnvironmentDiagnosticStatus.Pass,
            Details = "Test details",
            Recommendation = "Test recommendation"
        };

        // Act
        var json = JsonSerializer.Serialize(check);

        // Assert
        json.Should().Contain("\"status\":\"Pass\"");
        json.Should().NotContain("\"status\":0");
    }

    [Theory]
    [InlineData(EnvironmentDiagnosticStatus.Pass, "Pass")]
    [InlineData(EnvironmentDiagnosticStatus.Warning, "Warning")]
    [InlineData(EnvironmentDiagnosticStatus.Fail, "Fail")]
    [InlineData(EnvironmentDiagnosticStatus.NotAvailable, "NotAvailable")]
    public void AllStatusValues_SerializeCorrectly(EnvironmentDiagnosticStatus status, string expectedValue)
    {
        // Arrange
        var check = new EnvironmentDiagnosticCheck
        {
            Name = "Test",
            Status = status,
            Details = "",
            Recommendation = ""
        };

        // Act
        var json = JsonSerializer.Serialize(check);

        // Assert
        json.Should().Contain($"\"status\":\"{expectedValue}\"");
    }

    [Fact]
    public void EnvironmentDiagnosticsReport_SerializesStatusAsString()
    {
        // Arrange
        var report = new EnvironmentDiagnosticsReport
        {
            Environment = "Development",
            GeneratedAt = DateTime.UtcNow
        };

        report.DatabaseChecks.Add(new EnvironmentDiagnosticCheck
        {
            Name = "Database Reachable",
            Status = EnvironmentDiagnosticStatus.Pass,
            Details = "Connected successfully",
            Recommendation = ""
        });

        // Act
        var json = JsonSerializer.Serialize(report);
        var doc = JsonDocument.Parse(json);
        var statusElement = doc.RootElement.GetProperty("databaseChecks")[0].GetProperty("status");

        // Assert
        statusElement.ValueKind.Should().Be(JsonValueKind.String);
        statusElement.GetString().Should().Be("Pass");
    }

    [Fact]
    public void DeserializeFromFrontend_CanHandleStringStatus()
    {
        // Arrange
        var jsonString = @"{
            ""generatedAt"": ""2026-01-01T00:00:00Z"",
            ""environment"": ""Development"",
            ""databaseChecks"": [
                {
                    ""name"": ""Database Reachable"",
                    ""status"": ""Pass"",
                    ""details"": ""Connected"",
                    ""recommendation"": """"
                }
            ],
            ""backendApiChecks"": [],
            ""workspaceChecks"": [],
            ""reviewContextChecks"": [],
            ""exportChecks"": [],
            ""overallStatus"": ""Pass""
        }";

        // Act
        var report = JsonSerializer.Deserialize<EnvironmentDiagnosticsReport>(jsonString);

        // Assert
        report.Should().NotBeNull();
        report!.DatabaseChecks.Should().HaveCount(1);
        report.DatabaseChecks[0].Status.Should().Be(EnvironmentDiagnosticStatus.Pass);
    }
}
