using BirkNext.SecurityExpectations;
using BirkNext.SourceArchitecture;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services;

public sealed class SecurityCandidateSemanticsTests
{
    private static SecurityExpectationCandidate C(SecurityExpectationField field, string value, int file = 0, Guid? snapshot = null, string environment = "") => new()
    {
        FieldType = field, Value = value, NormalizedValue = SecurityExpectationValues.Normalize(field, value)!,
        SourceSnapshotId = snapshot ?? Guid.Empty, SourceFile = $"config{file}.json", EnvironmentScope=environment, EvidenceState = ArchitectureEvidenceState.Confirmed
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
    [Fact] public void CandidateIdentityIsStableAndExactDuplicateProvenanceIsRemoved()
    {
        var snapshot = Guid.NewGuid();
        var original = C(SecurityExpectationField.Authority,"https://identity.example.test/tenant",snapshot:snapshot) with { SourceLine=4, SourceSymbol="Authority" };
        var exactRepeat = original with { Id="duplicate-extractor-row" };
        var otherFile = C(SecurityExpectationField.Authority,"https://identity.example.test/tenant",file:1,snapshot:snapshot);
        var before = SecurityExpectationValues.Group([original]);
        var after = SecurityExpectationValues.Group([original,exactRepeat,otherFile]);
        after.Should().ContainSingle(); after[0].Id.Should().Be(before[0].Id);
        after[0].SupportingEvidenceCount.Should().Be(2);
        after[0].SupportingEvidence.Select(e=>e.FilePath).Should().Contain("config0.json").And.Contain("config1.json");
    }
    [Theory]
    [InlineData("{ABCDEFAB-1234-5678-9012-ABCDEFABCDEF}","abcdefab-1234-5678-9012-abcdefabcdef")]
    [InlineData(" ABCDEFAB-1234-5678-9012-ABCDEFABCDEF ","abcdefab-1234-5678-9012-abcdefabcdef")]
    public void GuidIdentifiersUseCanonicalComparison(string first,string second)
    {
        SecurityExpectationValues.Normalize(SecurityExpectationField.TenantId,first).Should().Be(SecurityExpectationValues.Normalize(SecurityExpectationField.TenantId,second));
        SecurityExpectationValues.Normalize(SecurityExpectationField.ClientId,first).Should().Be(SecurityExpectationValues.Normalize(SecurityExpectationField.ClientId,second));
    }
    [Fact] public void RedirectQueryAndPathSemanticsArePreserved()
    {
        var callback="https://example.test/authentication/login-callback";
        SecurityExpectationValues.Normalize(SecurityExpectationField.RedirectUrl,callback).Should().NotBe(SecurityExpectationValues.Normalize(SecurityExpectationField.RedirectUrl,callback+"/"));
        SecurityExpectationValues.Normalize(SecurityExpectationField.RedirectUrl,callback).Should().NotBe(SecurityExpectationValues.Normalize(SecurityExpectationField.RedirectUrl,callback+"?x=1"));
        SecurityExpectationValues.Normalize(SecurityExpectationField.RedirectUrl,"https://EXAMPLE.test/authentication/login-callback?x=1")
            .Should().Be("https://example.test/authentication/login-callback?x=1");
    }
    [Fact] public void AuthorityAndHostNormalizationAreTyped()
    {
        SecurityExpectationValues.Normalize(SecurityExpectationField.Authority," HTTPS://LOGIN.MICROSOFTONLINE.COM/TENANT/ ")
            .Should().Be(SecurityExpectationValues.Normalize(SecurityExpectationField.Authority,"https://login.microsoftonline.com/TENANT"));
        SecurityExpectationValues.Normalize(SecurityExpectationField.RestHost,"API.EXAMPLE.COM.").Should().Be("api.example.com");
        SecurityExpectationValues.Normalize(SecurityExpectationField.RestHost,"*.example.com").Should().Be("*.example.com");
        SecurityExpectationValues.Normalize(SecurityExpectationField.RestHost,"*.example.com").Should().NotBe(SecurityExpectationValues.Normalize(SecurityExpectationField.RestHost,"example.com"));
    }
    [Fact] public void CandidateGroupingKeepsRecognizedSourceEnvironmentsSeparate()
    {
        var grouped=SecurityExpectationValues.Group([
            C(SecurityExpectationField.TenantId,"tenant.example.test",file:0,environment:"Development"),
            C(SecurityExpectationField.TenantId,"TENANT.EXAMPLE.TEST",file:1,environment:"Production")]);
        grouped.Should().HaveCount(2);
        grouped.Select(c=>c.EnvironmentScope).Should().BeEquivalentTo("Development","Production");
        grouped.Select(c=>c.Id).Should().OnlyHaveUniqueItems();
    }
}
