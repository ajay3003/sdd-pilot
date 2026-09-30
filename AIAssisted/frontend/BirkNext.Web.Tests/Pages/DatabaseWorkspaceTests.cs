using BirkNext.DatabaseArchitecture;
using BirkNext.Web.Components;
using Bunit;
using FluentAssertions;

namespace BirkNext.Web.Tests.Pages;

public sealed class DatabaseWorkspaceTests : BunitContext
{
    private static DatabaseArchitectureSnapshot Model() => new() { SourceFingerprint = "generic-fingerprint", Status = DatabaseAnalysisStatus.Partial, Databases = [new() { Id = "db", LogicalName = "Generic candidate", Schemas = [new() { Name = "public", Tables = [new() { Id = "a", LogicalName = "Customer", Columns = [new() { Name = "Id", IsPrimaryKey = true }, new() { Name = "Name" }] }, new() { Id = "b", LogicalName = "Purchase", Columns = [new() { Name = "CustomerId", IsForeignKey = true }], Relationships = [new() { FromTable = "b", ToTable = "a", FromColumns = ["CustomerId"], ToColumns = ["Id"], Cardinality = "N:1", EvidenceState = DatabaseEvidenceState.Inferred }] }] }] }] };
    [Fact] public void OverviewExplainsSourceRuntimeBoundaryAndCounts()
    {
        var cut = Render<DatabaseWorkspace>(p => p.Add(c => c.Snapshot, Model())); cut.Markup.Should().Contain("does not prove").And.Contain("generic-fingerprint").And.Contain("Inferred relationships").And.NotContain("Pass");
    }
    [Fact] public void TablesAreSearchableAndOpenAccessibleDetails()
    {
        var cut = Render<DatabaseWorkspace>(p => p.Add(c => c.Snapshot, Model())); cut.FindAll("nav button").Single(b => b.TextContent == "Tables").Click(); cut.Find("input[type=search]").Input("CustomerId"); cut.FindAll("tbody tr").Should().ContainSingle(); cut.Find("tbody button").Click(); cut.Find("aside").TextContent.Should().Contain("Purchase").And.Contain("Columns").And.Contain("Source evidence");
    }
    [Fact] public void RelationshipAlternativeShowsInferredState()
    {
        var cut = Render<DatabaseWorkspace>(p => p.Add(c => c.Snapshot, Model())); cut.FindAll("nav button").Single(b => b.TextContent == "Relationships").Click(); cut.Find("tbody").TextContent.Should().Contain("Inferred").And.Contain("N:1").And.Contain("CustomerId");
    }
    [Fact] public void ChangesRequirePreviousSnapshotAndNeverClaimDrift()
    {
        var cut = Render<DatabaseWorkspace>(p => p.Add(c => c.Snapshot, Model())); cut.FindAll("nav button").Single(b => b.TextContent == "Changes").Click(); cut.Markup.Should().Contain("Source schema change").And.Contain("previous database analysis snapshot is required").And.NotContain("deployed DB drift");
    }
}
