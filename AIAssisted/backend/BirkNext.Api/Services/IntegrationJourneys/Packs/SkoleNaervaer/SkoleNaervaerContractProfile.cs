using BirkNext.Api.Services.AltinnTesting;
using BirkNext.Api.Services.ContractEvolution;
using BirkNext.Integrations;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;

/// <summary>
/// Skolenærvær's use of the generic contract-evolution engine: the contract dependency graph of Rapportmottak as DATA, and the domain's
/// semantic rules for the Altinn form. Every boundary states its evidence; a mapping BirkNext has no evidence of stays unknown (null), so
/// its impact is Not evaluated rather than guessed. Nothing here diffs, fingerprints or traverses: the engine does.
/// </summary>
public static class SkoleNaervaerContractProfile
{
    public const string MuMessage = "skolenaervaer:mu-message";
    public const string SkoleReport = "skolenaervaer:skole-report-request";
    public const string BufdataDataset = "skolenaervaer:bufdata-dataset";
    private const string CrossRepo = "docs/skolenaervaer-cross-repo-verification.md";

    /// <summary>The Rapportmottak contract chain, starting at the Altinn form contract the live metadata identifies.</summary>
    public static ContractDependencyGraph Graph(string altinnFormContractId) => new(
    [
        AltinnContractEvolution.ProducerBoundary(new SkoleNaervaerAltinnForm(), altinnFormContractId, "BirkNext", "Altinn"),
        new()
        {
            Id = "altinn-to-mu", Label = "Altinn → MU (form to MU message)", FromComponent = "Altinn", ToComponent = "MU", Role = ContractBoundaryRole.Maps,
            ContractId = altinnFormContractId, OutputContractId = MuMessage, Mapping = null,
            Evidence = "Who turns the Altinn form into MU's .flat message, and how, is not in BirkNext's evidence (the MU message nests the hours differently from the form).",
        },
        new()
        {
            Id = "mu-transport", Label = "MU → MM (transport)", FromComponent = "MU", ToComponent = "MM", Role = ContractBoundaryRole.Transports, ContractId = MuMessage,
            Evidence = $"MU hands MM a Service Bus queue message with a Blob reference to the AES-256-CBC encrypted payload; it does not read the report ({CrossRepo} §1, source review).",
        },
        new()
        {
            Id = "mm-input", Label = "MM input (XSD v2.0.0 flat)", FromComponent = "MU", ToComponent = "MM", Role = ContractBoundaryRole.Consumes, ContractId = MuMessage,
            Paths = null, UnknownMembers = ContractUnknownMemberPolicy.Reject, SupportedVersions = ["2.0.0"],
            Evidence = $"MM validates the decrypted message against XSD v2.0.0 flat before mapping ({CrossRepo} §1). Its read paths are not imported into BirkNext.",
        },
        new()
        {
            Id = "mm-to-skole", Label = "MM → Skoletjenesten mapping", FromComponent = "MM", ToComponent = "Skoletjenesten", Role = ContractBoundaryRole.Maps,
            ContractId = MuMessage, OutputContractId = SkoleReport, Mapping = null,
            Evidence = $"MM maps XML to Skole's JSON report (SkolenaervaerMapper). The field-level match was verified in a source review ({CrossRepo} §2) but the mapping is not imported as data.",
        },
        new()
        {
            Id = "skole-intake", Label = "Skoletjenesten report intake", FromComponent = "MM", ToComponent = "Skoletjenesten", Role = ContractBoundaryRole.Consumes,
            ContractId = SkoleReport, Paths = null, UnknownMembers = ContractUnknownMemberPolicy.Reject, SupportedVersions = ["2.0.0"],
            Evidence = $"Strict JSON: unknown, duplicate or missing fields give 400; skjemaversjon \"2.0.0\" ({CrossRepo} §2–3).",
        },
        new()
        {
            Id = "skole-to-bufdata", Label = "Skoletjenesten → Bufdata dataset", FromComponent = "Skoletjenesten", ToComponent = "Bufdata", Role = ContractBoundaryRole.Maps,
            ContractId = SkoleReport, OutputContractId = BufdataDataset, Mapping = null,
            Evidence = $"No dataset writer or publisher exists yet (ADR-028; no Parquet library is referenced) ({CrossRepo}).",
        },
        new()
        {
            Id = "bufdata", Label = "Bufdata", FromComponent = "Skoletjenesten", ToComponent = "Bufdata", Role = ContractBoundaryRole.Consumes, ContractId = BufdataDataset,
            Paths = null, Evidence = "Bufdata's consumption of the dataset is not verified (NV).",
        },
    ]);

    /// <summary>The impact of the live Altinn form against its confirmed baseline, or null when no live form schema has been read.</summary>
    public static ContractImpactResult? Analyze(AltinnAppContractRecord? record)
    {
        if (AltinnContractEvolution.ToContractSnapshot(record?.PublicMetadata) is not { } current) return null;
        var baseline = AltinnContractEvolution.ToContractSnapshot(record?.Confirmed?.Snapshot);
        return ContractImpactAnalyzer.Analyze(baseline, current, Graph(current.Descriptor.ContractId), new SkoleNaervaerContractSemantics());
    }
}

/// <summary>
/// Skolenærvær's business meaning of Altinn form changes. Only explicit, known paths are classified: the message header is technical; the
/// child's identity, the reporting period and the hours are business data. Any other new or changed member needs a business review — a new
/// code-list value is never mapped to an existing one, and no rule here guesses what an unknown field means.
/// </summary>
public sealed class SkoleNaervaerContractSemantics : IContractSemanticRules
{
    private const string Header = "Meldingshode";
    private const string ChildIdentity = "Klient/Identifikator";
    private static readonly string[] Period = ["Periode"];
    private static readonly string[] Hours = ["AntallTimerEtterPlanen", "AntallTimerFaktiskNaervaer"];

    public ContractSemanticAssessment? Assess(ContractChange change)
    {
        var path = change.Path;
        bool Under(string root) => ContractCompatibilityRules.Covers(root, path);
        if (change.Change is ContractChangeClass.NamespaceChanged or ContractChangeClass.VersionChanged)
            return new(ContractCompatibility.NeedsReview, "The form's namespace or version changed: MU, MM and Skole pin the report contract version.");
        if (path == AltinnContractEvolution.RootPath)
            return new(ContractCompatibility.NeedsReview, "The form's XML root changed: the message type downstream may differ.");
        if (Under(Header))
            return change.Change == ContractChangeClass.FieldAddedOptional
                ? new(ContractCompatibility.Compatible, "Optional technical message-header field: no business meaning.")
                : new(ContractCompatibility.NeedsReview, "Message header changed: MM uses Meldingshode/Id as the correlation id towards Skole.");
        if (Under(ChildIdentity))
            return change.Change is ContractChangeClass.FieldAddedOptional or ContractChangeClass.EnumValueAdded
                ? new(ContractCompatibility.NeedsReview, "The child's identity structure gained an option: Person lookup and Bufdata delivery depend on it.")
                : new(ContractCompatibility.Breaking, "The child's identity structure changed: the Person lookup and the Bufdata delivery key depend on it.");
        if (Period.Any(Under))
            return new(ContractCompatibility.NeedsReview, "Reporting-period semantics changed: Skole's validity rules and period lock depend on them.");
        if (Hours.Any(Under))
            return new(ContractCompatibility.NeedsReview, "Attendance hours changed: Skole's report and Bufdata's coverage depend on them.");
        return change.Change switch
        {
            ContractChangeClass.EnumValueAdded => new(ContractCompatibility.NeedsReview, $"New code-list value \"{change.NewValue}\": needs a business decision; it is never mapped to an existing value."),
            ContractChangeClass.FieldAddedOptional or ContractChangeClass.FieldAddedRequired =>
                new(ContractCompatibility.NeedsReview, "No Skolenærvær rule says this field is technical; whether it carries business meaning to Skole and Bufdata needs review."),
            _ => new(ContractCompatibility.NeedsReview, "A business field of the report changed; needs review."),
        };
    }
}
