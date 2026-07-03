using BirkNext.Api.Models.Admin;
using BirkNext.Api.Tests.Extensions;
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
            Status = SystemSettingsStatus.Pass,
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
    [InlineData(SystemSettingsStatus.Pass, "Pass")]
    [InlineData(SystemSettingsStatus.Warning, "Warning")]
    [InlineData(SystemSettingsStatus.Fail, "Fail")]
    [InlineData(SystemSettingsStatus.Unavailable, "Unavailable")]
    public void AllStatusValues_SerializeCorrectly(SystemSettingsStatus status, string expectedValue)
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
            GeneratedAt = DateTime.UtcNow,
            Sections = new()
            {
                new SettingsSection
                {
                    Title = "Database",
                    Items = new()
                    {
                        new SettingsItem
                        {
                            Name = "Database Reachable",
                            Status = SystemSettingsStatus.Pass,
                            Description = "Connected successfully",
                            Recommendation = ""
                        }
                    }
                }
            }
        };

        // Act
        var json = JsonSerializer.Serialize(report);
        var doc = JsonDocument.Parse(json);
        var statusElement = doc.RootElement.GetProperty("sections")[0].GetProperty("items")[0].GetProperty("status");

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
            ""overallStatus"": ""Pass"",
            ""sections"": [
                {
                    ""title"": ""Database"",
                    ""description"": ""Database checks"",
                    ""status"": ""Pass"",
                    ""items"": [
                        {
                            ""name"": ""Database Reachable"",
                            ""value"": """",
                            ""status"": ""Pass"",
                            ""description"": ""Connected"",
                            ""recommendation"": """",
                            ""isRequired"": true
                        }
                    ],
                    ""isRequired"": true
                }
            ]
        }";

        // Act
        var report = JsonSerializer.Deserialize<EnvironmentDiagnosticsReport>(jsonString);

        // Assert
        report.Should().NotBeNull();
        report!.Sections.Should().HaveCount(1);
        report.Sections[0].Items.Should().HaveCount(1);
        report.Sections[0].Items[0].Status.Should().Be(SystemSettingsStatus.Pass);
    }
}
