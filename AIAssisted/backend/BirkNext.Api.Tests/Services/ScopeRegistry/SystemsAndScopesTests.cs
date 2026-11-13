using System.Text.Json;
using BirkNext.Api.Data.CanonicalInterpretation;
using BirkNext.Api.Data.ScopeRegistry;
using BirkNext.Api.Services.CanonicalInterpretation;
using BirkNext.Api.Services.ScopeRegistry;
using BirkNext.Integrations;
using BirkNext.IntegrationTopology;
using BirkNext.Interpretation;
using BirkNext.Scopes;
using BirkNext.Snapshots;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Tests.Services.ScopeRegistry;

/// <summary>
/// Systems & Scopes on a synthetic M2LB-shaped interpretation: systems declared by a domain profile (Altinn, MU, Person, Skoletjenesten), services
/// from source (M2LB.Person.Api, M2LB.SkoleService.Api, M2LB.PersonBiRKAdapter.Worker) and one messaging channel. Nothing is grouped or connected
/// by name similarity; Altinn gets no invented connection; runtime is never inferred; scopes accept only ids their catalog confirms.
/// </summary>
public sealed class SystemsAndScopesTests
{
    const string Scope = "env-m2lb";

    sealed class NoSources : ICanonicalSourceReader
    {
        public Task<IqrSourceSnapshot?> FindAsync(Guid snapshotId, CancellationToken ct) => Task.FromResult<IqrSourceSnapshot?>(null);
    }

    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    sealed class Fixture
    {
        public required CanonicalInterpretationDbContext Db { get; init; }
        public required CanonicalInterpretationService Canonical { get; init; }
        public Guid Latest { get; init; }
        readonly Dictionary<string, Guid> _ids = [];

        public Guid Entity(string type, string identity, string name, bool inLatest = true)
        {
            var id = Guid.NewGuid();
            Db.Entities.Add(new CanonicalEntityRecord { Id = id, Scope = Scope, EntityType = type, IdentityKey = identity, DisplayName = name, SourceIdentity = name,
                ResolutionMethod = type == "System" ? "declared system name" : "messaging endpoint", AttributesJson = type == "MessagingChannel" ? "{\"kind\":\"topic\"}" : "{}" });
            if (inLatest) Db.SnapshotEntities.Add(new InterpretationSnapshotEntityRecord { SnapshotId = Latest, EntityId = id, DisplayName = name });
            _ids[$"{type}|{identity}"] = id;
            return id;
        }

        public void Alias(string entityKey, string alias, string state) => Db.Aliases.Add(new CanonicalAliasRecord
        {
            Id = Guid.NewGuid(), Scope = Scope, EntityId = _ids[entityKey], Alias = alias, NormalizedAlias = CanonicalKeys.Normalize(alias), State = state,
            Source = "domain profile skolenaervaer", RuleId = "CR-K",
        });

        public void Relation(string from, string type, string to, string resolution, string review = "NotReviewed")
        {
            var id = Guid.NewGuid();
            Db.Relations.Add(new CanonicalRelationRecord
            {
                Id = id, Scope = Scope, SourceEntityId = _ids[from], TargetEntityId = _ids[to], RelationType = type, RelationKey = $"{from}->{type}->{to}",
                Resolution = resolution, Confidence = "Explicit", RuleId = "CR-N", RuleSetVersion = "test", ReviewState = review, Active = true,
                EvidenceJson = JsonSerializer.Serialize(new[] { new CorrelationEvidence("channel", "src/Program.cs", "person.barn") }, Web), Explanation = $"{from} {type} {to}.",
            });
            Db.SnapshotRelations.Add(new InterpretationSnapshotRelationRecord { SnapshotId = Latest, RelationId = id, Resolution = resolution, ReviewState = review, Confidence = "Explicit", RuleId = "CR-N" });
        }
    }

    static async Task<Fixture> M2lbLike()
    {
        var db = new CanonicalInterpretationDbContext(new DbContextOptionsBuilder<CanonicalInterpretationDbContext>().UseInMemoryDatabase($"sys-{Guid.NewGuid():N}").Options);
        var latest = Guid.NewGuid();
        db.Snapshots.Add(new InterpretationSnapshotRecord { Id = latest, Scope = Scope, CreatedAt = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero),
            SourceSnapshotsJson = JsonSerializer.Serialize(new[] { new SnapshotReference(SnapshotType.SourceAnalysis, Guid.NewGuid().ToString(), "M2LB-language.zip") }, Web) });
        var f = new Fixture { Db = db, Canonical = new CanonicalInterpretationService(db, new NoSources(), TimeProvider.System), Latest = latest };
        f.Entity("System", "altinn", "Altinn");
        f.Entity("System", "mu", "MU");
        f.Entity("System", "person", "Person");
        f.Entity("System", "skoletjenesten", "Skoletjenesten");
        f.Entity("Service", "m2lb.person.api", "M2LB.Person.Api");
        f.Entity("Service", "m2lb.personbirkadapter.worker", "M2LB.PersonBiRKAdapter.Worker");
        f.Entity("Service", "m2lb.skoleservice.api", "M2LB.SkoleService.Api");
        f.Entity("Service", "old.removed.api", "Old.Removed.Api", inLatest: false);
        f.Entity("MessagingChannel", "topic:person.barn", "person.barn");
        f.Entity("Requirement", "skole/001/fr-001", "FR-001: Receive the attendance report");
        f.Entity("Scenario", "system-understanding/skole/s1", "Valid report is accepted");
        f.Alias("System|person", "M2LB.Person.Api", "Explicit");
        f.Alias("System|skoletjenesten", "M2LB.SkoleService.Api", "Explicit");
        f.Alias("System|mu", "Meldingsutveksleren", "Proposed");
        f.Relation("Service|m2lb.person.api", "Produces", "MessagingChannel|topic:person.barn", "Explicit");
        f.Relation("Service|m2lb.skoleservice.api", "Consumes", "MessagingChannel|topic:person.barn", "Explicit");
        f.Relation("Service|m2lb.personbirkadapter.worker", "Consumes", "MessagingChannel|topic:person.barn", "Candidate");
        f.Relation("Service|m2lb.person.api", "DependsOn", "Service|m2lb.skoleservice.api", "Candidate", review: "Rejected");
        await db.SaveChangesAsync();
        return f;
    }

    [Fact]
    public async Task Overview_lists_the_latest_systems_groups_services_only_by_declared_alias_and_invents_no_connection()
    {
        var f = await M2lbLike();
        var view = await f.Canonical.SystemsOverviewAsync(Scope, default);

        view.InterpretationId.Should().Be(f.Latest);
        view.SourceLabels.Should().Equal("M2LB-language.zip");
        view.Systems.Select(s => s.Name).Should().NotContain("Old.Removed.Api", "an entity an older interpretation left behind is not a current system")
            .And.Contain(["Altinn", "M2LB.Person.Api", "M2LB.PersonBiRKAdapter.Worker"]);
        view.Systems.Single(s => s.Name == "Person").ServiceKeys.Should().Equal("Service|m2lb.person.api");
        view.Systems.Single(s => s.Name == "M2LB.Person.Api").ParentBasis.Should().Contain("declares \"M2LB.Person.Api\"");
        view.Systems.Single(s => s.Name == "M2LB.PersonBiRKAdapter.Worker").ParentKey.Should().BeNull("a similar name is never membership");

        var altinn = view.Systems.Single(s => s.Name == "Altinn");
        (altinn.Outgoing + altinn.Incoming).Should().Be(0);
        view.Connections.Should().NotContain(c => c.SourceName == "Altinn" || c.TargetName == "Altinn", "no evidence connects Altinn here");

        var message = view.Connections.Single(c => c.SourceName == "M2LB.Person.Api" && c.TargetName == "M2LB.SkoleService.Api" && c.Kind == "Messaging");
        message.State.Should().Be(SystemConnectionState.FromSourceEvidence);
        message.Label.Should().Be("sends messages via person.barn");
        message.RelationKeys.Should().HaveCount(2);
        message.Unverified.Should().Contain("no message has been observed");
        view.Connections.Single(c => c.TargetName == "M2LB.PersonBiRKAdapter.Worker").State.Should().Be(SystemConnectionState.Candidate, "the weaker end decides");
        view.Connections.Single(c => c.Kind == "DependsOn").State.Should().Be(SystemConnectionState.Rejected);
        view.Connections.Should().NotContain(c => c.State == SystemConnectionState.ObservedAtRuntime, "runtime is never inferred from source");
        view.IdentityQuestions.Should().ContainSingle(q => q.TargetKind == ReviewTargetKind.Alias && q.Question.Contains("Meldingsutveksleren"));
    }

    [Fact]
    public async Task Without_an_interpretation_the_overview_and_catalog_say_so_instead_of_an_empty_list()
    {
        var db = new CanonicalInterpretationDbContext(new DbContextOptionsBuilder<CanonicalInterpretationDbContext>().UseInMemoryDatabase($"sys-{Guid.NewGuid():N}").Options);
        var canonical = new CanonicalInterpretationService(db, new NoSources(), TimeProvider.System);
        var view = await canonical.SystemsOverviewAsync(Scope, default);
        view.InterpretationId.Should().BeNull();
        view.Limitations.Should().ContainSingle().Which.Should().Contain("No interpretation");

        var page = await new ScopeMemberCatalogService(canonical).SearchAsync(ScopeMemberKind.System, Scope, null, 0, 25, default);
        page.Items.Should().BeEmpty();
        page.Notices.Should().ContainSingle().Which.Should().Contain("No interpretation exists");
        (await new ScopeMemberCatalogService(canonical).SearchAsync(ScopeMemberKind.Requirement, null, null, 0, 25, default)).Notices.Single().Should().Contain("active Target Environment");
    }

    [Fact]
    public void Topology_connections_keep_their_own_evidence_state_and_never_turn_an_expectation_into_a_connection()
    {
        var view = new SystemsOverviewView
        {
            Scope = Scope,
            Systems = [new SystemOverviewItem { Key = "System|altinn", Name = "Altinn", Type = CanonicalEntityType.System },
                new SystemOverviewItem { Key = "System|mu", Name = "MU", Type = CanonicalEntityType.System, Aliases = [new("Meldingsutveksleren", AliasState.Explicit, "profile")] }],
            Sources = ["Canonical interpretation: used"],
        };
        var topo = new IntegrationTopologyView
        {
            Components = [new TopologyComponent { Id = "c1", Name = "Altinn", Location = "https://altinn.example.test" }, new TopologyComponent { Id = "c2", Name = "Meldingsutveksleren" },
                new TopologyComponent { Id = "c3", Name = "MM" }],
            Connections =
            [
                new TopologyConnection { Id = "k1", ProducerComponentId = "c1", ConsumerComponentId = "c2", Type = TopologyInterfaceType.ExternalApplication, Relationship = TopologyRelationshipState.Candidate, Unverified = "Not observed." },
                new TopologyConnection { Id = "k2", ProducerComponentId = "c2", ConsumerComponentId = "c3", Type = TopologyInterfaceType.Messaging, Relationship = TopologyRelationshipState.ExpectedUnresolved },
                new TopologyConnection { Id = "k3", ProducerComponentId = "c2", ConsumerComponentId = "c3", Type = TopologyInterfaceType.Rest, Relationship = TopologyRelationshipState.Resolved },
            ],
            Sources = [new(TopologySource.SourceAnalysis, TopologySourceState.Used, "")],
        };
        var merged = SystemsOverviewService.Merge(view, topo);

        merged.Connections.Should().HaveCount(2, "an expected-but-unresolved relationship is not a connection");
        var altinnMu = merged.Connections.Single(c => c.Id == "topology:k1");
        (altinnMu.SourceKey, altinnMu.TargetKey, altinnMu.State, altinnMu.Origin).Should().Be(("System|altinn", "System|mu", SystemConnectionState.Candidate, "Integration Topology"));
        var muMm = merged.Connections.Single(c => c.Id == "topology:k3");
        (muMm.TargetKey, muMm.TargetName, muMm.State).Should().Be(("topology:c3", "MM", SystemConnectionState.FromSourceEvidence), "an unmatched component keeps its own name and is no canonical system");
        merged.Systems.Single(s => s.Name == "Altinn").ConfiguredLocation.Should().Be("https://altinn.example.test");
        merged.Connections.Should().NotContain(c => c.State == SystemConnectionState.ObservedAtRuntime);
    }

    [Fact]
    public async Task Catalogs_search_the_latest_interpretation_and_refuse_kinds_no_catalog_can_confirm()
    {
        var f = await M2lbLike();
        var catalog = new ScopeMemberCatalogService(f.Canonical);
        var systems = await catalog.SearchAsync(ScopeMemberKind.System, Scope, "person", 0, 25, default);
        systems.Items.Select(i => i.Label).Should().Equal("M2LB.Person.Api", "M2LB.PersonBiRKAdapter.Worker", "Person");
        systems.Items.Should().OnlyContain(i => i.CatalogScope == Scope && i.SourceModule == ScopeMemberCatalogService.CanonicalModule);
        (await catalog.SearchAsync(ScopeMemberKind.System, Scope, "removed", 0, 25, default)).Total.Should().Be(0);
        var requirements = await catalog.SearchAsync(ScopeMemberKind.Requirement, Scope, "report", 0, 25, default);
        requirements.Items.Single().TargetId.Should().Be("Requirement|skole/001/fr-001");
        var testCases = await catalog.SearchAsync(ScopeMemberKind.TestCase, Scope, null, 0, 25, default);
        testCases.Available.Should().BeFalse();
        testCases.Notices.Single().Should().Contain("cannot be added");
    }

    static ScopeRegistryService Registry(IScopeMemberResolver resolver) => new(new ScopeRegistryDbContext(new DbContextOptionsBuilder<ScopeRegistryDbContext>()
        .UseInMemoryDatabase($"scopes-{Guid.NewGuid():N}").Options), TimeProvider.System, resolver);

    static ScopeMember M(ScopeMemberKind kind, string id, string? catalogScope = null) => new() { Kind = kind, TargetId = id, Label = id, CatalogScope = catalogScope };

    [Fact]
    public async Task A_scope_can_be_created_from_a_requirement_or_scenario_without_any_system_and_records_the_catalog_context()
    {
        var f = await M2lbLike();
        var registry = Registry(new ScopeMemberCatalogService(f.Canonical));
        var created = await registry.CreateAsync(new CreateScopeRequest("import:m2lb", "Report intake", null, ScopePurpose.Test, null,
            [M(ScopeMemberKind.Requirement, "Requirement|skole/001/fr-001"), M(ScopeMemberKind.Scenario, "Scenario|system-understanding/skole/s1")], Scope), "tester");

        created.Error.Should().BeNull();
        created.Scope!.Revisions.Single().Members.Should().OnlyContain(m => m.CatalogScope == Scope);
        created.Scope.Scope.MembersByKind.Should().NotContainKey(ScopeMemberKind.System);

        var empty = await registry.CreateAsync(new CreateScopeRequest("import:m2lb", "Later", "Members come later", ScopePurpose.Test, null, [], Scope), "tester");
        empty.Error.Should().BeNull("an empty draft is a valid draft");
        var approve = await registry.ApproveAsync("import:m2lb", empty.Scope!.Scope.Id, new(empty.Scope.Scope.Version, 1, null), "lead");
        approve.Error.Should().Contain("empty scope cannot be approved");
    }

    [Fact]
    public async Task Fabricated_wrong_kind_and_other_environment_ids_are_refused_and_nothing_is_saved()
    {
        var f = await M2lbLike();
        var registry = Registry(new ScopeMemberCatalogService(f.Canonical));
        async Task<string?> Try(params ScopeMember[] members) =>
            (await registry.CreateAsync(new CreateScopeRequest("import:m2lb", $"S{Guid.NewGuid():N}", null, ScopePurpose.Test, null, [.. members], Scope), "tester")).Error;

        (await Try(M(ScopeMemberKind.System, "System|invented"))).Should().Contain("Not in the latest interpretation").And.Contain("Nothing was saved");
        (await Try(M(ScopeMemberKind.Requirement, "System|altinn"))).Should().Contain("not a requirement");
        (await Try(M(ScopeMemberKind.System, "System|altinn", "env-other"))).Should().Contain("another Target Environment");
        (await Try(M(ScopeMemberKind.TestCase, "TC-1"))).Should().Contain("No catalog can confirm");
        (await registry.ListAsync("import:m2lb")).Should().BeEmpty();
        (await Try(M(ScopeMemberKind.System, "System|altinn"), M(ScopeMemberKind.System, "Service|m2lb.person.api"))).Should().BeNull("one or several systems are fine");
    }

    [Fact]
    public async Task A_member_that_disappeared_from_the_catalog_is_kept_shown_as_missing_and_new_revisions_still_save()
    {
        var f = await M2lbLike();
        var registry = Registry(new ScopeMemberCatalogService(f.Canonical));
        var created = (await registry.CreateAsync(new CreateScopeRequest("import:m2lb", "Person flow", null, ScopePurpose.IntegrationE2E, null,
            [M(ScopeMemberKind.System, "System|person"), M(ScopeMemberKind.System, "Service|m2lb.person.api")], Scope), "tester")).Scope!;
        var approved = (await registry.ApproveAsync("import:m2lb", created.Scope.Id, new(created.Scope.Version, 1, "pinned"), "lead")).Scope!;

        // The service disappears from the latest interpretation (e.g. renamed in source).
        f.Db.SnapshotEntities.RemoveRange(f.Db.SnapshotEntities.Where(m => m.DisplayName == "M2LB.Person.Api"));
        await f.Db.SaveChangesAsync();
        var check = await registry.CheckAsync("import:m2lb", created.Scope.Id, null, Scope);
        check!.Single(c => c.Key == "System:Service|m2lb.person.api").Check.Should().Be(ScopeMemberCheck.Missing);
        check.Single(c => c.Key == "System:System|person").Check.Should().Be(ScopeMemberCheck.Current);

        var edited = await registry.SaveDraftAsync("import:m2lb", created.Scope.Id, new SaveScopeDraftRequest(approved.Scope.Version, "Person flow", null, ScopePurpose.IntegrationE2E, null,
            [.. approved.Revisions.First().Members, M(ScopeMemberKind.System, "System|skoletjenesten")], "Add Skoletjenesten", Scope), "tester");
        edited.Error.Should().BeNull("a carried-over member is never re-validated away");
        edited.Scope!.Revisions.Should().HaveCount(2);
        edited.Scope.Revisions.Single(r => r.Number == 1).State.Should().Be(ScopeRevisionState.Approved, "the approved revision never changes");
        edited.Scope.Revisions.Single(r => r.Number == 2).Members.Select(m => m.TargetId).Should().Contain("Service|m2lb.person.api");

        var submitted = await registry.SubmitAsync("import:m2lb", created.Scope.Id, new(edited.Scope.Scope.Version, 2, null));
        submitted.Scope!.Revisions.First().State.Should().Be(ScopeRevisionState.InReview);
        submitted.Scope.Scope.State.Should().Be(ScopeState.Approved, "revision 1 stays the approved membership while revision 2 is in review");
    }
}
