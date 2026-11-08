using BirkNext.Api.Services.ContractTesting;
using BirkNext.ContractTesting;
using BirkNext.ExecutionEvidence;
using BirkNext.SourceDomains;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Services.ContractTesting;

/// <summary>The two middle impact classes: Potential (a change the test touches, not certain to break it) and Needs human review (a change rules cannot judge).</summary>
public sealed class ContractImpactRulesTests
{
    private static SourceContract Contract(bool nameRequired, string[] security, string[] servers) => new()
    {
        Id = "children", Name = "Children", Type = SourceContractType.OpenApi, Servers = [.. servers],
        Operations = [new("GET /children/{id}", "GET", "/children/{id}", ["id"])
        {
            Detailed = true, ResponseCodes = ["200"], ResponseSchemas = new() { ["200"] = "Child" }, Security = [.. security],
            ParameterDetails = [new("id", "path", true, "integer")],
        }],
        Types = [new("Child", "object", nameRequired ? [new("id", "integer", true), new("name", "string", true)] : [new("id", "integer", true)])],
    };

    private static readonly ContractTestDefinition Test = new()
    {
        Id = Guid.NewGuid(), ContractKey = "children", ContractVersion = 1, Operation = "GET /children/{id}", Name = "GET /children/{id} — succeeds", Kind = ContractTestKind.Positive,
        Template = new() { Protocol = ExecutionProtocol.Rest, Method = "GET", Path = "/children/{{id}}", Assertions = [new("status", AssertionKind.StatusCode, AssertionOperator.Equals, null, "200")] },
    };

    [Fact]
    public void A_removed_required_response_field_is_a_potential_impact_even_when_unasserted()
    {
        var impact = ContractTestRules.Impact(Test, Contract(true, [], []), Contract(false, [], []), 2);
        impact.Level.Should().Be(ContractTestImpactLevel.Potential);
        impact.Reasons.Should().Contain(r => r.StartsWith("Potential impact:") && r.Contains("Child.name") && r.Contains("it was required"));
    }

    [Fact]
    public void Changed_security_or_servers_need_human_review_not_a_verdict()
    {
        var security = ContractTestRules.Impact(Test, Contract(true, [], []), Contract(true, ["bearer"], []), 2);
        security.Level.Should().Be(ContractTestImpactLevel.NeedsReview);
        security.Reasons.Should().Contain(r => r.StartsWith("Needs human review:") && r.Contains("authentication binding"));

        var servers = ContractTestRules.Impact(Test, Contract(true, [], ["http://localhost/api"]), Contract(true, [], ["http://localhost/v2"]), 2);
        servers.Level.Should().Be(ContractTestImpactLevel.NeedsReview);
        servers.Reasons.Should().Contain(r => r.Contains("declared servers changed"));
    }
}
