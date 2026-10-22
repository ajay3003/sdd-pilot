using BirkNext.Api.Services.SecurityClassification;
using BirkNext.Integrations;

namespace BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;

/// <summary>
/// Skolenærvær's projection of the shared Security Classification capability: the shared requirements its journeys depend on, plus its own
/// downstream rules (protected data in the report service, the Bufdata exclusion). The full review stays on the standalone page; this class
/// holds no evidence, source selection, test context or identity of its own.
/// </summary>
public sealed class SkoleNaervaerSecurityConsumer : ISecurityClassificationConsumer
{
    public const string Id = SkoleNaervaerJourneyPack.Id;

    public SecurityClassificationConsumerDefinition Definition { get; } = new(Id, "Skolenærvær", "Gradert tilgang across the Skolenærvær journeys",
    [
        SecurityRequirementKind.ClassificationPropagation, SecurityRequirementKind.UnauthorizedRuntimeDenial, SecurityRequirementKind.AuthorizedRuntimeAccess,
        SecurityRequirementKind.IdentityReferences, SecurityRequirementKind.TestContext,
    ],
    [
        new("protected-downstream-handling", "Protected downstream handling", "Skolenærværsrapport must keep a Kode 6/7 child's data protected after the Person lookup.", "Skolenærvær Testing"),
        new("bufdata-exclusion", "Bufdata exclusion rule", "The Bufdata dataset must not contain fødselsnummer or DUF.", "Skolenærvær Testing"),
    ]);
}
