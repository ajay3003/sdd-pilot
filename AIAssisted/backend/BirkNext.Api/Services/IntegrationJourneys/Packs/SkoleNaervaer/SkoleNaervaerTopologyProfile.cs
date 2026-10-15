using BirkNext.Api.Services.IntegrationTopology;

namespace BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;

/// <summary>
/// Skolenærvær's expectations of the integration topology: the components its journeys involve, with the explicit aliases different sources
/// use for them, and the paths its scenarios rely on. Expectations only — the topology resolves a step only from configured, discovered or
/// observed evidence, and this profile adds no connection, interface or address.
/// </summary>
public sealed class SkoleNaervaerTopologyProfile : ITopologyProfile
{
    public const string ProfileId = "skolenaervaer";
    private const string Docs = "docs/skolenaervaer-cross-repo-verification.md §1";

    public string Id => ProfileId;
    public string Label => "Skolenærvær";

    public IReadOnlyList<TopologyExpectedComponent> Components { get; } =
    [
        new("Altinn", ["Altinn (TT02)", "External reporting party (Altinn)"], "Reports are submitted through the Altinn app."),
        new("MU", ["Meldingsutveksleren"], $"Receives the report from Altinn and hands it to MM ({Docs})."),
        new("MM", ["Meldingsutvekslermottak", "Skolenærværsmottaket"], $"Validates and maps the report and calls Skoletjenesten ({Docs})."),
        new("Skoletjenesten", ["Skole", "Skolenærværsrapport", "Skolenaervaersrapport"], $"Receives reports and education data ({Docs})."),
        new("Person", [], $"Publishes person events Skoletjenesten consumes ({Docs})."),
        new("SkoleAdapter", [], $"Forwards education data from BiRK to Skoletjenesten ({Docs})."),
        new("Bufdata", [], $"Receives the completed dataset after period lock; no delivery exists yet (ADR-028, {Docs})."),
    ];

    public IReadOnlyList<TopologyExpectedPath> Paths { get; } =
    [
        new("rapportmottak", "Rapportmottak (normal report)",
        [
            new("Altinn → MU", "Altinn", "MU"),
            new("MU → MM", "MU", "MM"),
            new("MM → Skoletjenesten", "MM", "Skoletjenesten"),
            new("Skoletjenesten → Bufdata", "Skoletjenesten", "Bufdata"),
        ]),
        new("utdanningsdata", "Utdanningsdata (education data)", [new("SkoleAdapter → Skoletjenesten", "SkoleAdapter", "Skoletjenesten")]),
        new("person", "Person events", [new("Person → Skoletjenesten", "Person", "Skoletjenesten")]),
    ];
}
