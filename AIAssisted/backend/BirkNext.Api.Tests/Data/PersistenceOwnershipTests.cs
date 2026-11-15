using BirkNext.Api.Services.LocalDataReset;
using BirkNext.Api.Services.WorkspaceTransfer;
using BirkNext.Api.Tests.Utilities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BirkNext.Api.Tests.Data;

/// <summary>
/// Audit 2026-11-15 ARCH-01: the lists that must name every persistence context are kept in step. Every DbContext the API registers has a
/// migration inventory entry for backup/restore compatibility and a reset owner; the Critical E2E file store is backed up and reset as a whole.
/// Before this test, three contexts (Scope Registry, Task Alignment, Integration Journeys) were missing from the backup's migration inventory,
/// <c>flow-versions.json</c> was missing from the backup, and suggestion reviews survived a reset that removed the flows they review.
/// </summary>
public sealed class PersistenceOwnershipTests
{
    /// <summary>Every DbContext type of the API that the host registers (resolvable from a scope).</summary>
    private static List<Type> Contexts(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        return [.. typeof(Program).Assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract && typeof(DbContext).IsAssignableFrom(t))
            .Where(t => scope.ServiceProvider.GetService(t) is not null)];
    }
    [Fact]
    public void Every_registered_context_is_in_the_backup_migration_inventory()
    {
        using var factory = TestHostConfiguration.CreateDefaultHostWithEnginesDisabled();
        var contexts = Contexts(factory);
        contexts.Should().HaveCountGreaterThanOrEqualTo(7, "App, Delivery Reporting, Canonical interpretation, Scope Registry, Task Alignment, Snapshot Registry, Integration Journeys");
        CodeMigrations.Contexts.Should().BeEquivalentTo(contexts, "the backup compares the migrations of every schema it dumps");
        var inventory = CodeMigrations.Read(factory.Services.GetRequiredService<IServiceScopeFactory>());
        inventory.Should().HaveCount(contexts.Count, "one migration history per context");
        inventory.Values.Should().OnlyContain(m => m.Count > 0, "every context has migrations");
    }

    [Fact]
    public void Every_registered_context_has_exactly_one_reset_owner()
    {
        using var factory = TestHostConfiguration.CreateDefaultHostWithEnginesDisabled();
        var owners = LocalDataResetCoordinator.DedicatedResetContexts.Concat(ResettableSchemas.All.Select(s => s.ContextType)).ToList();
        owners.Should().OnlyHaveUniqueItems("a schema is cleared by one owner");
        owners.Should().BeEquivalentTo(Contexts(factory), "a context without a reset owner would keep a previous project's data after Reset");
    }

    [Fact]
    public void The_critical_e2e_store_is_backed_up_whole()
    {
        var stores = new ManagedFileStores(Path.GetTempPath(), Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "reset.json"));
        stores.Stores.Single(s => s.Name == "critical-e2e").Files.Should().BeEquivalentTo(
            ["flows.json", "history.json", "modules.json", "flow-versions.json", "suggestion-reviews.json"],
            "flow versions decide which definition a run executed (Policy 2 provenance); a restore without them loses that");
    }

    [Fact]
    public void Suggestion_reviews_are_cleared_with_the_flows_they_review()
    {
        var directory = Directory.CreateTempSubdirectory("birknext-suggestions-").FullName;
        try
        {
            var store = new BirkNext.Api.Services.CriticalE2E.Suggestions.CriticalE2ESuggestionStore(directory);
            store.Update("s-1", Guid.NewGuid(), r => r);
            store.All().Should().ContainSingle();
            store.Clear();
            store.All().Should().BeEmpty();
            new BirkNext.Api.Services.CriticalE2E.Suggestions.CriticalE2ESuggestionStore(directory).All().Should().BeEmpty("the cleared state is persisted");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
