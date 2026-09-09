namespace BirkNext.Api.Services.ContractAnalysis;

/// <summary>
/// Deterministic contract compatibility analyzer.
/// Producer → Consumer directional semantics.
/// Pure logic, no I/O.
/// Supports REST/OpenAPI and GraphQL comparisons.
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

    ContractCompatibilityResult CompareGraphQL(
        GraphQlNormalizedContract producer,
        GraphQlNormalizedContract consumer,
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

        // Collect all required fields (both explicit in Properties and implicit in Required list)
        var consumerRequiredFields = new HashSet<string>(consumer.Required);
        consumerRequiredFields.UnionWith(consumer.Properties.Where(p => p.Required).Select(p => p.Name));

        // Compare properties
        foreach (var producerProp in producer.Properties)
        {
            var consumerProp = consumer.Properties.FirstOrDefault(p => p.Name == producerProp.Name);

            if (consumerProp == null)
            {
                // If producer field matches a required field in consumer (even if not in Properties),
                // don't report as AdditionalProducerProperty
                if (consumerRequiredFields.Contains(producerProp.Name))
                {
                    // Producer provides the required field, which is good
                    continue;
                }

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
        // First check properties in consumer.Properties that are required
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

        // Also check fields in consumer.Required that aren't explicitly in consumer.Properties
        // This indicates a malformed consumer schema (required fields not defined in properties)
        var consumerPropertyNames = new HashSet<string>(consumer.Properties.Select(p => p.Name));
        foreach (var requiredField in consumer.Required.Where(f => !consumerPropertyNames.Contains(f)))
        {
            // Required field exists in consumer.Required but not in consumer.Properties
            // This is a schema integrity issue - report as unsupported
            differences.Add(new ContractDifference
            {
                Type = ContractDifferenceType.UnsupportedSchema,
                Path = consumer.Name,
                Property = requiredField,
                Severity = ContractDifferenceSeverity.Breaking,
                ProducerValue = "Not defined",
                ConsumerValue = "Required but not in schema",
                Explanation = $"Consumer schema is malformed: field '{requiredField}' is marked required but not defined in properties"
            });
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

    public ContractCompatibilityResult CompareGraphQL(
        GraphQlNormalizedContract producer,
        GraphQlNormalizedContract consumer,
        string producerService,
        string consumerService,
        string contractName,
        string? producerSource,
        string? consumerSource)
    {
        var differences = new List<ContractDifference>();

        // Build type lookup maps
        var producerTypeMap = producer.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var consumerTypeMap = consumer.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var producerOperationMap = producer.Operations.ToDictionary(o => $"{o.Kind}:{o.Name}", StringComparer.Ordinal);
        var consumerOperationMap = consumer.Operations.ToDictionary(o => $"{o.Kind}:{o.Name}", StringComparer.Ordinal);

        // First, compare types directly (for schema compatibility testing without operations)
        foreach (var consumerType in consumer.Types)
        {
            if (consumerTypeMap.TryGetValue(consumerType.Name, out var actualConsumerType) &&
                producerTypeMap.TryGetValue(consumerType.Name, out var producerType))
            {
                // For OBJECT types, compare fields
                if (actualConsumerType.Kind == "OBJECT" && producerType.Kind == "OBJECT")
                {
                    CompareGraphQlTypes(actualConsumerType.Name, producerType, actualConsumerType, producerTypeMap, consumerTypeMap, differences, isInput: false);
                }
                // For INPUT_OBJECT types, compare input fields
                else if (actualConsumerType.Kind == "INPUT_OBJECT" && producerType.Kind == "INPUT_OBJECT")
                {
                    CompareGraphQlTypes(actualConsumerType.Name, producerType, actualConsumerType, producerTypeMap, consumerTypeMap, differences, isInput: true);
                }
            }
        }

        // Compare operations that consumer expects
        foreach (var consumerOp in consumer.Operations)
        {
            var key = $"{consumerOp.Kind}:{consumerOp.Name}";
            if (!producerOperationMap.TryGetValue(key, out var producerOp))
            {
                differences.Add(new ContractDifference
                {
                    Type = ContractDifferenceType.MissingOperation,
                    Path = consumerOp.RootType,
                    Operation = consumerOp.Name,
                    Severity = ContractDifferenceSeverity.Breaking,
                    ProducerValue = "Missing",
                    ConsumerValue = consumerOp.Kind,
                    Explanation = $"Consumer expects {consumerOp.Kind} operation '{consumerOp.Name}'; producer does not provide it"
                });
                continue;
            }

            CompareGraphQlOperations(consumerOp, producerOp, producerTypeMap, consumerTypeMap, differences);
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
            Differences = differences.OrderBy(d => d.Operation).ThenBy(d => d.Path).ThenBy(d => d.Property).ToList(),
            AnalysisReadiness = ContractAnalysisReadiness.Ready,
            Message = status switch
            {
                ContractCompatibilityStatus.Compatible => "Producer and consumer GraphQL schemas are compatible",
                ContractCompatibilityStatus.Warning => $"{differences.Count} non-breaking GraphQL differences detected",
                ContractCompatibilityStatus.Breaking => $"{differences.Count(d => d.Severity == ContractDifferenceSeverity.Breaking)} breaking GraphQL differences detected",
                _ => ""
            }
        };
    }

    private void CompareGraphQlOperations(
        GraphQlOperation consumerOp,
        GraphQlOperation producerOp,
        Dictionary<string, GraphQlType> producerTypeMap,
        Dictionary<string, GraphQlType> consumerTypeMap,
        List<ContractDifference> differences)
    {
        var opPath = $"{consumerOp.RootType}.{consumerOp.Name}";

        // Compare return type compatibility
        if (consumerOp.ReturnType != null && producerOp.ReturnType != null)
        {
            var (consumerTypeName, _, _) = consumerOp.ReturnType.Unwrap();
            var (producerTypeName, _, _) = producerOp.ReturnType.Unwrap();

            if (consumerTypeMap.TryGetValue(consumerTypeName, out var consumerType) &&
                producerTypeMap.TryGetValue(producerTypeName, out var producerType))
            {
                CompareGraphQlTypes(opPath, producerType, consumerType, producerTypeMap, consumerTypeMap, differences, isInput: false);
            }
        }

        // Compare arguments: consumer arguments must be satisfiable by producer
        foreach (var consumerArg in consumerOp.Arguments)
        {
            var producerArg = producerOp.Arguments.FirstOrDefault(a => a.Name == consumerArg.Name);

            if (producerArg == null)
            {
                // Consumer requires argument that producer doesn't provide
                if (!HasDefaultValue(consumerArg.DefaultValue))
                {
                    differences.Add(new ContractDifference
                    {
                        Type = ContractDifferenceType.MissingOperation,
                        Path = opPath,
                        Property = consumerArg.Name,
                        Severity = ContractDifferenceSeverity.Breaking,
                        ProducerValue = "Missing",
                        ConsumerValue = "Required",
                        Explanation = $"Operation argument '{consumerArg.Name}' required by consumer is not provided by producer"
                    });
                }
                continue;
            }

            CompareGraphQlArguments(opPath, producerArg, consumerArg, differences);
        }

        // Producer may add optional arguments → non-breaking
        foreach (var producerArg in producerOp.Arguments)
        {
            if (!consumerOp.Arguments.Any(a => a.Name == producerArg.Name))
            {
                if (!HasDefaultValue(producerArg.DefaultValue))
                {
                    // Producer requires argument that consumer doesn't send
                    differences.Add(new ContractDifference
                    {
                        Type = ContractDifferenceType.RequirednessMismatch,
                        Path = opPath,
                        Property = producerArg.Name,
                        Severity = ContractDifferenceSeverity.Breaking,
                        ProducerValue = "Required by producer",
                        ConsumerValue = "Not sent by consumer",
                        Explanation = $"Producer requires argument '{producerArg.Name}' that consumer does not provide"
                    });
                }
            }
        }
    }

    private void CompareGraphQlArguments(
        string operationPath,
        GraphQlArgument producer,
        GraphQlArgument consumer,
        List<ContractDifference> differences)
    {
        if (producer.Type == null || consumer.Type == null)
            return;

        var producerUnwrapped = producer.Type.Unwrap();
        var consumerUnwrapped = consumer.Type.Unwrap();

        // For arguments (input): producer nullability is more permissive
        // If producer requires (non-null) but consumer wants optional → BREAKING
        if (!producerUnwrapped.IsNonNull && consumerUnwrapped.IsNonNull)
        {
            differences.Add(new ContractDifference
            {
                Type = ContractDifferenceType.RequirednessMismatch,
                Path = operationPath,
                Property = producer.Name,
                Severity = ContractDifferenceSeverity.Breaking,
                ProducerValue = "Nullable",
                ConsumerValue = "Non-null required",
                Explanation = $"Producer argument '{producer.Name}' is nullable but consumer expects it to be non-null"
            });
        }

        // Type compatibility for arguments
        if (producerUnwrapped.TypeName != consumerUnwrapped.TypeName &&
            !IsGraphQlScalarCompatible(producerUnwrapped.TypeName, consumerUnwrapped.TypeName))
        {
            differences.Add(new ContractDifference
            {
                Type = ContractDifferenceType.TypeMismatch,
                Path = operationPath,
                Property = producer.Name,
                Severity = ContractDifferenceSeverity.Breaking,
                ProducerValue = producerUnwrapped.TypeName,
                ConsumerValue = consumerUnwrapped.TypeName,
                Explanation = $"Argument type mismatch for '{producer.Name}'"
            });
        }
    }

    private void CompareGraphQlTypes(
        string path,
        GraphQlType producer,
        GraphQlType consumer,
        Dictionary<string, GraphQlType> producerTypeMap,
        Dictionary<string, GraphQlType> consumerTypeMap,
        List<ContractDifference> differences,
        bool isInput)
    {
        // Compare fields (output types) or input fields (input types)
        var producerFields = isInput ? (producer.InputFields ?? []) : producer.Fields;
        var consumerFields = isInput ? (consumer.InputFields ?? []) : consumer.Fields;

        if (isInput)
        {
            // Input type: compare fields that consumer sends
            foreach (var consumerField in consumerFields)
            {
                var producerField = producerFields.FirstOrDefault(f => f.Name == consumerField.Name);

                if (producerField == null)
                {
                    differences.Add(new ContractDifference
                    {
                        Type = ContractDifferenceType.MissingOperation,
                        Path = path,
                        Property = consumerField.Name,
                        Severity = ContractDifferenceSeverity.Breaking,
                        ProducerValue = "Missing",
                        ConsumerValue = "Defined",
                        Explanation = $"Input field '{consumerField.Name}' used by consumer not available in producer type"
                    });
                    continue;
                }

                CompareGraphQlFields(path, producerField, consumerField, isInput, differences);
            }
        }
        else
        {
            // Output type: compare fields that consumer requests
            foreach (var consumerField in consumerFields)
            {
                var producerField = producerFields.FirstOrDefault(f => f.Name == consumerField.Name);

                if (producerField == null)
                {
                    differences.Add(new ContractDifference
                    {
                        Type = ContractDifferenceType.MissingRequiredProperty,
                        Path = path,
                        Property = consumerField.Name,
                        Severity = ContractDifferenceSeverity.Breaking,
                        ProducerValue = "Missing",
                        ConsumerValue = "Required",
                        Explanation = $"Output field '{consumerField.Name}' expected by consumer not provided by producer"
                    });
                    continue;
                }

                CompareGraphQlFields(path, producerField, consumerField, isInput, differences);
            }
        }
    }

    private void CompareGraphQlFields(
        string path,
        GraphQlField producer,
        GraphQlField consumer,
        bool isInput,
        List<ContractDifference> differences)
    {
        if (producer.Type == null || consumer.Type == null)
            return;

        var fieldPath = $"{path}.{producer.Name}";
        var producerUnwrapped = producer.Type.Unwrap();
        var consumerUnwrapped = consumer.Type.Unwrap();

        if (isInput)
        {
            // Input field nullability: producer nullable but consumer expects non-null → BREAKING
            if (!producerUnwrapped.IsNonNull && consumerUnwrapped.IsNonNull)
            {
                differences.Add(new ContractDifference
                {
                    Type = ContractDifferenceType.RequirednessMismatch,
                    Path = fieldPath,
                    Severity = ContractDifferenceSeverity.Breaking,
                    ProducerValue = "Nullable",
                    ConsumerValue = "Non-null",
                    Explanation = $"Input field '{producer.Name}' is nullable in producer but required in consumer"
                });
            }
        }
        else
        {
            // Output field nullability: producer nullable but consumer expects non-null → BREAKING
            if (!producerUnwrapped.IsNonNull && consumerUnwrapped.IsNonNull)
            {
                differences.Add(new ContractDifference
                {
                    Type = ContractDifferenceType.NullabilityMismatch,
                    Path = fieldPath,
                    Severity = ContractDifferenceSeverity.Breaking,
                    ProducerValue = "Nullable",
                    ConsumerValue = "Non-null required",
                    Explanation = $"Output field '{producer.Name}' may be null in producer but consumer expects non-null"
                });
            }
        }

        // Type mismatch
        if (producerUnwrapped.TypeName != consumerUnwrapped.TypeName &&
            !IsGraphQlScalarCompatible(producerUnwrapped.TypeName, consumerUnwrapped.TypeName))
        {
            differences.Add(new ContractDifference
            {
                Type = ContractDifferenceType.TypeMismatch,
                Path = fieldPath,
                Severity = ContractDifferenceSeverity.Breaking,
                ProducerValue = producerUnwrapped.TypeName,
                ConsumerValue = consumerUnwrapped.TypeName,
                Explanation = $"Field type mismatch: producer {producerUnwrapped.TypeName} vs consumer {consumerUnwrapped.TypeName}"
            });
        }
    }

    private static bool HasDefaultValue(string? defaultValue)
    {
        return !string.IsNullOrEmpty(defaultValue) && defaultValue != "null";
    }

    private static bool IsGraphQlScalarCompatible(string producerScalar, string consumerScalar)
    {
        if (producerScalar == consumerScalar)
            return true;

        // Define compatible scalar conversions
        var compatibilities = new Dictionary<(string, string), bool>
        {
            { ("Int", "Float"), true },
            { ("Int", "String"), false },
            { ("String", "ID"), false },
            { ("ID", "String"), false },
        };

        return compatibilities.TryGetValue((producerScalar, consumerScalar), out var compatible) && compatible;
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
