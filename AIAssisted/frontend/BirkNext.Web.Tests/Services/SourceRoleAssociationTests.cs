using BirkNext.Web.Models;
using BirkNext.Web.Services;
using BirkNext.Web.Services.ProjectEvidence;
using BirkNext.Web.Services.ProjectImport;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Source roles and association suggestions: a role confirmed in Source Management is the canonical package role every consumer reads (it never
/// adds a capability); suggestions are explained, classed (strong / possible) and never applied automatically; names alone are weak.
/// </summary>
public sealed class SourceRoleAssociationTests
{
    private static SddLifecycleState Lifecycle(params (string Id, string Name, string App, string[] Confirmed)[] imports)
    {
        var lifecycle = new SddLifecycleState();
        foreach (var app in imports.Select(i => i.App).Distinct()) lifecycle.Applications.Add(new SddApplicationContext { Id = app, Name = app });
        foreach (var (id, name, app, confirmed) in imports)
            lifecycle.ProjectImports.Add(new SddProjectImportRecord { ImportId = id, ProjectName = name, ArchiveFileName = name + ".zip", ApplicationId = app, ConfirmedRoles = [.. confirmed], ImportedAt = DateTimeOffset.UtcNow });
        return lifecycle;
    }

    [Fact]
    public void A_role_confirmed_in_Source_Management_is_the_canonical_role_and_a_confirmed_primary_is_never_demoted()
    {
        var lifecycle = Lifecycle(("m", "M2LB", "app", []), ("l", "M2LB-language", "app", [nameof(PackageEvidenceRole.PrimaryApplicationSource), nameof(PackageEvidenceRole.Documentation)]),
            ("t", "Terraform-iac-m2lb", "app", [nameof(PackageEvidenceRole.Infrastructure)]));

        var packages = ApplicationContexts.Packages(lifecycle, "app", setup: null);

        packages.Single(p => p.ImportId == "l").Should().Match<ApplicationPackage>(p => p.Role == PackageEvidenceRole.PrimaryApplicationSource && p.RoleConfirmed,
            "several confirmed roles: the association role is the first in role order");
        packages.Single(p => p.ImportId == "t").Should().Match<ApplicationPackage>(p => p.Role == PackageEvidenceRole.Infrastructure && p.RoleConfirmed);
        packages.Single(p => p.ImportId == "m").RoleConfirmed.Should().BeFalse("without a confirmation the role is detected from content (not classified without source setup)");
        ApplicationContexts.ConfirmedRole(new SddProjectImportRecord { ConfirmedRoles = ["Unknown", nameof(PackageEvidenceRole.NotClassified)] }).Should().BeNull();
    }

    private static ProjectEvidencePackage Package(string id, string name, params PackageCapability[] capabilities) =>
        new(id, name, name + ".zip", "sha-" + id, DateTimeOffset.UtcNow, null, capabilities, new Dictionary<WorkspaceArtifactType, int>(), 0, [], [], [], null);

    [Fact]
    public void Suggestions_are_strong_only_with_explicit_or_reviewed_evidence_and_names_are_only_possible_matches()
    {
        var identity = new TargetApplicationIdentity { Name = "M2LB", ImportId = "own" };

        ProjectEvidenceAssociation.SuggestionFor(Package("own", "Anything"), identity, noApplication: false, null)!.Value.Strength.Should().Be(AssociationSuggestion.Strong);
        ProjectEvidenceAssociation.SuggestionFor(Package("x", "Other"), identity, noApplication: false, new HashSet<string> { "x" })!.Value
            .Should().Match<(AssociationSuggestion Strength, string Reason)>(s => s.Strength == AssociationSuggestion.Strong && s.Reason.Contains("another Target Environment"));
        ProjectEvidenceAssociation.SuggestionFor(Package("l", "M2LB-language"), identity, noApplication: true, null)!.Value.Strength.Should().Be(AssociationSuggestion.Possible);
        ProjectEvidenceAssociation.SuggestionFor(Package("c", "M2LB.Common"), identity, noApplication: true, null)!.Value.Reason.Should().Contain("names alone are weak evidence");
        ProjectEvidenceAssociation.SuggestionFor(Package("l", "M2LB-language"), identity, noApplication: false, null)
            .Should().BeNull("when an application holds the evidence, a similar name in another application is not a suggestion");
        ProjectEvidenceAssociation.SuggestionFor(Package("t", "Tiltak 2 - Sikkert digitalt økosystem"), identity, noApplication: true, null).Should().BeNull("unrelated");
    }

    [Theory]
    [InlineData(PackageEvidenceRole.PrimaryApplicationSource, ProjectEvidenceCapability.ApplicationSource)]
    [InlineData(PackageEvidenceRole.RelatedApplicationSource, ProjectEvidenceCapability.IntegrationSource)]
    [InlineData(PackageEvidenceRole.SupportingSource, ProjectEvidenceCapability.SupportingSource)]
    [InlineData(PackageEvidenceRole.Documentation, ProjectEvidenceCapability.Documentation)]
    [InlineData(PackageEvidenceRole.Infrastructure, ProjectEvidenceCapability.Infrastructure)]
    [InlineData(PackageEvidenceRole.GeneratedDocumentation, ProjectEvidenceCapability.GeneratedDocumentation)]
    [InlineData(PackageEvidenceRole.Contracts, ProjectEvidenceCapability.ContractSchema)]
    public void One_vocabulary_maps_each_source_role_to_its_evidence_category(PackageEvidenceRole role, ProjectEvidenceCapability category) =>
        SourceRoleVocabulary.CategoryOf(role).Should().Be(category);

    [Fact]
    public void Only_roles_the_content_supports_are_offered()
    {
        var infrastructure = Package("t", "Terraform", new PackageCapability(ProjectEvidenceCapability.Infrastructure, PackageCapabilityStatus.Ready, "main.tf"));
        SourceRoleVocabulary.Supported(infrastructure).Should().Equal(PackageEvidenceRole.Infrastructure);
        SourceRoleVocabulary.Choices.Should().NotContain(PackageEvidenceRole.NotClassified);
    }
}
