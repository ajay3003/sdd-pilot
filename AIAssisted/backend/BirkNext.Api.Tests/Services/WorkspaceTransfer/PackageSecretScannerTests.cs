using System.Text.Json.Nodes;
using BirkNext.Api.Services.WorkspaceTransfer;
using FluentAssertions;
using Xunit;

namespace BirkNext.Api.Tests.Services.WorkspaceTransfer;

/// <summary>
/// The package secret scanner and diagnostic redaction. GUIDs are identities: a GUID whose last group holds eleven digits followed by a letter must
/// never be flagged as an identifier or rewritten by redaction (that made exports fail at random when a fresh GUID happened to have that shape).
/// </summary>
public sealed class PackageSecretScannerTests
{
    private const string GuidWithElevenDigits = "aaaaaaaa-0000-4000-8000-12345678901a";

    [Fact]
    public void A_guid_with_eleven_digits_in_its_last_group_is_not_an_identifier()
    {
        var payload = JsonNode.Parse($$"""{"id":"{{GuidWithElevenDigits}}","doc":"[{\"snapshotId\":\"{{GuidWithElevenDigits}}\"}]","note":"ref {{GuidWithElevenDigits}} done"}""");
        PackageSecretScanner.Scan("t", payload).Should().BeEmpty();
    }

    [Fact]
    public void Identifier_shaped_values_are_still_found_next_to_guids_and_in_embedded_json()
    {
        var payload = JsonNode.Parse($$"""{"id":"{{GuidWithElevenDigits}}","doc":"[{\"person\":\"01010012345\"}]","note":"{{GuidWithElevenDigits}} 01010012345"}""");
        PackageSecretScanner.Scan("t", payload).Should().HaveCount(2).And.OnlyContain(f => f.Severity == PackageSecretScanner.Sensitive);
    }

    [Fact]
    public void Redaction_keeps_every_guid_and_replaces_identifiers_deterministically()
    {
        var text = $"{GuidWithElevenDigits} 01010012345 {GuidWithElevenDigits}";
        var once = PackageSecretScanner.RedactIdentifiers(text);
        once.Should().StartWith(GuidWithElevenDigits).And.EndWith(GuidWithElevenDigits).And.NotContain("01010012345").And.Contain("[redacted-id-");
        PackageSecretScanner.RedactIdentifiers(text).Should().Be(once, "the same input always gives the same marker");
        PackageSecretScanner.Scan("t", JsonValue.Create(once)).Should().BeEmpty("a redacted value scans clean");
    }

    [Fact]
    public void Secrets_are_blocking_and_never_echoed()
    {
        var findings = PackageSecretScanner.Scan("t", JsonNode.Parse("""{"clientSecret":"s3cr3t-value","token":"eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ4In0.c2ln"}"""));
        findings.Should().HaveCount(2).And.OnlyContain(f => f.Severity == PackageSecretScanner.Blocking);
        string.Join(" ", findings.Select(f => f.Finding + f.Location)).Should().NotContain("s3cr3t").And.NotContain("eyJ");
    }
}
