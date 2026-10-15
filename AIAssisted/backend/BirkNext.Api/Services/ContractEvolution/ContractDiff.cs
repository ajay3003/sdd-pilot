using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.ContractEvolution;

/// <summary>
/// Structural differences between two normalized snapshots of one contract, by stable path. A removed path and an added path stay a removal
/// and an addition: renames are never inferred from names or types. Uses the shared <see cref="ContractChangeClass"/> taxonomy.
/// </summary>
public static class ContractDiff
{
    public static List<ContractChange> Compare(ContractSnapshot previous, ContractSnapshot current)
    {
        var changes = new List<ContractChange>();
        if (previous.Fingerprint == current.Fingerprint) return changes;
        var namespaceChanged = !string.Equals(previous.Namespace, current.Namespace, StringComparison.Ordinal);
        if (namespaceChanged)
            changes.Add(new(ContractChangeClass.NamespaceChanged, "", previous.Namespace, current.Namespace,
                $"Namespace changed: {previous.Namespace ?? "(none)"} → {current.Namespace ?? "(none)"}. Same member names in another namespace are not assumed equivalent."));
        if (!string.Equals(previous.Descriptor.Version, current.Descriptor.Version, StringComparison.Ordinal))
            changes.Add(new(ContractChangeClass.VersionChanged, "", previous.Descriptor.Version, current.Descriptor.Version,
                $"Declared version changed: {previous.Descriptor.Version ?? "(not declared)"} → {current.Descriptor.Version ?? "(not declared)"}."));

        var before = previous.Nodes.ToDictionary(node => node.Path, StringComparer.Ordinal);
        var after = current.Nodes.ToDictionary(node => node.Path, StringComparer.Ordinal);
        foreach (var node in current.Nodes.Where(node => !before.ContainsKey(node.Path)))
            changes.Add(new(node.Required ? ContractChangeClass.FieldAddedRequired : ContractChangeClass.FieldAddedOptional, node.Path, null, Describe(node),
                $"Added {(node.Required ? "required" : "optional")} {node.Kind} {node.Path}."));
        foreach (var node in previous.Nodes.Where(node => !after.ContainsKey(node.Path)))
            changes.Add(new(ContractChangeClass.FieldRemoved, node.Path, Describe(node), null, $"Removed {node.Kind} {node.Path}."));
        foreach (var node in current.Nodes.Where(node => before.ContainsKey(node.Path)))
        {
            var old = before[node.Path];
            if (!string.Equals(old.DataType, node.DataType, StringComparison.Ordinal))
                changes.Add(new(ContractChangeClass.TypeChanged, node.Path, old.DataType, node.DataType, $"{node.Path}: type {old.DataType ?? "(none)"} → {node.DataType ?? "(none)"}."));
            if (old.Required != node.Required)
                changes.Add(new(ContractChangeClass.RequirednessChanged, node.Path, old.Required ? "required" : "optional", node.Required ? "required" : "optional",
                    $"{node.Path}: {(old.Required ? "required" : "optional")} → {(node.Required ? "required" : "optional")}."));
            else if (!string.Equals(old.Cardinality, node.Cardinality, StringComparison.Ordinal))
                changes.Add(new(ContractChangeClass.CardinalityChanged, node.Path, old.Cardinality, node.Cardinality, $"{node.Path}: occurrence [{old.Cardinality}] → [{node.Cardinality}]."));
            foreach (var value in node.EnumValues.Except(old.EnumValues, StringComparer.Ordinal))
                changes.Add(new(ContractChangeClass.EnumValueAdded, node.Path, null, value, $"{node.Path}: value \"{value}\" added. It is never mapped to an existing value automatically."));
            foreach (var value in old.EnumValues.Except(node.EnumValues, StringComparer.Ordinal))
                changes.Add(new(ContractChangeClass.EnumValueRemoved, node.Path, value, null, $"{node.Path}: value \"{value}\" removed."));
            if (!namespaceChanged && !string.Equals(old.Namespace, node.Namespace, StringComparison.Ordinal))
                changes.Add(new(ContractChangeClass.NamespaceChanged, node.Path, old.Namespace, node.Namespace, $"{node.Path}: namespace {old.Namespace ?? "(none)"} → {node.Namespace ?? "(none)"}."));
        }

        // Order matters only among members both snapshots have, under the same parent (an XSD sequence); an insertion alone is not a reorder.
        foreach (var parent in current.Nodes.Where(node => node.Position > 0).GroupBy(node => Parent(node.Path), StringComparer.Ordinal))
        {
            var common = parent.Where(node => before.TryGetValue(node.Path, out var old) && old.Position > 0).Select(node => node.Path).ToHashSet(StringComparer.Ordinal);
            if (common.Count < 2) continue;
            var oldOrder = previous.Nodes.Where(node => common.Contains(node.Path)).OrderBy(node => node.Position).Select(node => node.Path).ToList();
            var newOrder = current.Nodes.Where(node => common.Contains(node.Path)).OrderBy(node => node.Position).Select(node => node.Path).ToList();
            if (!oldOrder.SequenceEqual(newOrder, StringComparer.Ordinal))
                changes.Add(new(ContractChangeClass.OrderChanged, parent.Key, string.Join(", ", oldOrder), string.Join(", ", newOrder), $"{(parent.Key.Length == 0 ? "(root)" : parent.Key)}: member order changed."));
        }
        if (changes.Count == 0)
            changes.Add(new(ContractChangeClass.None, "", previous.Fingerprint, current.Fingerprint, "The fingerprint changed but no structural difference was classified."));
        return changes;
    }

    public static string Parent(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";

    private static string Describe(ContractNode node) =>
        $"{node.Kind} {node.DataType ?? "(no type)"} [{node.Cardinality}]{(node.EnumValues.Count > 0 ? $" {{{string.Join(", ", node.EnumValues)}}}" : "")}";
}
