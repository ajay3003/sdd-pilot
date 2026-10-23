using BirkNext.TestData;

namespace BirkNext.Web.Services;

/// <summary>
/// Test Data page wording. Approval (may it be used), validation (is it valid — format or schema) and intent (what the test expects of it)
/// are three independent facts, each with its own label; none is derived from another. Nothing here knows a domain: intent comes from the
/// generic <c>negative-test</c> capability or the expected-result kind, and source actions are keyed by provider id only.
/// </summary>
public static class TestDataPresentation
{
    public const string NegativeTestCapability = "negative-test";
    public const string UserGuideHref = "user-guide#test-data";
    public const string SchemaStudioHref = "schema-studio";

    public static string KindLabel(TestDataKind kind) => kind switch
    {
        TestDataKind.EntityReference => "Entity reference",
        TestDataKind.ExpectedResult => "Expected result",
        TestDataKind.ReferenceData => "Reference data",
        TestDataKind.Structured => "Structured payload",
        TestDataKind.Scalar => "Scalar value",
        _ => kind.ToString(),
    };

    public static string EnvironmentLabel(TestDataAssetSummary asset) => asset.Environment ?? "Any environment";

    public static string ApprovalLabel(TestDataApproval approval) => approval switch
    {
        TestDataApproval.Approved => "Approved",
        TestDataApproval.Unapproved => "Not approved",
        TestDataApproval.Disabled => "Disabled",
        _ => "Expired",
    };

    public static string ApprovalTone(TestDataApproval approval) => approval == TestDataApproval.Approved ? "ready" : "attention";

    /// <summary>The validation fact: the schema result when an XSD is declared, otherwise the format/type validation.</summary>
    public static string ValidationLabel(TestDataAssetSummary asset) => asset.SchemaCheck is { } check
        ? check.Status switch
        {
            TestDataSchemaStatus.Validated => "Valid",
            TestDataSchemaStatus.Invalid => "Invalid",
            TestDataSchemaStatus.SchemaUnavailable => "Schema unavailable",
            TestDataSchemaStatus.DependencyUnresolved => "Dependency unresolved",
            _ => "Not evaluated",
        }
        : asset.Validation switch
        {
            TestDataValidation.Validated => "Valid",
            TestDataValidation.Invalid => "Invalid",
            TestDataValidation.NeedsValidation => "Not validated",
            TestDataValidation.Unavailable => "Unavailable",
            _ => "Not verified",
        };

    /// <summary>Invalid is a fact, not a verdict: for a negative test it is what the fixture is for, so it is never styled as an error there.</summary>
    public static string ValidationTone(TestDataAssetSummary asset) => ValidationLabel(asset) switch
    {
        "Valid" => "ready",
        "Invalid" => IsNegative(asset) ? "neutral" : "attention",
        _ => "neutral",
    };

    public static bool IsNegative(TestDataAssetSummary asset) => asset.Capabilities.Contains(NegativeTestCapability, StringComparer.Ordinal);

    public static string IntentLabel(TestDataAssetSummary asset) =>
        asset.Kind == TestDataKind.ExpectedResult ? "Expected result" : IsNegative(asset) ? "Negative test" : "Positive";

    public static string IntentHelp(TestDataAssetSummary asset) =>
        asset.Kind == TestDataKind.ExpectedResult ? (asset.ExpectsAbsence ? "What a test expects — here: that nothing exists." : "What a test expects to observe.")
        : IsNegative(asset) ? "A deliberately invalid fixture: a test uses it to prove that invalid input is rejected."
        : "A fixture a test uses as valid input.";

    /// <summary>A non-sensitive scalar shows its value; anything else only says whether a value is available — never the value.</summary>
    public static string ValueLabel(TestDataAssetSummary asset) =>
        // A sensitive asset's value is never shown, even if a summary carried one.
        asset.DisplayValue is { } value && asset.Sensitivity != TestDataSensitivity.Sensitive ? value
        : asset.ExpectsAbsence ? "Nothing expected"
        : asset.Sensitivity == TestDataSensitivity.Sensitive ? (asset.ValueAvailable ? "Available securely" : "Not available")
        : asset.ValueAvailable ? "Available (see details)" : "Not available";

    public static string TypeLabel(TestDataAssetSummary asset) =>
        asset.Kind == TestDataKind.Scalar ? asset.ValueType.ToString()
        : asset.Kind == TestDataKind.EntityReference ? asset.EntityType ?? "Entity"
        : asset.ExpectsAbsence ? "Expects absence" : asset.MediaType ?? "—";

    /// <summary>A source's state in its own terms, as its provider words it (a catalog is configured, approved references exist, fixtures are
    /// registered), so "Not configured" is said only of a source that is configured; else the generic label. Unavailable is always the catalog's.</summary>
    public static string SourceStatusLabel(TestDataSourceSummary source) => source.Status == TestDataSourceStatus.Unavailable ? "Unavailable"
        : source.StatusLabel ?? source.Status switch
        {
            TestDataSourceStatus.Available => "Available",
            TestDataSourceStatus.Empty => "Configured · 0 assets",
            _ => "Not configured",
        };

    public static string SourceTone(TestDataSourceSummary source) => source.Status switch
    {
        TestDataSourceStatus.Available => "ready",
        TestDataSourceStatus.Unavailable => "attention",
        _ => "neutral",
    };

    /// <summary>The one real action a source has, by provider id. Sources without a page to go to have none (no dead buttons).</summary>
    public static (string Label, string Href)? SourceAction(TestDataSourceSummary source) => source.Id switch
    {
        "configured" => ("How to configure", UserGuideHref),
        "schema-studio" => ("Open Schema Studio", SchemaStudioHref),
        _ => null,
    };

    public enum EmptyKind { NoSources, ConfiguredButEmpty, SourcesUnavailable }

    /// <summary>Why there are no assets: nothing configured, configured but empty, or a source that could not be read.</summary>
    public static EmptyKind Empty(TestDataCatalogView catalog) =>
        catalog.Sources.Any(source => source.Status == TestDataSourceStatus.Unavailable) && catalog.Sources.All(source => source.Status != TestDataSourceStatus.Empty)
            ? EmptyKind.SourcesUnavailable
            : catalog.Sources.Any(source => source.Status == TestDataSourceStatus.Empty) ? EmptyKind.ConfiguredButEmpty : EmptyKind.NoSources;

    public static string? SourceAnalysisHref(Guid? snapshot) => snapshot is { } id ? $"source-analysis?snapshot={id:D}" : null;

    // ── Catalog view (status, scope, data type, contract, consumers) ─────────────────────────────────────────────────────────────

    public static TestDataAssetStatus Status(TestDataAssetSummary asset) => asset.Status ?? TestDataCatalogRules.Status(asset.Approval, false, null, null, false);

    public static string StatusTone(TestDataAssetStatus status) => status switch
    {
        TestDataAssetStatus.Approved => "ready",
        TestDataAssetStatus.Deprecated => "neutral",
        _ => "attention",
    };

    public static string Scope(TestDataAssetSummary asset) => TestDataCatalogRules.ScopeLabel(asset.Environment);

    public static TestDataDataType DataType(TestDataAssetSummary asset) => asset.DataType ?? TestDataDataTypes.Of(asset);

    public static string ContractLabel(TestDataAssetSummary asset) => TestDataCatalogRules.Label(asset.ContractStatus ?? TestDataContractStatus.NotApplicable);

    public static string ContractTone(TestDataAssetSummary asset) => asset.ContractStatus switch
    {
        TestDataContractStatus.Valid or TestDataContractStatus.Compatible => "ready",
        TestDataContractStatus.NeedsReview or TestDataContractStatus.Invalid => "attention",
        _ => "neutral",
    };

    /// <summary>"Skolenærvær Testing › Rapportmottak" — the consumer's own display hierarchy (Test Data keeps no list of consumers).</summary>
    public static string Consumer(TestDataConsumer consumer) =>
        string.IsNullOrWhiteSpace(consumer.Parent) ? consumer.DisplayName : $"{consumer.Parent} › {consumer.DisplayName}";

    public static string UsedBy(int consumers) => consumers switch { 0 => "Not used", 1 => "Used by 1 consumer", _ => $"Used by {consumers} consumers" };

    /// <summary>A proposed alias from a candidate label ("CreatePersonRequest request body" → "createpersonrequest-request-body"); the person edits it.</summary>
    public static string SuggestAlias(string label)
    {
        var chars = label.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
        var alias = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return alias.Length > 64 ? alias[..64].TrimEnd('-') : alias.Length == 0 ? "asset" : alias;
    }

    /// <summary>Search: alias, label, data type, contract/schema, consumer names, field/parameter names, provenance labels — never values.</summary>
    public static bool Matches(TestDataAssetSummary asset, string? query, IEnumerable<TestDataUsageReference> usages)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var q = query.Trim();
        bool Has(string? text) => text?.Contains(q, StringComparison.OrdinalIgnoreCase) == true;
        return Has(asset.Alias) || Has(asset.Label) || Has(TestDataDataTypes.Label(DataType(asset))) || Has(asset.SchemaRef) || Has(asset.Schema?.SchemaPath) || Has(asset.Source)
            || (asset.Fields ?? []).Any(Has) || asset.Provenance.Any(f => Has(f.Label) || Has(f.Value) && f.Label is "Contract" or "Source" or "Registered as")
            || usages.Where(u => u.AssetId == asset.Id).Any(u => Has(Consumer(u.Consumer)) || Has(u.Usage));
    }
}
