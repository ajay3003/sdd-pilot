using BirkNext.Interpretation;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.CanonicalInterpretation;

/// <summary>
/// Cases G–N of the canonical correlation rules: for every case the source evidence, the normalized identity, the expected relation, its rule id
/// and its resolution — and the false positives each rule must refuse. Cases A–F stay in WorkItemInterpretationTests (unchanged).
/// </summary>
public sealed class CorrelationRulesTests
{
    const string Snap = "11111111-1111-1111-1111-111111111111";
    const string Snap2 = "22222222-2222-2222-2222-222222222222";

    static CorrelationResult Run(CorrelationInput input) => CorrelationRules.Correlate(input);
    static CorrelationInput In() => CorrelationInput.Empty("proj");

    // ---- G: same intent across modules ----

    [Fact]
    public void G_same_intent_in_two_modules_becomes_a_candidate_capability_never_an_identity_merge()
    {
        var result = Run(In() with
        {
            WorkItems =
            [
                new("M2LB", "001-absence-reporting", "Absence reporting for schools", ["Report pupil absence", "Validate absence report"], "M2LB/specs/001/spec.md", Snap),
                new("MU", "004-absence-report-intake", "Absence report intake", ["Receive absence report", "Validate absence report"], "MU/specs/004/spec.md", Snap),
            ],
        });

        result.Entities.Count(e => e.Type == CanonicalEntityType.Feature).Should().Be(2, "both work items keep their own identity");
        var capability = result.Entities.Should().ContainSingle(e => e.Type == CanonicalEntityType.Capability).Subject;
        capability.Attribute("candidate").Should().Be("true");
        var links = result.Relations.Where(r => r.Type == CanonicalRelationType.Implements).ToList();
        links.Should().HaveCount(2).And.OnlyContain(r => r.Resolution == RelationResolution.Candidate && r.RuleId == CorrelationRules.IntentRule && !r.IsEstablished);
        links.Should().OnlyContain(r => r.Explanation.Contains("confirm or reject"));
        result.Relations.Should().NotContain(r => r.Type == CanonicalRelationType.SameAs || r.Type == CanonicalRelationType.CandidateSameAs, "wording never proposes identity equivalence");
    }

    [Fact]
    public void G_rejects_similar_features_within_one_module_and_unrelated_wording()
    {
        var result = Run(In() with
        {
            WorkItems =
            [
                new("M2LB", "001", "Absence reporting", ["Report absence"], null),
                new("M2LB", "002", "Absence reporting export", ["Report absence export"], null),
                new("MU", "003", "Message archive cleanup", ["Delete expired messages"], null),
            ],
        });
        result.Entities.Should().NotContain(e => e.Type == CanonicalEntityType.Capability, "within one module features are distinct; across modules the wording is unrelated");
    }

    // ---- H: capability candidates ----

    [Fact]
    public void H_three_modules_group_into_one_candidate_capability_without_collapsing_features()
    {
        var result = Run(In() with
        {
            WorkItems =
            [
                new("A", "1", "Absence report validation", ["Validate absence report"], null),
                new("B", "2", "Absence report validation rules", ["Validate absence report"], null),
                new("C", "3", "Absence report validation service", ["Validate absence report"], null),
            ],
        });
        result.Entities.Should().ContainSingle(e => e.Type == CanonicalEntityType.Capability);
        result.Entities.Count(e => e.Type == CanonicalEntityType.Feature).Should().Be(3);
        result.Relations.Count(r => r.Type == CanonicalRelationType.Implements && r.Resolution == RelationResolution.Candidate).Should().Be(3);
    }

    // ---- I + J: contract identity vs occurrence vs version ----

    static ContractOccurrenceInput Xsd(string package, string path, string hash, string? version, string snapshot = Snap) =>
        new("XmlSchema", package, package, path, snapshot, hash, "urn:buf:skolenaervaer:v1", version, "Fravaersmelding", "abc123", ["SkoleService"]);

    [Fact]
    public void I_identical_content_in_two_packages_is_one_version_with_two_preserved_occurrences()
    {
        var result = Run(In() with { Contracts = [Xsd("M2LB", "schemas/fravaer.xsd", "AAA", "1.0"), Xsd("M2LB.Common", "contracts/fravaer.xsd", "aaa", "1.0", Snap2)] });

        result.Entities.Count(e => e.Type == CanonicalEntityType.ContractOccurrence).Should().Be(2, "occurrences are never merged or deleted");
        var version = result.Entities.Should().ContainSingle(e => e.Type == CanonicalEntityType.ContractVersion).Subject;
        version.IdentityKey.Should().Be("xmlschema:sha256:aaa@v1.0", "identity of content is the hash (case-insensitive), qualified by the declared version context");
        version.Attribute("contentHash").Should().Be("aaa");
        result.Relations.Where(r => r.Type == CanonicalRelationType.OccurrenceOf && r.RuleId == CorrelationRules.ContractHashRule)
            .Should().HaveCount(2).And.OnlyContain(r => r.Resolution == RelationResolution.Derived);
        var occurrences = result.Entities.Where(e => e.Type == CanonicalEntityType.ContractOccurrence).ToList();
        occurrences.Select(o => o.Attribute("package")).Should().BeEquivalentTo(["M2LB", "M2LB.Common"]);
        occurrences.Should().OnlyContain(o => o.Attribute("revision") == "abc123" && o.Attribute("declaredVersion") == "1.0");
    }

    [Fact]
    public void I_a_moved_file_keeps_its_version_identity_and_a_new_occurrence_is_recorded()
    {
        var before = Run(In() with { Contracts = [Xsd("M2LB", "old/fravaer.xsd", "aaa", "1.0")] });
        var after = Run(In() with { Contracts = [Xsd("M2LB", "new/location/fravaer.xsd", "aaa", "1.0", Snap2)] });
        after.Entities.Single(e => e.Type == CanonicalEntityType.ContractVersion).Key.Should().Be(before.Entities.Single(e => e.Type == CanonicalEntityType.ContractVersion).Key);
        after.Entities.Single(e => e.Type == CanonicalEntityType.Contract).Key.Should().Be(before.Entities.Single(e => e.Type == CanonicalEntityType.Contract).Key);
    }

    [Fact]
    public void J_two_versions_stay_two_versions_of_one_declared_contract()
    {
        var result = Run(In() with { Contracts = [Xsd("M2LB", "v1/fravaer.xsd", "aaa", "1.0"), Xsd("M2LB", "v2/fravaer.xsd", "bbb", "2.0")] });
        result.Entities.Count(e => e.Type == CanonicalEntityType.ContractVersion).Should().Be(2, "different versions never collapse into one active version");
        result.Entities.Should().ContainSingle(e => e.Type == CanonicalEntityType.Contract);
        result.Entities.Where(e => e.Type == CanonicalEntityType.ContractVersion).Select(e => e.Version).Should().BeEquivalentTo(["1.0", "2.0"]);
        result.Relations.Should().NotContain(r => r.Type == CanonicalRelationType.Supersedes || r.Type == CanonicalRelationType.SameAs, "compatibility and order are never inferred");
    }

    [Fact]
    public void J_same_declared_version_with_different_content_is_an_ambiguous_conflict_to_review()
    {
        var result = Run(In() with { Contracts = [Xsd("M2LB", "a/fravaer.xsd", "aaa", "1.0"), Xsd("M2LB.Common", "b/fravaer.xsd", "bbb", "1.0", Snap2)] });
        var conflict = result.Relations.Should().ContainSingle(r => r.RuleId == CorrelationRules.ContractVersionConflictRule).Subject;
        conflict.Resolution.Should().Be(RelationResolution.Ambiguous);
        conflict.Type.Should().Be(CanonicalRelationType.CandidateSameAs);
        result.Entities.Count(e => e.Type == CanonicalEntityType.ContractVersion).Should().Be(2);
    }

    [Fact]
    public void J_matching_file_names_without_declared_identity_are_never_grouped()
    {
        ContractOccurrenceInput Api(string package, string hash) => new("OpenApi", package, null, "openapi.yaml", Snap, hash, null, "1.0", null);
        var result = Run(In() with { Contracts = [Api("ServiceA", "aaa"), Api("ServiceB", "bbb")] });
        result.Entities.Should().NotContain(e => e.Type == CanonicalEntityType.Contract, "two openapi.yaml files with no declared title are not one contract");
        result.Relations.Should().NotContain(r => r.RuleId == CorrelationRules.ContractVersionConflictRule);
    }

    // ---- K: aliases ----

    [Fact]
    public void K_declared_aliases_are_explicit_and_an_acronym_match_is_only_proposed()
    {
        var result = Run(In() with
        {
            Systems =
            [
                new(CanonicalEntityType.System, "Meldingsutveksleren", ["Buf.Meldingsutveksler.Service"], "domain profile skolenaervaer"),
                new(CanonicalEntityType.System, "MU", [], "integration catalog"),
            ],
        });
        result.Aliases.Should().Contain(a => a.Alias == "Buf.Meldingsutveksler.Service" && a.State == AliasState.Explicit && a.RuleId == CorrelationRules.DeclaredAliasRule);
        var proposed = result.Aliases.Should().ContainSingle(a => a.Alias == "MU").Subject;
        proposed.State.Should().Be(AliasState.Proposed, "the same letters can name another system in another project");
        proposed.EntityKey.Should().Be(CanonicalKeys.EntityKey(CanonicalEntityType.System, "meldingsutveksleren"));
        result.Entities.Count(e => e.Type == CanonicalEntityType.System).Should().Be(2, "an alias never merges identities");
    }

    [Theory]
    [InlineData("MU", "Meldingsutveksleren", true)]
    [InlineData("MU", "Meldings Utveksler", true)]
    [InlineData("MM", "Meldingsutveksleren", false)]
    [InlineData("MU", "Mottak", false)]
    [InlineData("OS", "Order Service", true)]
    [InlineData("OS", "Order Shipping Service", false)]
    public void K_acronym_matching_is_strict(string acronym, string name, bool expected) => CorrelationRules.AcronymMatches(acronym, name).Should().Be(expected);

    // ---- L: source symbols ----

    [Fact]
    public void L_route_attribute_is_explicit_name_similarity_is_only_a_candidate_and_requirement_ids_are_references()
    {
        var result = Run(In() with
        {
            Operations = [new("SkoleService", "GET", "/api/absence/{pupilId}", "openapi")],
            Symbols =
            [
                new("m2lb", "src/SkoleService/AbsenceApi.cs", "AbsenceApi.Get", "Buf.Skole", "Controller", "GET", "/api/absence/{id}", ["FR-1"], [], "SkoleService", Snap),
                new("m2lb", "src/SkoleService/AbsenceController.cs", "AbsenceController", "Buf.Skole", "Controller", null, null, [], [], "SkoleService", Snap),
            ],
        });
        var op = result.Entities.Should().ContainSingle(e => e.Type == CanonicalEntityType.ApiOperation).Subject;
        op.IdentityKey.Should().Be("skoleservice|GET /api/absence/{}", "parameter names fold, so {pupilId} and {id} are one operation");
        result.Relations.Should().ContainSingle(r => r.RuleId == CorrelationRules.SymbolRouteRule && r.Resolution == RelationResolution.Explicit);
        result.Relations.Should().ContainSingle(r => r.RuleId == CorrelationRules.SymbolNameRule).Which.Resolution.Should().Be(RelationResolution.Candidate);
        var requirement = result.Relations.Should().ContainSingle(r => r.RuleId == CorrelationRules.SymbolRequirementRule).Subject;
        requirement.Type.Should().Be(CanonicalRelationType.References, "a reference is not an IMPLEMENTS claim");
        requirement.TargetKey.Should().Be(CanonicalKeys.EntityKey(CanonicalEntityType.Requirement, "FR-001"), "FR-1 normalizes to FR-001");
        result.Relations.Where(r => r.RuleId == CorrelationRules.SymbolServiceRule).Should().HaveCount(2).And.OnlyContain(r => r.Resolution == RelationResolution.Derived);
    }

    [Fact]
    public void L_a_symbol_identity_survives_a_file_move()
    {
        SourceSymbolInput S(string path) => new("m2lb", path, "AbsenceApi.Get", "Buf.Skole", "Controller", "GET", "/x", [], [], "SkoleService");
        Run(In() with { Symbols = [S("a/AbsenceApi.cs")] }).Entities.Single(e => e.Type == CanonicalEntityType.SourceSymbol).Key
            .Should().Be(Run(In() with { Symbols = [S("b/moved/AbsenceApi.cs")] }).Entities.Single(e => e.Type == CanonicalEntityType.SourceSymbol).Key);
    }

    // ---- M: Test → VERIFIES ----

    [Fact]
    public void M_explicit_links_verify_candidates_stay_candidates_and_execution_status_is_absent()
    {
        var result = Run(In() with
        {
            Tests =
            [
                new("m2lb", "tests/AbsenceTests.cs", "Buf.Skole.Tests.AbsenceTests.Reports_absence", "AbsenceTests", ["FR-001", "US1"], ["FR-2"], Snap, "m2lb|Skole.Tests|Buf.Skole.Tests.AbsenceTests.Reports_absence"),
                new("m2lb", "tests/OtherTests.cs", "Buf.Skole.Tests.OtherTests.Something", null, [], ["SC-3"], Snap),
            ],
        });
        var verifies = result.Relations.Where(r => r.Type == CanonicalRelationType.Verifies).ToList();
        verifies.Where(r => r.Resolution == RelationResolution.Explicit).Select(r => r.TargetKey).Should().BeEquivalentTo(
            [CanonicalKeys.EntityKey(CanonicalEntityType.Requirement, "FR-001"), CanonicalKeys.EntityKey(CanonicalEntityType.UserStory, "US1")]);
        verifies.Where(r => r.Resolution == RelationResolution.Candidate).Should().HaveCount(2).And.OnlyContain(r => r.RuleId == CorrelationRules.TestCandidateRule && !r.IsEstablished);
        verifies.Should().OnlyContain(r => !r.Explanation.Contains("passed", StringComparison.OrdinalIgnoreCase) || r.Explanation.Contains("not that it ran or passed"));
        result.Entities.Single(e => e.DisplayName == "Buf.Skole.Tests.AbsenceTests.Reports_absence").IdentityKey
            .Should().Be("m2lb|Skole.Tests|Buf.Skole.Tests.AbsenceTests.Reports_absence", "the test evidence's StableIdentity is the canonical identity");
        result.Entities.Should().ContainSingle(e => e.Type == CanonicalEntityType.TestSuite);
    }

    [Fact]
    public void M_a_candidate_that_is_also_explicit_is_not_duplicated()
    {
        var result = Run(In() with { Tests = [new("p", "t.cs", "T.A", null, ["FR-1"], ["FR-001"])] });
        result.Relations.Where(r => r.Type == CanonicalRelationType.Verifies).Should().ContainSingle().Which.Resolution.Should().Be(RelationResolution.Explicit);
    }

    // ---- N: cross-domain chains ----

    [Fact]
    public void N_partial_chain_reports_missing_links_and_never_fabricates_them()
    {
        var result = Run(In() with
        {
            Operations = [new("SkoleService", "POST", "/api/absence", "openapi")],
            Contracts = [Xsd("M2LB", "schemas/fravaer.xsd", "aaa", "1.0") with { ReferencingServices = ["SkoleService"] }],
            Channels = [new("ServiceBusTopic", "absence-reported", ["SkoleService"], ["MeldingsutvekslerReceiver"], "src/x.cs:10", Snap)],
        });
        var hops = result.Relations.Select(r => new CorrelationChain.Hop(r.SourceKey, r.TargetKey, r.Type, r.Resolution, RelationReviewState.NotReviewed, r.RuleId)).ToList();
        CanonicalEntityType? TypeOf(string key) => result.Entities.FirstOrDefault(e => e.Key == key)?.Type;
        var service = CanonicalKeys.EntityKey(CanonicalEntityType.Service, "skoleservice");

        var steps = CorrelationChain.Walk(service, [CanonicalEntityType.Service, CanonicalEntityType.ApiOperation, CanonicalEntityType.ContractVersion, CanonicalEntityType.Test], hops, TypeOf);
        steps[0].Missing.Should().BeFalse("the service exposes the operation explicitly");
        steps[1].Missing.Should().BeTrue("no evidence links the operation to the contract; nothing is filled in");
        steps[2].Missing.Should().BeTrue();

        result.Relations.Should().Contain(r => r.Type == CanonicalRelationType.Produces && r.RuleId == CorrelationRules.ChannelRule && r.Resolution == RelationResolution.Explicit);
        result.Relations.Should().Contain(r => r.Type == CanonicalRelationType.Consumes && r.SourceKey == CanonicalKeys.EntityKey(CanonicalEntityType.Service, "meldingsutvekslerreceiver"));
    }

    [Fact]
    public void Rules_are_deterministic_whatever_the_input_order()
    {
        var items = new List<WorkItemIntentInput> { new("A", "1", "Absence report validation", [], null), new("B", "2", "Absence report validation", [], null) };
        var tests = new List<TestInput> { new("p", "a.cs", "T.A", null, ["FR-1"], []), new("p", "b.cs", "T.B", null, [], ["FR-2"]) };
        var a = Run(In() with { WorkItems = items, Tests = tests });
        var b = Run(In() with { WorkItems = [.. Enumerable.Reverse(items)], Tests = [.. Enumerable.Reverse(tests)] });
        a.Relations.Select(r => (r.Key, r.Resolution, r.EvidenceFingerprint)).Should().Equal(b.Relations.Select(r => (r.Key, r.Resolution, r.EvidenceFingerprint)));
        a.Entities.Select(e => e.Key).Should().Equal(b.Entities.Select(e => e.Key));
        a.RuleSetVersion.Should().Be("correlation/2");
    }

    // ---- precedence ----

    [Theory]
    [InlineData(RelationResolution.Candidate, "fp", ReviewDecisionOutcome.Confirmed, "fp", RelationReviewState.Confirmed, RelationResolution.Confirmed)]
    [InlineData(RelationResolution.Candidate, "fp2", ReviewDecisionOutcome.Confirmed, "fp", RelationReviewState.RevalidationRequired, RelationResolution.Candidate)]
    [InlineData(RelationResolution.Candidate, "fp", ReviewDecisionOutcome.Rejected, "fp", RelationReviewState.Rejected, RelationResolution.Rejected)]
    [InlineData(RelationResolution.Candidate, "fp2", ReviewDecisionOutcome.Rejected, "fp", RelationReviewState.RevalidationRequired, RelationResolution.Rejected)]
    [InlineData(RelationResolution.Explicit, "fp2", ReviewDecisionOutcome.Rejected, "fp", RelationReviewState.Conflict, RelationResolution.Explicit)]
    [InlineData(RelationResolution.Candidate, "fp", ReviewDecisionOutcome.Deferred, "fp", RelationReviewState.Deferred, RelationResolution.Candidate)]
    public void Precedence_human_decisions_win_in_scope_but_changed_evidence_is_revalidated_or_conflicts(
        RelationResolution evidence, string evidenceFp, ReviewDecisionOutcome outcome, string decisionFp, RelationReviewState review, RelationResolution effective)
    {
        var decision = new ReviewDecisionFact(Guid.NewGuid(), ReviewTargetKind.Relation, "k", outcome, decisionFp, "reviewer", DateTimeOffset.UtcNow, null, "proj");
        CorrelationPrecedence.Apply(evidence, evidenceFp, decision).Should().Be((review, effective));
        CorrelationPrecedence.Counts(effective).Should().Be(effective is RelationResolution.Explicit or RelationResolution.Confirmed);
    }
}
