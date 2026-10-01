using BirkNext.SecurityExpectations;
using BirkNext.SourceArchitecture;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services;

public sealed class SecurityCandidateSemanticsTests
{
    private static SecurityExpectationCandidate C(SecurityExpectationField field, string value, int file = 0, Guid? snapshot = null) => new()
    {
        FieldType = field, Value = value, NormalizedValue = SecurityExpectationValues.Normalize(field, value)!,
        SourceSnapshotId = snapshot ?? Guid.Empty, SourceFile = $"config{file}.json", EvidenceState = ArchitectureEvidenceState.Confirmed
    };
    [Fact] public void TenOccurrencesAreOneCandidateWithTenEvidenceItems()
    {
        var grouped = SecurityExpectationValues.Group(Enumerable.Range(0,10).Select(i => C(SecurityExpectationField.Authority,"https://identity.example.test/tenant",i)));
        grouped.Should().HaveCount(1); grouped[0].SupportingEvidenceCount.Should().Be(10);
        SecurityExpectationValues.DeriveState(grouped[0],new(),1).Should().Be(SecurityCandidateState.Detected);
    }
    [Fact] public void UnapprovedAmbiguityIsReviewAndNeverConflict()
    {
        var a = C(SecurityExpectationField.Authority,"https://identity.example.test/a");
        SecurityExpectationValues.DeriveState(a,new(),2).Should().Be(SecurityCandidateState.NeedsReview);
    }
    [Fact] public void ApprovedSingletonMatchesOrConflicts()
    {
        var approved = new ApprovedSecurityExpectations { ExpectedAuthority = "https://identity.example.test/a" };
        SecurityExpectationValues.DeriveState(C(SecurityExpectationField.Authority,approved.ExpectedAuthority),approved,2).Should().Be(SecurityCandidateState.MatchesSource);
        SecurityExpectationValues.DeriveState(C(SecurityExpectationField.Authority,"https://identity.example.test/b"),approved,2).Should().Be(SecurityCandidateState.Conflict);
    }
    [Fact] public void MultiValueAdditionDoesNotConflict()
    {
        var approved = new ApprovedSecurityExpectations { AllowedRestHosts = ["a.example.test","b.example.test"] };
        SecurityExpectationValues.DeriveState(C(SecurityExpectationField.RestHost,"c.example.test"),approved,3).Should().Be(SecurityCandidateState.Detected);
        SecurityExpectationValues.DeriveState(C(SecurityExpectationField.RestHost,"a.example.test"),approved,3).Should().Be(SecurityCandidateState.MatchesSource);
    }
    [Theory]
    [InlineData(SecurityExpectationField.Authority,"https://login.microsoftonline.com/tenant","https://login.microsoftonline.com/tenant/v2.0")]
    [InlineData(SecurityExpectationField.RedirectUrl,"https://example.test/Callback","https://example.test/callback")]
    [InlineData(SecurityExpectationField.RedirectUrl,"https://example.test/callback","https://example.test/callback/")]
    [InlineData(SecurityExpectationField.RestHost,"example.test","example.test:8443")]
    [InlineData(SecurityExpectationField.TenantId,"11111111-1111-1111-1111-111111111111","tenant.example.test")]
    public void UnprovenEquivalenceStaysSeparate(SecurityExpectationField field,string a,string b)
        => SecurityExpectationValues.Normalize(field,a).Should().NotBe(SecurityExpectationValues.Normalize(field,b));
    [Fact] public void RelatedSourcesCombineEvidenceWithoutMergingSnapshots()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var grouped = SecurityExpectationValues.Group([C(SecurityExpectationField.RestHost,"api.example.test",snapshot:a),C(SecurityExpectationField.RestHost,"API.example.test",snapshot:b)]);
        grouped.Should().HaveCount(1); grouped[0].SourceSnapshotIds.Should().BeEquivalentTo([a,b]); grouped[0].SupportingEvidenceCount.Should().Be(2);
    }
}
