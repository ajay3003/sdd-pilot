using BirkNext.TestData;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>Validation trust reads through the shared vocabulary: only server checks are Verified; a client claim is never server-confirmed.</summary>
public sealed class TestDataTrustPresentationTests
{
    [Theory]
    [InlineData(TestDataValidationTrust.ServerVerifiedSchema, SemanticStatus.Verified)]
    [InlineData(TestDataValidationTrust.ServerExplicitSchemaReference, SemanticStatus.Verified)]
    [InlineData(TestDataValidationTrust.ClientReportedOnly, SemanticStatus.NotVerified)]
    [InlineData(TestDataValidationTrust.SchemaUnavailable, SemanticStatus.Unavailable)]
    [InlineData(TestDataValidationTrust.ValidationFailed, SemanticStatus.VerificationFailed)]
    [InlineData(TestDataValidationTrust.NotPerformed, SemanticStatus.NotAssessed)]
    public void Trust_maps_onto_the_shared_vocabulary(TestDataValidationTrust trust, SemanticStatus expected)
    {
        var (status, label, explanation) = TestDataPresentation.Trust(trust);
        status.Should().Be(expected);
        label.Should().Be(TestDataValidationTrusts.Label(trust));
        explanation.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_client_claim_is_never_server_confirmed_or_shown_as_verified()
    {
        TestDataPresentation.Trust(TestDataValidationTrust.ClientReportedOnly).Status.Should().NotBe(SemanticStatus.Verified);
        TestDataValidationTrusts.Of(null, "Valid").Should().Be(TestDataValidationTrust.ClientReportedOnly);
        TestDataValidationTrusts.Of(new TestDataSchemaCheck(TestDataSchemaStatus.SchemaUnavailable, "x.xsd", "", []), "Valid").Should().Be(TestDataValidationTrust.ClientReportedOnly);
        TestDataValidationTrusts.Of(new TestDataSchemaCheck(TestDataSchemaStatus.Invalid, "x.xsd", "", []), "Valid").Should().Be(TestDataValidationTrust.ValidationFailed);
    }
}
