using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BirkNext.Schemas;

namespace BirkNext.Web.Services.SchemaStudio;

public enum SchemaGenerationProfileKind { GenericValid, Boundary, ControlledInvalid, Minimal, MaximumComplexity }
public enum SchemaGenerationValueMode { SchemaDefault, Synthetic, Boundary, Invalid, FixedEnum, Nil }

/// <summary>Rules contain generation instructions and ranges, never a raw real-world identity or secret.</summary>
public sealed record SchemaGenerationRule(string ElementPath, SchemaGenerationValueMode Mode, decimal? Minimum = null, decimal? Maximum = null,
    int? MinimumLength = null, int? MaximumLength = null, DateOnly? DateFrom = null, DateOnly? DateTo = null,
    int? OccurrenceCount = null);

public sealed record SchemaGenerationProfile(Guid Id, string Name, string SchemaFingerprint, string? ProjectScope,
    SchemaGenerationProfileKind Kind, int Seed, IReadOnlyList<SchemaGenerationRule> Rules,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, bool IsBuiltIn = false);

/// <summary>Schema-bound profile catalog. Callers persist SnapshotJson through the current workspace artifact repository.</summary>
public sealed class SchemaGenerationProfilesService
{
    public const int MaxProfiles = 200;
    public const int MaxRulesPerProfile = 500;
    public const int MaxDocumentBytes = 5 * 1024 * 1024;
    internal const string NilToken = "\u0000schema-studio:nil";
    private readonly Dictionary<Guid, SchemaGenerationProfile> _profiles = [];
    public int RejectedUnboundLegacyProfiles { get; private set; }

    public IReadOnlyList<SchemaGenerationProfile> Profiles => _profiles.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    public static IReadOnlyList<SchemaGenerationProfile> BuiltIns(string schemaFingerprint, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UnixEpoch;
        return new[]
        {
            BuiltIn("Generic valid", SchemaGenerationProfileKind.GenericValid, 4101),
            BuiltIn("Boundary values", SchemaGenerationProfileKind.Boundary, 4102),
            BuiltIn("Controlled invalid values", SchemaGenerationProfileKind.ControlledInvalid, 4103),
            BuiltIn("Minimal XML", SchemaGenerationProfileKind.Minimal, 4104),
            BuiltIn("Maximum complexity", SchemaGenerationProfileKind.MaximumComplexity, 4105),
        };

        SchemaGenerationProfile BuiltIn(string name, SchemaGenerationProfileKind kind, int seed)
        {
            var idBytes = SHA256.HashData(Encoding.UTF8.GetBytes(schemaFingerprint + ":" + kind));
            var id = new Guid(idBytes.AsSpan(0, 16));
            return new(id, name, schemaFingerprint, null, kind, seed, [], at, at, true);
        }
    }

    public SchemaGenerationProfile Create(string name, string schemaFingerprint, string? projectScope, SchemaGenerationProfileKind kind, int seed)
    {
        EnsureCapacity();
        var now = DateTimeOffset.UtcNow;
        var profile = new SchemaGenerationProfile(Guid.NewGuid(), name.Trim(), schemaFingerprint, NormalizeScope(projectScope), kind, seed, [], now, now);
        ValidateProfile(profile);
        _profiles.Add(profile.Id, profile);
        return profile;
    }

    public SchemaGenerationProfile Edit(Guid id, string name, SchemaGenerationProfileKind kind, int seed, IReadOnlyList<SchemaGenerationRule> rules)
    {
        var current = GetEditable(id);
        var updated = current with { Name = name.Trim(), Kind = kind, Seed = seed, Rules = rules.ToArray(), UpdatedAt = DateTimeOffset.UtcNow };
        ValidateProfile(updated);
        _profiles[id] = updated;
        return updated;
    }

    public SchemaGenerationProfile Clone(Guid id, string? newName = null)
    {
        var source = _profiles.TryGetValue(id, out var profile) ? profile : throw new KeyNotFoundException("Generation profile was not found.");
        return Clone(source, newName);
    }

    public SchemaGenerationProfile Clone(SchemaGenerationProfile source, string? newName = null)
    {
        EnsureCapacity();
        var name = string.IsNullOrWhiteSpace(newName) ? source.Name + " copy" : newName.Trim();
        ValidateProfile(source);
        ValidateName(name);
        var now = DateTimeOffset.UtcNow;
        var clone = source with { Id = Guid.NewGuid(), Name = name, Rules = source.Rules.ToArray(), CreatedAt = now, UpdatedAt = now, IsBuiltIn = false };
        _profiles.Add(clone.Id, clone);
        return clone;
    }

    public bool Delete(Guid id) => _profiles.TryGetValue(id, out var profile) && !profile.IsBuiltIn && _profiles.Remove(id);

    public bool IsCompatible(SchemaGenerationProfile profile, SchemaPackage package, string? projectScope = null) =>
        (profile.SchemaFingerprint == package.Fingerprint || profile.IsBuiltIn && profile.SchemaFingerprint == "*")
        && (profile.IsBuiltIn || string.Equals(profile.ProjectScope, NormalizeScope(projectScope), StringComparison.Ordinal));

    public IReadOnlyDictionary<string, string> GenerateValues(SchemaPackage package, SchemaGenerationProfile profile, string? projectScope = null)
    {
        if (!IsCompatible(profile, package, projectScope)) throw new InvalidOperationException("This generation profile belongs to a different schema fingerprint or project scope.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var rules = profile.Rules.ToDictionary(r => r.ElementPath, StringComparer.Ordinal);
        var members = new Dictionary<string, EffectiveMember>(StringComparer.Ordinal);
        foreach (var root in package.EffectiveSchema) Index(root, "/" + root.Name);
        ValidateRulesForSchema(rules.Values, members);
        foreach (var root in package.EffectiveSchema) Visit(root, "/" + root.Name);
        return values;

        void Index(EffectiveMember member, string path)
        {
            members[path] = member;
            foreach (var attribute in member.Attributes ?? [])
                members[path + "/@" + attribute.Name] = new EffectiveMember(attribute.Name, attribute.Type, "1", "1", attribute.DeclaredIn,
                    attribute.InheritedFrom, attribute.TypeDeclaredIn, null, attribute.Facets, [], attribute.NamespaceUri, null,
                    attribute.DefaultValue, attribute.FixedValue, BaseType: attribute.BaseType);
            foreach (var child in member.Children) Index(child, path + "/" + child.Name);
        }

        void Visit(EffectiveMember member, string path)
        {
            if (rules.TryGetValue(path, out var occurrenceRule) && occurrenceRule.OccurrenceCount == 0) return;
            var explicitlyConfigured = rules.Keys.Any(rulePath => rulePath.Equals(path, StringComparison.Ordinal)
                || rulePath.StartsWith(path + "/", StringComparison.Ordinal));
            if (member.MinOccurs == "0" && profile.Kind != SchemaGenerationProfileKind.MaximumComplexity && !explicitlyConfigured)
                return;

            foreach (var attribute in member.Attributes ?? [])
            {
                var attributePath = path + "/@" + attribute.Name;
                var include = attribute.Use == "required" || attribute.DefaultValue is not null || attribute.FixedValue is not null
                    || profile.Kind == SchemaGenerationProfileKind.MaximumComplexity || rules.ContainsKey(attributePath);
                if (!include) continue;
                var attributeModel = members[attributePath];
                rules.TryGetValue(attributePath, out var attributeRule);
                values[attributePath] = Value(attributeModel, attributeRule, profile, attributePath);
            }

            if (member.Children.Count == 0 && !(member.Type is null && member.Attributes is { Count: > 0 }))
            {
                rules.TryGetValue(path, out var rule);
                if (rule?.Mode == SchemaGenerationValueMode.Nil)
                {
                    if (!member.Nillable) throw new InvalidOperationException($"Element {path} is not nillable in this schema.");
                    values[path] = NilToken;
                    return;
                }
                values[path] = Value(member, rule, profile, path);
                return;
            }
            foreach (var child in member.Children) Visit(child, path + "/" + child.Name);
        }
    }

    public string ExportJson(Guid id)
    {
        var profile = _profiles.TryGetValue(id, out var stored) ? stored : BuiltIns("*").FirstOrDefault(p => p.Id == id)
            ?? throw new KeyNotFoundException("Generation profile was not found.");
        return JsonSerializer.Serialize(profile, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    public string ExportJson(SchemaGenerationProfile profile) => JsonSerializer.Serialize(profile, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public SchemaGenerationProfile ImportJson(string json, string? projectScope = null, string? selectedSchemaFingerprint = null)
    {
        if (Encoding.UTF8.GetByteCount(json) > 256 * 1024) throw new InvalidDataException("Profile import exceeds the 256 KB limit.");
        var imported = JsonSerializer.Deserialize<SchemaGenerationProfile>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Profile document is empty.");
        if (imported.SchemaFingerprint == "*")
        {
            if (string.IsNullOrWhiteSpace(selectedSchemaFingerprint) || selectedSchemaFingerprint.Length > 128)
                throw new InvalidDataException("A portable profile must be imported while a schema is selected so it can be bound to that schema.");
            imported = imported with { SchemaFingerprint = selectedSchemaFingerprint };
        }
        ValidateProfile(imported);
        EnsureCapacity();
        var profile = imported with { Id = Guid.NewGuid(), IsBuiltIn = false, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            ProjectScope = NormalizeScope(projectScope) };
        _profiles.Add(profile.Id, profile);
        return profile;
    }

    public string SnapshotJson() => SerializeSnapshot(_profiles.Values);

    public string SnapshotJson(string? projectScope)
    {
        var scope = NormalizeScope(projectScope);
        return SerializeSnapshot(_profiles.Values.Where(profile => profile.ProjectScope == scope));
    }

    public void RestoreJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) { _profiles.Clear(); RejectedUnboundLegacyProfiles = 0; return; }
        if (Encoding.UTF8.GetByteCount(json) > MaxDocumentBytes) throw new InvalidDataException("Saved generation profiles exceed the 5 MB workspace limit.");
        var profiles = JsonSerializer.Deserialize<SchemaGenerationProfile[]>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
        if (profiles.Length > MaxProfiles) throw new InvalidDataException("Saved generation profiles exceed the profile-count limit.");
        if (profiles.Any(profile => profile is null)) throw new InvalidDataException("Saved generation profiles contain an empty entry.");
        if (profiles.Select(profile => profile.Id).Distinct().Count() != profiles.Length)
            throw new InvalidDataException("Saved generation profiles contain duplicate identities.");
        var unboundLegacy = profiles.Where(profile => profile.SchemaFingerprint == "*" && !profile.IsBuiltIn).ToArray();
        var restorable = profiles.Where(profile => profile.SchemaFingerprint != "*" || profile.IsBuiltIn).ToArray();
        foreach (var profile in restorable) ValidateProfile(profile);
        _profiles.Clear();
        foreach (var profile in restorable.Where(p => !p.IsBuiltIn)) _profiles[profile.Id] = profile;
        RejectedUnboundLegacyProfiles = unboundLegacy.Length;
    }

    private static string Value(EffectiveMember member, SchemaGenerationRule? rule, SchemaGenerationProfile profile, string path)
    {
        var random = new Random(StableSeed(profile.Seed, path));
        if (profile.Kind == SchemaGenerationProfileKind.ControlledInvalid && rule?.Mode != SchemaGenerationValueMode.Synthetic)
            return "__invalid_" + random.Next(1000, 9999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (rule?.Mode == SchemaGenerationValueMode.Invalid) return "__invalid_" + random.Next(1000, 9999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (member.FixedValue is not null) return member.FixedValue;
        if (rule?.Mode == SchemaGenerationValueMode.SchemaDefault && member.DefaultValue is not null) return member.DefaultValue;
        if (rule?.Mode == SchemaGenerationValueMode.SchemaDefault && member.DefaultValue is null && member.FixedValue is null)
            throw new InvalidOperationException($"Element {path} has no XSD default or fixed value.");
        if (rule?.Mode == SchemaGenerationValueMode.FixedEnum && !member.Facets.Any(f => f.Kind == "enumeration"))
            throw new InvalidOperationException($"Element {path} has no XSD enumeration constraint.");
        if (member.Facets.FirstOrDefault(f => f.Kind == "enumeration") is not null)
        {
            var enums = member.Facets.Where(f => f.Kind == "enumeration").Select(f => f.Value).Distinct().ToArray();
            return EnsurePatterns(member, enums[random.Next(enums.Length)], path);
        }
        var patterns = member.Facets.Where(f => f.Kind == "pattern").Select(f => f.Value).ToArray();
        var effectiveType = member.BaseType ?? member.Type;
        if (patterns.Length > 0 && IsStringLike(effectiveType))
            return GeneratePatternValue(member, patterns, rule, profile, random, path);
        var minFacet = member.Facets.FirstOrDefault(f => f.Kind is "minInclusive" or "minExclusive");
        var maxFacet = member.Facets.FirstOrDefault(f => f.Kind is "maxInclusive" or "maxExclusive");
        var schemaMinimum = DecimalFacet(minFacet);
        var schemaMaximum = DecimalFacet(maxFacet);
        var (intrinsicMinimum, intrinsicMaximum) = IntrinsicNumericBounds(effectiveType);
        var minimum = new[] { rule?.Minimum, schemaMinimum, intrinsicMinimum }.Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty().Max();
        var hasMinimum = rule?.Minimum.HasValue == true || schemaMinimum.HasValue || intrinsicMinimum.HasValue;
        var maximum = new[] { rule?.Maximum, schemaMaximum, intrinsicMaximum }.Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty().Min();
        var hasMaximum = rule?.Maximum.HasValue == true || schemaMaximum.HasValue || intrinsicMaximum.HasValue;
        decimal? numericMinimum = hasMinimum ? minimum : null;
        decimal? numericMaximum = hasMaximum ? maximum : null;
        if (LocalType(effectiveType) == "dateTime")
            return GenerateDateTime(member, rule, profile, random, path);
        if (LocalType(effectiveType) == "date")
        {
            var from = rule?.DateFrom ?? new DateOnly(2000, 1, 1); var to = rule?.DateTo ?? new DateOnly(2030, 12, 31);
            from = Later(from, DateFacet(member, "minInclusive", exclusive: false));
            from = Later(from, DateFacet(member, "minExclusive", exclusive: true));
            to = Earlier(to, DateFacet(member, "maxInclusive", exclusive: false));
            to = Earlier(to, DateFacet(member, "maxExclusive", exclusive: true));
            if (to < from) throw new InvalidOperationException($"Date range for {path} is reversed.");
            var date = rule?.Mode == SchemaGenerationValueMode.Boundary || profile.Kind == SchemaGenerationProfileKind.Boundary
                ? (random.Next(2) == 0 ? from : to)
                : from.AddDays(to.DayNumber == from.DayNumber ? 0 : random.Next(to.DayNumber - from.DayNumber + 1));
            return EnsurePatterns(member, date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), path);
        }
        if (LocalType(effectiveType) == "boolean") return EnsurePatterns(member, random.Next(2) == 0 ? "true" : "false", path);
        // An unbounded numeric type (xs:double without facets) has no boundary to pick; it uses the regular numeric value below.
        if ((rule?.Mode == SchemaGenerationValueMode.Boundary || profile.Kind == SchemaGenerationProfileKind.Boundary)
            && (numericMinimum is not null || numericMaximum is not null || !IsNumeric(effectiveType)))
        {
            if (numericMinimum is { } lower && numericMaximum is { } upper && lower <= upper)
            {
                var limits = NumericLimits(member, lower, upper, minFacet, maxFacet, rule);
                if (limits.Maximum is null || limits.Minimum > limits.Maximum.Value) throw new InvalidOperationException($"The numeric bounds for {path} contain no representable value.");
                return EnsurePatterns(member, (random.Next(2) == 0 ? limits.Minimum : limits.Maximum.Value).ToString(System.Globalization.CultureInfo.InvariantCulture), path);
            }
            if (numericMinimum is { } min)
            {
                var limits = NumericLimits(member, min, null, minFacet, null, rule);
                return EnsurePatterns(member, limits.Minimum.ToString(System.Globalization.CultureInfo.InvariantCulture), path);
            }
            if (numericMaximum is { } max)
            {
                var limits = NumericLimits(member, null, max, null, maxFacet, rule);
                return EnsurePatterns(member, limits.Maximum!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), path);
            }
            var (minLength, maxLength) = LengthBounds(member, rule);
            var size = minLength is { } ml && maxLength is { } xl ? (random.Next(2) == 0 ? ml : xl) : minLength ?? maxLength ?? 1;
            if (size > 4096) throw new InvalidOperationException($"The minimum string length for {path} exceeds the 4,096-character generation limit.");
            return EnsurePatterns(member, new string('S', Math.Clamp(size, 0, 4096)), path);
        }
        var (minCharsBound, maxCharsBound) = LengthBounds(member, rule);
        var minChars = minCharsBound ?? 1;
        if (minChars > 4096) throw new InvalidOperationException($"The minimum string length for {path} exceeds the 4,096-character generation limit.");
        var maxChars = maxCharsBound ?? Math.Min(minChars + 8, 32);
        if (IsNumeric(effectiveType))
        {
            if (!hasMinimum && !hasMaximum) return EnsurePatterns(member, IsInteger(effectiveType) ? "1" : "1.5", path);
            var limits = NumericLimits(member, numericMinimum, numericMaximum, minFacet, maxFacet, rule);
            if (limits.Maximum is { } max && limits.Minimum > max) throw new InvalidOperationException($"The numeric bounds for {path} contain no representable value.");
            var value = limits.Maximum is null || limits.Minimum == limits.Maximum.Value
                ? limits.Minimum : limits.Minimum + (decimal)random.NextDouble() * (limits.Maximum.Value - limits.Minimum);
            if (IsInteger(effectiveType) && limits.Maximum is { } integerUpper)
                value = Math.Clamp(decimal.Truncate(value), limits.Minimum, integerUpper);
            return EnsurePatterns(member, value.ToString(System.Globalization.CultureInfo.InvariantCulture), path);
        }
        var raw = "Synthetic" + random.Next(1000, 9999).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var length = Math.Clamp(Math.Max(minChars, Math.Min(raw.Length, maxChars)), 0, 4096);
        var synthetic = raw.PadRight(length, 'S')[..length];
        return EnsurePatterns(member, profile.Kind == SchemaGenerationProfileKind.ControlledInvalid ? "!" : synthetic, path);
    }

    private static string GeneratePatternValue(EffectiveMember member, IReadOnlyList<string> patterns, SchemaGenerationRule? rule,
        SchemaGenerationProfile profile, Random random, string path)
    {
        var (minimumLength, maximumLength) = LengthBounds(member, rule);
        var min = minimumLength ?? 0;
        var max = maximumLength ?? 4096;
        if (min > 4096 || max < min) throw new InvalidOperationException($"String constraints for {path} exceed supported generation bounds.");
        for (var attempt = 0; attempt < 24; attempt++)
        {
            if (!XsdPatternValueGenerator.TryCreate(patterns[random.Next(patterns.Count)], random, out var candidate)) continue;
            if (candidate.Length > max) continue;
            var boundary = rule?.Mode == SchemaGenerationValueMode.Boundary || profile.Kind == SchemaGenerationProfileKind.Boundary;
            var targets = boundary
                ? new[] { maximumLength ?? candidate.Length, Math.Max(min, candidate.Length), candidate.Length }.Distinct()
                : new[] { Math.Max(min, candidate.Length) };
            foreach (var targetLength in targets)
            {
                if (targetLength > 4096 || targetLength > max || targetLength < min) continue;
                var sized = candidate.Length < targetLength ? candidate + new string('A', targetLength - candidate.Length) : candidate;
                if (sized.Length != targetLength || !FitsFacets(member, sized, rule)) continue;
                if (patterns.All(pattern => XsdPatternValueGenerator.Matches(pattern, sized))) return sized;
            }
        }
        throw new InvalidOperationException($"Could not generate a value for the XSD pattern constraints at {path}. This pattern may use unsupported XML Schema regex syntax or conflict with the configured length limits.");
    }

    private static string EnsurePatterns(EffectiveMember member, string value, string path)
    {
        var patterns = member.Facets.Where(f => f.Kind == "pattern").Select(f => f.Value).ToArray();
        if (patterns.Length > 0 && !patterns.All(pattern => XsdPatternValueGenerator.Matches(pattern, value)))
            throw new InvalidOperationException($"The generated value for {path} does not satisfy its XSD pattern constraint.");
        return value;
    }
    private static bool FitsFacets(EffectiveMember member, string value, SchemaGenerationRule? rule)
    {
        var (minLength, maxLength) = LengthBounds(member, rule);
        if (minLength is { } min && value.Length < min || maxLength is { } max && value.Length > max) return false;
        if (member.Facets.Where(f => f.Kind == "enumeration").Any() && !member.Facets.Any(f => f.Kind == "enumeration" && f.Value == value)) return false;
        return true;
    }
    private static string GenerateDateTime(EffectiveMember member, SchemaGenerationRule? rule, SchemaGenerationProfile profile, Random random, string path)
    {
        var start = new DateTimeOffset((rule?.DateFrom ?? new DateOnly(2000, 1, 1)).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var end = new DateTimeOffset((rule?.DateTo ?? new DateOnly(2030, 12, 31)).ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc));
        foreach (var facet in member.Facets.Where(f => f.Kind is "minInclusive" or "minExclusive" or "maxInclusive" or "maxExclusive"))
        {
            if (!DateTimeOffset.TryParse(facet.Value, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var bound))
                throw new InvalidOperationException($"XSD dateTime constraint for {path} could not be interpreted.");
            if (facet.Kind == "minInclusive" && bound > start) start = bound;
            else if (facet.Kind == "minExclusive" && bound >= start) start = bound.AddTicks(1);
            else if (facet.Kind == "maxInclusive" && bound < end) end = bound;
            else if (facet.Kind == "maxExclusive" && bound <= end) end = bound.AddTicks(-1);
        }
        if (end < start) throw new InvalidOperationException($"Date range for {path} does not overlap its XSD dateTime constraints.");
        var value = rule?.Mode == SchemaGenerationValueMode.Boundary || profile.Kind == SchemaGenerationProfileKind.Boundary
            ? (random.Next(2) == 0 ? start : end)
            : start.AddTicks(start == end ? 0 : random.NextInt64(end.Ticks - start.Ticks + 1));
        return EnsurePatterns(member, value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture), path);
    }
    private static int? IntFacet(EffectiveMember member, string kind) => int.TryParse(member.Facets.FirstOrDefault(f => f.Kind == kind)?.Value, out var value) ? value : null;
    private static (int? Minimum, int? Maximum) LengthBounds(EffectiveMember member, SchemaGenerationRule? rule)
    {
        var lowerFacets = member.Facets.Where(f => f.Kind is "minLength" or "length").Select(f => int.TryParse(f.Value, out var value) ? value : (int?)null).Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        var upperFacets = member.Facets.Where(f => f.Kind is "maxLength" or "length").Select(f => int.TryParse(f.Value, out var value) ? value : (int?)null).Where(v => v.HasValue).Select(v => v!.Value).ToArray();
        int? schemaMinimum = lowerFacets.Length == 0 ? null : lowerFacets.Max();
        int? schemaMaximum = upperFacets.Length == 0 ? null : upperFacets.Min();
        int? minimum = rule?.MinimumLength is { } requestedMin ? Math.Max(requestedMin, schemaMinimum ?? requestedMin) : schemaMinimum;
        int? maximum = rule?.MaximumLength is { } requestedMax ? Math.Min(requestedMax, schemaMaximum ?? requestedMax) : schemaMaximum;
        if (minimum is { } min && maximum is { } max && min > max) throw new InvalidOperationException($"The configured and XSD length limits for {member.Name} do not overlap.");
        return (minimum, maximum);
    }
    private static bool IsStringLike(string? type) => LocalType(type) is "string" or "normalizedString" or "token" or "language" or "Name" or "NCName" or "NMTOKEN" or "anyURI";
    private static bool IsNumeric(string? type) => LocalType(type) is "decimal" or "float" or "double" or "integer" or "int" or "long" or "short" or "byte" or "positiveInteger" or "nonNegativeInteger" or "negativeInteger" or "nonPositiveInteger" or "unsignedInt" or "unsignedLong" or "unsignedShort" or "unsignedByte";
    private static (decimal? Minimum, decimal? Maximum) IntrinsicNumericBounds(string? type) => LocalType(type) switch
    {
        "positiveInteger" => (1m, null), "nonNegativeInteger" => (0m, null), "unsignedLong" => (0m, ulong.MaxValue),
        "negativeInteger" => (null, -1m), "nonPositiveInteger" => (null, 0m),
        "unsignedByte" => (0m, byte.MaxValue), "unsignedShort" => (0m, ushort.MaxValue),
        "unsignedInt" => (0m, uint.MaxValue), "byte" => (sbyte.MinValue, sbyte.MaxValue),
        "short" => (short.MinValue, short.MaxValue), "int" => (int.MinValue, int.MaxValue),
        "long" => (long.MinValue, long.MaxValue), _ => (null, null)
    };
    private static DateOnly? DateFacet(EffectiveMember member, string kind, bool exclusive)
    {
        var facet = member.Facets.LastOrDefault(f => f.Kind == kind);
        if (facet is null) return null;
        if (LocalType(member.BaseType ?? member.Type) == "dateTime")
        {
            if (!DateTimeOffset.TryParse(facet.Value, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var bound)) return null;
            var day = DateOnly.FromDateTime(bound.UtcDateTime);
            var generatedAtNoon = new DateTimeOffset(day.ToDateTime(new TimeOnly(12), DateTimeKind.Utc));
            var isMinimum = kind.StartsWith("min", StringComparison.Ordinal);
            if (isMinimum ? (exclusive ? generatedAtNoon <= bound : generatedAtNoon < bound)
                : (exclusive ? generatedAtNoon >= bound : generatedAtNoon > bound))
            {
                try { day = day.AddDays(isMinimum ? 1 : -1); }
                catch (ArgumentOutOfRangeException) { return null; }
            }
            return day;
        }
        if (!DateOnly.TryParse(facet.Value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date)) return null;
        if (!exclusive) return date;
        try { return kind.StartsWith("min", StringComparison.Ordinal) ? date.AddDays(1) : date.AddDays(-1); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
    private static DateOnly Later(DateOnly current, DateOnly? constraint) => constraint is { } value && value > current ? value : current;
    private static DateOnly Earlier(DateOnly current, DateOnly? constraint) => constraint is { } value && value < current ? value : current;
    private static decimal? DecimalFacet(SchemaFacet? facet) => facet is not null
        && decimal.TryParse(facet.Value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;
    private static bool IsInteger(string? type) => LocalType(type) is "integer" or "int" or "long" or "short" or "byte" or "positiveInteger" or "nonNegativeInteger" or "negativeInteger" or "nonPositiveInteger" or "unsignedInt" or "unsignedLong" or "unsignedShort" or "unsignedByte";
    private static (decimal Minimum, decimal? Maximum) NumericLimits(EffectiveMember member, decimal? minimum, decimal? maximum,
        SchemaFacet? minimumFacet, SchemaFacet? maximumFacet, SchemaGenerationRule? rule)
    {
        var minimumExclusive = minimumFacet?.Kind == "minExclusive" && (rule?.Minimum is null || DecimalFacet(minimumFacet) == minimum);
        var maximumExclusive = maximumFacet?.Kind == "maxExclusive" && (rule?.Maximum is null || DecimalFacet(maximumFacet) == maximum);
        var lower = minimum ?? 0m;
        var upper = maximum;
        if (minimumExclusive)
        {
            if (IsInteger(member.BaseType ?? member.Type)) lower = decimal.Floor(lower) + 1m;
            else lower += FractionStep(member);
        }
        else if (IsInteger(member.BaseType ?? member.Type)) lower = decimal.Ceiling(lower);
        if (upper is { } upperValue)
        {
            if (maximumExclusive)
            {
                if (IsInteger(member.BaseType ?? member.Type)) upper = decimal.Ceiling(upperValue) - 1m;
                else upper = upperValue - FractionStep(member);
            }
            else if (IsInteger(member.BaseType ?? member.Type)) upper = decimal.Floor(upperValue);
        }
        if (minimum is null && upper is { } onlyUpper) lower = onlyUpper;
        return (lower, upper);
    }
    private static decimal FractionStep(EffectiveMember member)
    {
        var digits = IntFacet(member, "fractionDigits");
        if (digits is >= 0 and <= 27) return 1m / (decimal)Math.Pow(10, digits.Value);
        return 0.000001m;
    }
    private static string? LocalType(string? type) => type?.Split(':').LastOrDefault();
    private static int StableSeed(int seed, string path)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + path));
        return BitConverter.ToInt32(hash, 0) & int.MaxValue;
    }
    private static string? NormalizeScope(string? scope) => string.IsNullOrWhiteSpace(scope) ? null : scope.Trim();
    private void EnsureCapacity() { if (_profiles.Count >= MaxProfiles) throw new InvalidOperationException($"A workspace supports at most {MaxProfiles} custom generation profiles."); }
    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 120) throw new ArgumentException("Profile name must contain 1 to 120 characters.", nameof(name));
        if (name.Any(char.IsControl)) throw new ArgumentException("Profile name contains unsupported characters.", nameof(name));
    }
    private static void ValidateProfile(SchemaGenerationProfile profile)
    {
        if (profile.Id == Guid.Empty || string.IsNullOrWhiteSpace(profile.SchemaFingerprint) || profile.SchemaFingerprint.Length > 128
            || profile.SchemaFingerprint == "*" && !profile.IsBuiltIn
            || profile.SchemaFingerprint != "*" && (profile.SchemaFingerprint.Length != 64 || !profile.SchemaFingerprint.All(Uri.IsHexDigit))
            || profile.ProjectScope is { Length: > 512 } || !Enum.IsDefined(profile.Kind))
            throw new InvalidDataException("Profile identity, schema fingerprint, project scope or kind is invalid.");
        ValidateName(profile.Name);
        ValidateRules(profile.Rules);
        if (profile.Rules.Any(rule => !Enum.IsDefined(rule.Mode)))
            throw new InvalidDataException("Profile contains an unsupported generation mode.");
    }
    private static void ValidateRules(IReadOnlyList<SchemaGenerationRule>? rules)
    {
        if (rules is null || rules.Count > MaxRulesPerProfile) throw new InvalidDataException($"A profile supports at most {MaxRulesPerProfile} rules.");
        if (rules.Any(r => r is null || string.IsNullOrWhiteSpace(r.ElementPath) || r.ElementPath.Length > 1024 || r.ElementPath.Contains("..", StringComparison.Ordinal)
            || r.MinimumLength is < 0 or > 4096 || r.MaximumLength is < 0 or > 4096 || r.DateFrom is { } from && r.DateTo is { } to && to < from
            || r.Minimum is { } min && r.Maximum is { } max && max < min || r.OccurrenceCount is < 0 or > 100))
            throw new InvalidDataException("Profile contains an invalid path or out-of-range generation rule.");
        if (rules.Select(rule => rule.ElementPath).Distinct(StringComparer.Ordinal).Count() != rules.Count)
            throw new InvalidDataException("Profile contains more than one rule for the same element path.");
    }
    private static void ValidateRulesForSchema(IEnumerable<SchemaGenerationRule> rules, IReadOnlyDictionary<string, EffectiveMember> members)
    {
        foreach (var rule in rules)
        {
            if (!members.TryGetValue(rule.ElementPath, out var member)) throw new InvalidOperationException($"Profile rule path {rule.ElementPath} is not present in the selected schema.");
            if (rule.OccurrenceCount is { } occurrenceCount)
            {
                if (rule.ElementPath.Contains("/@", StringComparison.Ordinal))
                    throw new InvalidOperationException($"Occurrence counts apply to elements, not attributes: {rule.ElementPath}.");
                var minimum = int.TryParse(member.MinOccurs, out var minOccurs) ? minOccurs : 1;
                var maximum = member.MaxOccurs == "unbounded" ? 100
                    : int.TryParse(member.MaxOccurs, out var maxOccurs) ? maxOccurs : 1;
                if (occurrenceCount < minimum || occurrenceCount > maximum || occurrenceCount > 100)
                    throw new InvalidOperationException($"Occurrence count for {rule.ElementPath} must be between {minimum} and {Math.Min(maximum, 100)} for this schema.");
            }
            if (member.Children.Count > 0 && (rule.OccurrenceCount is null || rule.Minimum is not null || rule.Maximum is not null
                || rule.MinimumLength is not null || rule.MaximumLength is not null || rule.DateFrom is not null || rule.DateTo is not null
                || rule.Mode != SchemaGenerationValueMode.Synthetic))
                throw new InvalidOperationException($"Complex element rules can only set an occurrence count: {rule.ElementPath}.");
            var effectiveType = member.BaseType ?? member.Type;
            if ((rule.Minimum is not null || rule.Maximum is not null) && !IsNumeric(effectiveType)) throw new InvalidOperationException($"Numeric limits can only be used with a numeric schema type: {rule.ElementPath}.");
            if ((rule.MinimumLength is not null || rule.MaximumLength is not null) && !IsStringLike(effectiveType)) throw new InvalidOperationException($"Length limits can only be used with a string schema type: {rule.ElementPath}.");
            if ((rule.DateFrom is not null || rule.DateTo is not null) && LocalType(effectiveType) is not ("date" or "dateTime")) throw new InvalidOperationException($"Date ranges can only be used with date or dateTime elements: {rule.ElementPath}.");
            if (rule.Mode == SchemaGenerationValueMode.Nil && !member.Nillable) throw new InvalidOperationException($"Element {rule.ElementPath} is not nillable in this schema.");
            if (rule.Mode == SchemaGenerationValueMode.SchemaDefault && member.DefaultValue is null && member.FixedValue is null) throw new InvalidOperationException($"Element {rule.ElementPath} has no XSD default or fixed value.");
            if (rule.Mode == SchemaGenerationValueMode.FixedEnum && !member.Facets.Any(f => f.Kind == "enumeration")) throw new InvalidOperationException($"Element {rule.ElementPath} has no XSD enumeration constraint.");
            _ = LengthBounds(member, rule);
        }
    }

    private static string SerializeSnapshot(IEnumerable<SchemaGenerationProfile> profiles)
    {
        var json = JsonSerializer.Serialize(profiles.ToArray(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (Encoding.UTF8.GetByteCount(json) > MaxDocumentBytes)
            throw new InvalidDataException("Generation profiles exceed the 5 MB workspace limit.");
        return json;
    }

    private SchemaGenerationProfile GetEditable(Guid id) => _profiles.TryGetValue(id, out var profile) && !profile.IsBuiltIn ? profile : throw new InvalidOperationException("Built-in or missing profiles cannot be edited.");
}
