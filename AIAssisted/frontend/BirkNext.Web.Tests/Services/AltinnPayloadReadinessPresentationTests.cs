using BirkNext.Integrations;
using BirkNext.Web.Services;
using BirkNext.Web.Tests.Components;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// Connected Systems → Altinn: test identities (who is involved) and the test payload (what is submitted) are separate Test Data gaps, each with
/// one owner action; a known contract identifier with incomplete evidence is shown with its real maturity, never as a bare "Missing".
/// </summary>
public sealed class AltinnPayloadReadinessPresentationTests
{
    [Fact]
    public void A_missing_xml_fixture_is_its_own_test_data_group_and_card_reason()
    {
        var payload = new AltinnReadinessItem("testdata", "Test data", AltinnReadinessState.Blocked, "What is submitted: XML fixture missing · owner: Test Data");
        var view = AltinnEnvironmentUiTests.View() with
        {
            Summary = [.. AltinnEnvironmentUiTests.View().Summary.Where(i => i.Key != "testdata"), payload],
            Contract = new("contract", "Report contract", AltinnReadinessState.Partial, "Contract identifier known · deployed/source contract evidence incomplete"),
        };

        var groups = AltinnEnvironmentPresentation.ThingsToFix(view);
        var testData = groups.Single(g => g.Key == "testdata");
        (testData.Owner, testData.Action, testData.Kind).Should().Be(("Test Data", "Open Test Data", AltinnEnvironmentPresentation.FixAction.TestData));
        testData.Items.Single().Should().Be(new AltinnEnvironmentPresentation.FixItem("XML fixture", "missing"));
        AltinnEnvironmentPresentation.ToShell(testData).Href.Should().Be(AltinnEnvironmentPresentation.TestDataHref, "the owner is Test Data; no XML editing here");
        AltinnEnvironmentPresentation.CardReason(payload, view).Should().Be("XML fixture missing");
        groups.Single(g => g.Key == "contract").Items.Single().Problem.Should().StartWith("Contract identifier known", "partial evidence is not Missing");
    }

    [Fact]
    public void A_ready_payload_adds_no_fix_item()
    {
        var view = AltinnEnvironmentUiTests.View() with
        {
            Summary = [.. AltinnEnvironmentUiTests.View().Summary.Where(i => i.Key != "testdata"), new AltinnReadinessItem("testdata", "Test data", AltinnReadinessState.Ready, "What is submitted: XML fixture ready")],
        };
        AltinnEnvironmentPresentation.ThingsToFix(view).Should().NotContain(g => g.Key == "testdata");
    }
}
