using BirkNext.Api.Configuration;
using BirkNext.Api.Controllers;
using BirkNext.Api.Models;
using BirkNext.Api.Services.ImplementationTraceability;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BirkNext.Api.Tests.Services;

/// <summary>
/// The workflow uses the canonical feature names with stable step keys (stored decisions keep working); no step requires approval; the
/// Dashboard is not a step; without Azure DevOps, Change Traceability is "not configured" and returns no evidence — never demo evidence.
/// </summary>
public sealed class WorkflowNamingAndChangeTraceabilityTests
{
    [Fact]
    public void Steps_use_canonical_names_with_stable_keys_and_no_dashboard_step()
    {
        var steps = WorkflowDefinitions.AllSteps;

        steps.Select(s => s.Title).Should().NotContain(["Artifact Traceability", "Implementation Review", "Dashboard"]);
        var traceability = WorkflowDefinitions.GetDefinition("ArtifactTraceability")!;
        traceability.Title.Should().Be("Requirements Traceability");
        traceability.ActionLabel.Should().Be("Open Requirements Traceability");
        traceability.RequiredArtifacts.Should().BeEquivalentTo(["Specification"], "a cross-document analysis reads Constitution, Plan and Tasks when present");
        traceability.OptionalArtifacts.Should().BeEquivalentTo(["Constitution", "Plan", "Tasks"]);

        var alignment = WorkflowDefinitions.GetDefinition("ImplementationReview")!;
        alignment.Title.Should().Be("Task Alignment Review");
        alignment.Description.Should().Contain("does not inspect source code");
        alignment.RequiredArtifacts.Should().BeEquivalentTo(["Specification", "Tasks"], "it needs no Source Analysis");

        steps.Where(s => s.RequiresManualReview).Should().OnlyContain(s => !s.RequiresApproval, "a recorded review decision completes a step");
        steps.Should().NotContain(s => s.StepKey == "Dashboard");
    }

    [Fact]
    public async Task Without_Azure_DevOps_change_traceability_returns_no_evidence_and_says_not_configured()
    {
        var report = await new NotConfiguredImplementationEvidenceProvider().FetchAsync([1001, 1002], null, null);

        report.Tasks.Should().BeEmpty();
        report.UnmappedChanges.Should().BeEmpty();
        report.Source.Should().Be("NotConfigured");
        report.StatusMessage.Should().StartWith("Azure DevOps is not configured for this project.");

        var controller = new ImplementationTraceabilityController(new NotConfiguredImplementationEvidenceProvider(), null!,
            Options.Create(new AzureDevOpsOptions()), NullLogger<ImplementationTraceabilityController>.Instance);
        var status = (ProviderStatusResponse)((OkObjectResult)controller.GetStatus()).Value!;
        (status.Configured, status.UsingMock).Should().Be((false, false));
        status.Message.Should().NotContain("demo").And.Contain("not configured");
    }
}
