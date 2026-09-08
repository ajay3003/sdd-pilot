namespace BirkNext.Api.Services.ContractAnalysis;

/// <summary>
/// Deterministic contract compatibility analyzer.
/// Producer → Consumer directional semantics.
/// Pure logic, no I/O.
/// </summary>
public interface IContractComparer
{
    ContractCompatibilityResult Compare(
        NormalizedContract producer,
        NormalizedContract consumer,
        string producerService,
        string consumerService,
        string contractName,
        string? producerSource,
        string? consumerSource);
}

public sealed class ContractComparer : IContractComparer
{
    public ContractCompatibilityResult Compare(
        NormalizedContract producer,
        NormalizedContract consumer,
        string producerService,
        string consumerService,
        string contractName,
        string? producerSource,
        string? consumerSource)
    {
        var differences = new List<ContractDifference>();

        // Compare all schemas in producer against consumer expectations
        foreach (var producerSchema in producer.Schemas)
        {
            var consumerSchema = consumer.Schemas.FirstOrDefault(s => s.Name == producerSchema.Name);

            if (consumerSchema == null)
            {
                differences.Add(new ContractDifference
                {
                    Type = ContractDifferenceType.UnsupportedSchema,
                    Path = producerSchema.Name,
                    Severity = ContractDifferenceSeverity.Warning,
                    ProducerValue = "Present",
                    ConsumerValue = "Not defined",
                    Explanation = $"Producer defines schema '{producerSchema.Name}' that consumer does not model"
                });
                continue;
            }

            CompareSchemas(producerSchema, consumerSchema, differences);
        }

        // Determine overall status
        var hasBreaking = differences.Any(d => d.Severity == ContractDifferenceSeverity.Breaking);
        var status = hasBreaking
            ? ContractCompatibilityStatus.Breaking
            : differences.Count > 0
                ? ContractCompatibilityStatus.Warning
                : ContractCompatibilityStatus.Compatible;

        return new ContractCompatibilityResult
        {
            Compatible = !hasBreaking,
            Status = status,
            Producer = producerService,
            Consumer = consumerService,
            Contract = contractName,
            ProducerSource = RedactUrl(producerSource),
            ConsumerSource = RedactUrl(consumerSource),
            Differences = differences.OrderBy(d => d.Path).ThenBy(d => d.Property).ToList(),
            AnalysisReadiness = ContractAnalysisReadiness.Ready,
            Message = status switch
            {
                ContractCompatibilityStatus.Compatible => "Producer and consumer are fully compatible",
                ContractCompatibilityStatus.Warning => $"{differences.Count} non-breaking differences detected",
                ContractCompatibilityStatus.Breaking => $"{differences.Count(d => d.Severity == ContractDifferenceSeverity.Breaking)} breaking differences detected",
                _ => ""
            }
        };
    }

    private void CompareSchemas(
        NormalizedSchema producer,
        NormalizedSchema consumer,
        List<ContractDifference> differences)
    {
        // Compare type
        if (producer.Type != consumer.Type && !IsCompatibleType(producer.Type, consumer.Type))
        {
            differences.Add(new ContractDifference
            {
                Type = ContractDifferenceType.TypeMismatch,
                Path = producer.Name,
                Severity = ContractDifferenceSeverity.Breaking,
                ProducerValue = producer.Type,
                ConsumerValue = consumer.Type,
                Explanation = $"Type mismatch: producer {producer.Type} incompatible with consumer {consumer.Type}"
            });
        }

        // Compare nullability: if producer can emit null but consumer forbids it → BREAKING
        if (producer.Nullable && !consumer.Nullable)
        {
            differences.Add(new ContractDifference
            {
                Type = ContractDifferenceType.NullabilityMismatch,
                Path = producer.Name,
                Severity = ContractDifferenceSeverity.Breaking,
                ProducerValue = "nullable",
                ConsumerValue = "non-null",
                Explanation = "Producer may emit null; consumer does not accept null"
            });
        }

        // Compare enum values: producer values must be subset of consumer
        if (producer.EnumValues != null && consumer.EnumValues != null)
        {
            var producerOnly = producer.EnumValues.Except(consumer.EnumValues).ToList();
            if (producerOnly.Count > 0)
            {
                differences.Add(new ContractDifference
                {
                    Type = ContractDifferenceType.EnumValueMismatch,
                    Path = producer.Name,
                    Severity = ContractDifferenceSeverity.Breaking,
                    ProducerValue = string.Join(", ", producerOnly),
                    ConsumerValue = string.Join(", ", consumer.EnumValues),
                    Explanation = $"Producer can emit enum values ({string.Join(", ", producerOnly)}) not accepted by consumer"
                });
            }
        }

        // Compare properties
        foreach (var producerProp in producer.Properties)
        {
            var consumerProp = consumer.Properties.FirstOrDefault(p => p.Name == producerProp.Name);

            if (consumerProp == null)
            {
                // Producer sends extra field - only breaking if consumer rejects additional properties
                if (consumer.AllowsAdditionalProperties == false)
                {
                    differences.Add(new ContractDifference
                    {
                        Type = ContractDifferenceType.AdditionalProducerProperty,
                        Path = producer.Name,
                        Property = producerProp.Name,
                        Severity = ContractDifferenceSeverity.Breaking,
                        ProducerValue = producerProp.Type,
                        ConsumerValue = "Not defined",
                        Explanation = $"Producer sends field '{producerProp.Name}'; consumer does not accept additional properties"
                    });
                }
                else
                {
                    differences.Add(new ContractDifference
                    {
                        Type = ContractDifferenceType.AdditionalProducerProperty,
                        Path = producer.Name,
                        Property = producerProp.Name,
                        Severity = ContractDifferenceSeverity.Info,
                        ProducerValue = producerProp.Type,
                        ConsumerValue = "Not defined",
                        Explanation = $"Producer sends optional field '{producerProp.Name}' not modeled by consumer"
                    });
                }
                continue;
            }

            CompareProperties(producer.Name, producerProp, consumerProp, differences);
        }

        // Check if consumer requires fields producer doesn't provide
        foreach (var consumerProp in consumer.Properties.Where(p => p.Required))
        {
            var producerProp = producer.Properties.FirstOrDefault(p => p.Name == consumerProp.Name);
            if (producerProp == null)
            {
                differences.Add(new ContractDifference
                {
                    Type = ContractDifferenceType.MissingRequiredProperty,
                    Path = producer.Name,
                    Property = consumerProp.Name,
                    Severity = ContractDifferenceSeverity.Breaking,
                    ProducerValue = "Not provided",
                    ConsumerValue = consumerProp.Type,
                    Explanation = $"Consumer requires field '{consumerProp.Name}'; producer does not provide it"
                });
            }
        }
    }

    private void CompareProperties(
        string schemaName,
        NormalizedProperty producer,
        NormalizedProperty consumer,
        List<ContractDifference> differences)
    {
        var propPath = $"{schemaName}.{producer.Name}";

        // Type mismatch
        if (producer.Type != consumer.Type && !IsCompatibleType(producer.Type, consumer.Type))
        {
            differences.Add(new ContractDifference
            {
                Type = ContractDifferenceType.TypeMismatch,
                Path = propPath,
                Severity = ContractDifferenceSeverity.Breaking,
                ProducerValue = producer.Type,
                ConsumerValue = consumer.Type,
                Explanation = $"Type mismatch: producer {producer.Type} incompatible with consumer {consumer.Type}"
            });
        }

        // Nullability: producer can emit null but consumer forbids → BREAKING
        if (producer.Nullable && !consumer.Nullable)
        {
            differences.Add(new ContractDifference
            {
                Type = ContractDifferenceType.NullabilityMismatch,
                Path = propPath,
                Severity = ContractDifferenceSeverity.Breaking,
                ProducerValue = "nullable",
                ConsumerValue = "non-null",
                Explanation = "Producer may emit null; consumer does not accept null"
            });
        }

        // Requiredness: consumer requires but producer is optional → BREAKING
        if (consumer.Required && !producer.Required)
        {
            differences.Add(new ContractDifference
            {
                Type = ContractDifferenceType.RequirednessMismatch,
                Path = propPath,
                Severity = ContractDifferenceSeverity.Breaking,
                ProducerValue = "optional",
                ConsumerValue = "required",
                Explanation = "Consumer requires field; producer may omit it"
            });
        }

        // Array item type mismatch
        if (producer.ArrayItemType != null && consumer.ArrayItemType != null &&
            producer.ArrayItemType != consumer.ArrayItemType &&
            !IsCompatibleType(producer.ArrayItemType, consumer.ArrayItemType))
        {
            differences.Add(new ContractDifference
            {
                Type = ContractDifferenceType.ArrayItemTypeMismatch,
                Path = propPath,
                Severity = ContractDifferenceSeverity.Breaking,
                ProducerValue = $"array[{producer.ArrayItemType}]",
                ConsumerValue = $"array[{consumer.ArrayItemType}]",
                Explanation = "Array element type mismatch"
            });
        }

        // Enum compatibility
        if (producer.EnumValues != null && consumer.EnumValues != null)
        {
            var producerOnly = producer.EnumValues.Except(consumer.EnumValues).ToList();
            if (producerOnly.Count > 0)
            {
                differences.Add(new ContractDifference
                {
                    Type = ContractDifferenceType.EnumValueMismatch,
                    Path = propPath,
                    Severity = ContractDifferenceSeverity.Breaking,
                    ProducerValue = string.Join(", ", producerOnly),
                    ConsumerValue = string.Join(", ", consumer.EnumValues),
                    Explanation = $"Producer enum contains values ({string.Join(", ", producerOnly)}) not accepted by consumer"
                });
            }
        }
    }

    private static bool IsCompatibleType(string producerType, string consumerType)
    {
        // Define compatible type widening rules
        var compatibilities = new Dictionary<(string, string), bool>
        {
            // Allow common numeric widening
            { ("integer", "number"), true },
            { ("int32", "int64"), true },
            { ("int32", "number"), true },
            { ("float", "double"), true },
        };

        return compatibilities.TryGetValue((producerType, consumerType), out var compatible) && compatible;
    }

    private static string? RedactUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return url;

        try
        {
            var uri = new Uri(url);
            var sensitiveParams = new[] { "token", "key", "auth", "password", "secret", "bearer" };
            var query = uri.Query;

            foreach (var param in sensitiveParams)
            {
                query = System.Text.RegularExpressions.Regex.Replace(
                    query,
                    $@"[?&]{param}=[^&]*",
                    $"?{param}=***",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }

            return uri.Scheme + "://" + uri.Host + uri.AbsolutePath + query;
        }
        catch
        {
            return url;
        }
    }
}
