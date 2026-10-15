using BirkNext.Api.Services.ContractEvolution;
using BirkNext.Integrations;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.AltinnTesting;

/// <summary>
/// Altinn app forms as generic contracts: the live public metadata and the confirmed baseline become <see cref="ContractSnapshot"/>s, and a
/// domain pack's form binding becomes the producer boundary. Altinn-generic: no app or domain is named here.
/// </summary>
public static class AltinnContractEvolution
{
    /// <summary>The XML root element is part of the form contract: a node so that a changed root is detected.</summary>
    public const string RootPath = "#xml-root";

    public static string ContractId(string appId, string dataTypeId) => $"altinn:{appId}:{dataTypeId}";

    /// <summary>The form contract of one Altinn snapshot, or null when the snapshot has no form schema.</summary>
    public static ContractSnapshot? ToContractSnapshot(AltinnContractSnapshot? snapshot)
    {
        if (snapshot is not { Fields: { } fields, FormDataTypeId: { } dataType }) return null;
        var shapes = fields.Select(field => new ContractFieldShape(field.Path, field.Format is null ? field.Type : $"{field.Type}:{field.Format}", field.Required)).ToList();
        if (snapshot.XmlRootElement is { } root) shapes.Add(new ContractFieldShape(RootPath, root, true));
        return ContractSnapshots.FromFields(new ContractDescriptor(ContractId(snapshot.AppId, dataType), SourceContractType.JsonSchema, dataType, null, snapshot.Source.ToString(),
                Location: $"{snapshot.AppId} · {dataType}"),
            snapshot.XmlNamespace, shapes, snapshot.CapturedAt,
            $"Altinn form schema of {snapshot.AppId} ({snapshot.Source}, documents fingerprint {Short(snapshot.Fingerprint)}).");
    }

    /// <summary>
    /// The producer boundary of a form binding: the paths the producer writes are exactly the binding's paths. Namespace and root are taken from
    /// the contract at write time, so they are not producer paths.
    /// </summary>
    public static ContractBoundary ProducerBoundary(IAltinnFormBinding binding, string contractId, string producer, string consumer) => new()
    {
        Id = "producer", Label = $"{producer} → {consumer}", FromComponent = producer, ToComponent = consumer, Role = ContractBoundaryRole.Produces,
        ContractId = contractId, Paths = [.. binding.Fields.Select(field => ContractSnapshots.NormalizePath(field.Path))],
        Evidence = $"{producer}'s form binding: it writes only these paths. {binding.BindingBasis}",
    };

    private static string Short(string fingerprint) => fingerprint.Length > 12 ? fingerprint[..12] : fingerprint;
}
