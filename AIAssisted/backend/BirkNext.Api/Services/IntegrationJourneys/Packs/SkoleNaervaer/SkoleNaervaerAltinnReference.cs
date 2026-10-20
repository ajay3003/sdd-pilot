namespace BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;

/// <summary>
/// The Skolenærvær Altinn app's API description as a Test Pack reference: the safe facts of the app's OpenAPI document, shipped inside BirkNext
/// (<see cref="FileName"/>, an embedded resource) so the reference is available wherever BirkNext runs. The OpenAPI document itself is never read
/// at runtime, imported or bound; a fixture test pins every fact against what the generic OpenAPI reader extracts from it. It proves nothing
/// about the deployed app, runtime, credentials, reporters or execution.
/// </summary>
public static class SkoleNaervaerAltinnReference
{
    public const string FileName = "skolenaervaer-altinn.reference.json";
    public const string AppScope = "buf/skolenaervaer";

    public static TestPackReference Reference(string roleKey) =>
        new(roleKey, "OpenAPI", "Built-in OpenAPI reference for the Skolenærvær Altinn app", () => TestPackReferences.Embedded(FileName))
        {
            Scope = AppScope,
            NotProven =
            [
                "The deployed Altinn app version, its current TT02 deployment or runtime health",
                "Client id, client secret, redirect URI or scopes",
                "Reporter identity, authorization or role assignment",
                "Runtime token acquisition or integration enrollment",
                "Synthetic person/test identities or execution permission",
                "That a message can be or was submitted",
            ],
        };
}
