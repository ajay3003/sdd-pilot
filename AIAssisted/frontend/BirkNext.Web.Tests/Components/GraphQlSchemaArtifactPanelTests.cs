using System.Text;
using BirkNext.ApiReview;
using BirkNext.Web.Components;
using BirkNext.Web.Services;
using BirkNext.Web.Tests.Integration;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BirkNext.Web.Tests.Components;

/// <summary>Configure / replace / remove a trusted GraphQL schema artifact per target; validity and errors are announced and linked.</summary>
public sealed class GraphQlSchemaArtifactPanelTests : BunitContext
{
    private readonly FakeGraphQlSchemaArtifactApi _api = new();
    private int _changed;

    private static readonly ApiReviewTarget TargetA = new() { TargetId = "gql-a", EnvironmentId = "dev", ApiType = ApiReviewTargetType.GraphQl, Host = "api-dev.example.test", BasePath = "/a/graphql", ServiceName = "Autorisasjon" };
    private static readonly ApiReviewTarget TargetB = TargetA with { TargetId = "gql-b", BasePath = "/b/graphql", ServiceName = "Barn" };

    public GraphQlSchemaArtifactPanelTests()
    {
        Services.AddSingleton<IGraphQlSchemaArtifactApiService>(_api);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<GraphQlSchemaArtifactPanel> Open(IReadOnlyDictionary<string, GraphQlSchemaArtifact>? artifacts = null) =>
        Render<GraphQlSchemaArtifactPanel>(p => p
            .Add(c => c.EnvironmentId, "dev")
            .Add(c => c.Targets, new[] { TargetA, TargetB })
            .Add(c => c.Artifacts, artifacts ?? new Dictionary<string, GraphQlSchemaArtifact>())
            .Add(c => c.Changed, () => _changed++));

    private static InputFileContent File(string name, string content) => InputFileContent.CreateFromText(content, name);

    [Fact]
    public void EachTargetHasItsOwnRow_WithALabelledFileControl()
    {
        var cut = Open();
        var rows = cut.FindAll("[data-testid=gsa-target]");
        rows.Select(r => r.GetAttribute("data-target-id")).Should().Equal("gql-a", "gql-b");
        rows.Should().OnlyContain(r => r.QuerySelector("label.gsa-file-button input[type=file]") != null, "the file input is named by its label");
        rows[0].QuerySelector("input[type=file]")!.GetAttribute("accept").Should().Be(".graphql,.graphqls,.gql");
    }

    [Fact]
    public void UploadingValidSdl_BindsItToThatTargetAndEnvironment_AndAnnouncesIt()
    {
        var cut = Open();
        var input = cut.FindComponents<Microsoft.AspNetCore.Components.Forms.InputFile>()[0];
        input.UploadFiles(File("m2lb-schema.graphql", "type Query { roles: [Role!]! } type Role { id: ID! }"));

        cut.WaitForAssertion(() => _api.Uploads.Should().ContainSingle());
        var upload = _api.Uploads[0];
        (upload.EnvironmentId, upload.TargetId, upload.FileName).Should().Be(("dev", "gql-a", "m2lb-schema.graphql"));
        _changed.Should().Be(1);
        cut.Find("[data-testid=gsa-status]").TextContent.Should().Contain("m2lb-schema.graphql saved").And.Contain("valid SDL");
        cut.Find("[data-testid=gsa-status]").GetAttribute("role").Should().Be("status");
    }

    [Fact]
    public void ARejectedArtifact_ShowsItsReason_LinkedToTheControl_AndNothingChanges()
    {
        _api.RejectWith = "This is a GraphQL operation document (queries/fragments), not a schema. Upload the server's SDL.";
        var cut = Open();
        cut.FindComponents<Microsoft.AspNetCore.Components.Forms.InputFile>()[0].UploadFiles(File("ops.graphql", "query X { roles { id } }"));

        cut.WaitForAssertion(() => cut.Find("[data-testid=gsa-error]"));
        var error = cut.Find("[data-testid=gsa-error]");
        error.GetAttribute("role").Should().Be("alert");
        error.TextContent.Should().StartWith("Invalid schema artifact:").And.Contain("operation document");
        cut.FindAll("input[type=file]")[0].GetAttribute("aria-describedby").Should().Be(error.Id);
        _changed.Should().Be(0);
    }

    [Fact]
    public void AWrongFileType_IsRejectedBeforeUpload()
    {
        var cut = Open();
        cut.FindComponents<Microsoft.AspNetCore.Components.Forms.InputFile>()[1].UploadFiles(File("schema.json", "{}"));
        cut.WaitForAssertion(() => cut.Find("[data-testid=gsa-error]").TextContent.Should().Contain(".graphql, .graphqls, .gql"));
        _api.Uploads.Should().BeEmpty();
    }

    [Fact]
    public void AConfiguredArtifact_ShowsValidityHashAndOffersReplaceAndRemove()
    {
        var artifact = FakeGraphQlSchemaArtifactApi.Artifact("dev", "gql-a", "a-very-long-schema-file-name-for-the-m2lb-autorisasjon-graphql-api-2026-09-25.graphql");
        _api.Stored[("dev", "gql-a")] = artifact;
        var cut = Open(new Dictionary<string, GraphQlSchemaArtifact> { ["gql-a"] = artifact });
        var row = cut.FindAll("[data-testid=gsa-target]")[0];

        row.GetAttribute("data-configured").Should().Be("true");
        row.QuerySelector("[data-testid=gsa-source]")!.TextContent.Should().Be("Runtime introspection + configured fallback");
        row.QuerySelector("[data-testid=gsa-validity]")!.TextContent.Should().Contain("Valid SDL", "validity is stated in text, not colour alone");
        row.QuerySelector("[data-testid=gsa-hash]")!.TextContent.Should().Be($"sha256 {artifact.ShortHash}");
        row.QuerySelector("label.gsa-file-button")!.TextContent.Trim().Should().Be("Replace");
        var remove = row.QuerySelector("[data-testid=gsa-remove]")!;
        remove.GetAttribute("aria-label").Should().StartWith("Remove schema artifact a-very-long-schema-file-name");
        cut.FindAll("[data-testid=gsa-target]")[1].QuerySelector("[data-testid=gsa-remove]").Should().BeNull();

        remove.Click();
        cut.WaitForAssertion(() => _api.Stored.Should().BeEmpty());
        _changed.Should().Be(1);
        cut.Find("[data-testid=gsa-status]").TextContent.Should().Contain("earlier results are unchanged");
    }

    [Fact]
    public void TheSdlIsNeverRenderedBack()
    {
        var artifact = FakeGraphQlSchemaArtifactApi.Artifact("dev", "gql-a", content: "type Query { secretLookingField: String }");
        var cut = Open(new Dictionary<string, GraphQlSchemaArtifact> { ["gql-a"] = artifact });
        cut.Markup.Should().NotContain("secretLookingField");
    }
}
