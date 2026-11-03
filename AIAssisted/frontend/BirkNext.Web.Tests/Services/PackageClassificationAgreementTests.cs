using BirkNext.Web.Services;
using BirkNext.Web.Models;
using BirkNext.Web.Services.ProjectEvidence;
using BirkNext.Web.Services.ProjectImport;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>One package, one primary classification: Project Evidence's classification follows the same role precedence as the application
/// contexts used by Dashboard, Import and association (infrastructure before documentation), naming carried documents alongside.</summary>
public sealed class PackageClassificationAgreementTests
{
    private static ProjectEvidencePackage Package(params ProjectEvidenceCapability[] capabilities) => new(
        "import-x", "Terraform-iac-m2lb", "x.zip", "sha", DateTimeOffset.UtcNow, null,
        [.. capabilities.Select(c => new PackageCapability(c, PackageCapabilityStatus.Ready, ""))], new Dictionary<WorkspaceArtifactType, int>(), 17, [], [], [], null);

    [Theory]
    [InlineData(new[] { ProjectEvidenceCapability.Infrastructure, ProjectEvidenceCapability.Documentation }, PackageEvidenceRole.Infrastructure, "Infrastructure source + documents")]
    [InlineData(new[] { ProjectEvidenceCapability.Infrastructure, ProjectEvidenceCapability.ContractSchema }, PackageEvidenceRole.Infrastructure, "Infrastructure source")]
    [InlineData(new[] { ProjectEvidenceCapability.Documentation }, PackageEvidenceRole.Documentation, "Documentation source")]
    [InlineData(new[] { ProjectEvidenceCapability.GeneratedDocumentation, ProjectEvidenceCapability.ContractSchema }, PackageEvidenceRole.GeneratedDocumentation, "Generated documentation")]
    [InlineData(new[] { ProjectEvidenceCapability.ApplicationSource, ProjectEvidenceCapability.Documentation }, PackageEvidenceRole.PrimaryApplicationSource, "Source + documents")]
    public void Classification_and_role_agree(ProjectEvidenceCapability[] capabilities, PackageEvidenceRole role, string classification)
    {
        var package = Package(capabilities);

        ApplicationContexts.RoleOf(package).Should().Be(role);
        package.Classification.Should().Be(classification);
    }
}
