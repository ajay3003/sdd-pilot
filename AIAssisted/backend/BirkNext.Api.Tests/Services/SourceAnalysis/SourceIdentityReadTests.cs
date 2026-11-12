using System.Text.Json;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Services.SourceAnalysis;
using BirkNext.Api.Tests.Services.AiCodeReview;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.SourceAnalysis;

/// <summary>
/// The identity-only read of stored Source Analysis snapshots (Test Plan Suggestions performance): it materializes exactly the identity and
/// source-recognition members, equal to the full read, and skips the large evidence sections. The source context computed from it equals the
/// context computed from the full read.
/// </summary>
public sealed class SourceIdentityReadTests
{
    [Fact]
    public async Task The_identity_read_equals_the_full_read_for_every_identity_member_and_skips_the_large_sections()
    {
        await using var db = AiCodeReviewFixture.Db();
        await AiCodeReviewFixture.AnalyzeAsync(db, "contoso-shop.zip", AiCodeReviewFixture.Baseline());
        await AiCodeReviewFixture.AnalyzeAsync(db, "contoso-shop-v2.zip", AiCodeReviewFixture.Current());
        var store = new IqrSourceStore(db);
        var full = await store.ListSourceAnalysisAsync(500);
        var light = await store.ListSourceAnalysisIdentitiesAsync(500);

        light.Select(s => s.Id).Should().Equal(full.Select(s => s.Id), "same rows, same order");
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        for (var i = 0; i < full.Count; i++)
        {
            var (f, l) = (JsonSerializer.SerializeToElement(full[i], web), JsonSerializer.SerializeToElement(light[i], web));
            foreach (var member in IqrSourceStore.IdentityMembers)
                l.GetProperty(member).GetRawText().Should().Be(f.GetProperty(member).GetRawText(), $"{member} is an identity member");
        }
        full.Should().Contain(s => s.TestInventory != null || s.CodeRiskEvidence != null, "the fixture has large sections to skip");
        light.Should().OnlyContain(s => s.TestInventory == null && s.CodeRiskEvidence == null && s.DatabaseArchitecture == null && s.Tests.Count == 0);
    }

    [Fact]
    public async Task The_source_context_from_the_identity_read_equals_the_context_from_the_full_read()
    {
        await using var db = AiCodeReviewFixture.Db();
        await AiCodeReviewFixture.AnalyzeAsync(db, "contoso-shop.zip", AiCodeReviewFixture.Baseline());
        await AiCodeReviewFixture.AnalyzeAsync(db, "contoso-shop-v2.zip", AiCodeReviewFixture.Current());
        var store = new IqrSourceStore(db);
        var service = new SourceBaselineService(db, store);
        var fromLight = await service.ContextAsync(default);
        var fromFull = await service.ContextAsync(await store.ListSourceAnalysisAsync(SourceBaselineService.Window), default);
        JsonSerializer.Serialize(fromLight).Should().Be(JsonSerializer.Serialize(fromFull));
        fromLight.Sources.Should().NotBeEmpty();
    }
}
