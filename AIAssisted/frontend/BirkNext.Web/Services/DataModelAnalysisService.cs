using System.Text.RegularExpressions;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public sealed class DataModelAnalysisService : IDataModelAnalysisService
{
    // ── Parse ──────────────────────────────────────────────────────────────────

    public DataModelDocument Parse(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return new DataModelDocument();

        var title          = "Data Model";
        var titleSet       = false;
        var overviewLines  = new List<string>();
        var migrationLines = new List<string>();
        var retentionLines = new List<string>();

        var entities      = new List<DataEntity>();
        var relationships = new List<DataRelationship>();
        var indexes       = new List<DataIndex>();
        var constraints   = new List<DataConstraint>();
        var enums         = new List<DataEnum>();

        // Mutable builder state
        string?         currentEntityName        = null;
        bool            currentEntityIsTable      = false;
        string?         currentEntityDesc         = null;
        var             currentColumns            = new List<DataColumn>();
        var             currentEntityRelations    = new List<DataRelationship>();
        var             currentEntityIndexes      = new List<DataIndex>();
        var             currentEntityConstraints  = new List<DataConstraint>();
        var             currentTraceIds           = new List<string>();
        string?         currentEnumName           = null;
        string?         currentEnumDesc           = null;
        var             currentEnumValues         = new List<string>();
        var             currentSection            = string.Empty;
        var             currentEntitySectionKind = string.Empty; // "persistent", "runtime", etc.
        var             globalRelationships       = new List<DataRelationship>();
        var             globalIndexes             = new List<DataIndex>();
        var             globalConstraints         = new List<DataConstraint>();

        void FlushEntity()
        {
            if (currentEntityName is null) return;
            entities.Add(new DataEntity
            {
                Name            = currentEntityName,
                IsTable         = currentEntityIsTable,
                Description     = string.IsNullOrWhiteSpace(currentEntityDesc) ? null : currentEntityDesc.Trim(),
                Columns         = new List<DataColumn>(currentColumns),
                TraceabilityIds = new List<string>(currentTraceIds),
            });
            globalRelationships.AddRange(currentEntityRelations);
            globalIndexes.AddRange(currentEntityIndexes);
            globalConstraints.AddRange(currentEntityConstraints);
            currentEntityName       = null;
            currentEntityIsTable    = false;
            currentEntityDesc       = null;
            currentColumns.Clear();
            currentEntityRelations.Clear();
            currentEntityIndexes.Clear();
            currentEntityConstraints.Clear();
            currentTraceIds.Clear();
            currentSection = string.Empty;
        }

        void FlushEnum()
        {
            if (currentEnumName is null) return;
            enums.Add(new DataEnum
            {
                Name        = currentEnumName,
                Values      = currentEnumValues.Select(v => NormalizeStructuredName(v)).ToList(),
                Description = string.IsNullOrWhiteSpace(currentEnumDesc) ? null : currentEnumDesc.Trim(),
            });
            currentEnumName   = null;
            currentEnumDesc   = null;
            currentEnumValues.Clear();
            currentSection = string.Empty;
        }

        var tokens = MarkdownTokenizer.Tokenize(markdown);

        foreach (var token in tokens)
        {
            // ── Skip blank lines ───────────────────────────────────────────────

            if (token.Kind == MarkdownTokenKind.Blank)
                continue;

            // ── Headings ───────────────────────────────────────────────────────

            if (token.Kind == MarkdownTokenKind.Heading)
            {
                if (token.HeadingLevel == 1)
                {
                    if (!titleSet) { title = token.Content; titleSet = true; }
                    continue;
                }

                if (token.HeadingLevel == 2)
                {
                    FlushEntity();
                    FlushEnum();

                    var h2 = token.Content;

                    if (h2.StartsWith("Entity:", StringComparison.OrdinalIgnoreCase))
                    {
                        currentEntityName    = NormalizeStructuredName(h2[7..].Trim());
                        currentEntityIsTable = false;
                        currentSection       = string.Empty;
                        currentEntitySectionKind = string.Empty;
                    }
                    else if (h2.StartsWith("Table:", StringComparison.OrdinalIgnoreCase))
                    {
                        currentEntityName    = NormalizeStructuredName(h2[6..].Trim());
                        currentEntityIsTable = true;
                        currentSection       = string.Empty;
                        currentEntitySectionKind = string.Empty;
                    }
                    else if (h2.StartsWith("Enum:", StringComparison.OrdinalIgnoreCase))
                    {
                        currentEnumName = NormalizeStructuredName(h2[5..].Trim());
                        currentSection  = "enum";
                        currentEntitySectionKind = string.Empty;
                    }
                    else if (h2.Equals("Overview", StringComparison.OrdinalIgnoreCase))
                    {
                        currentSection = "overview";
                        currentEntitySectionKind = string.Empty;
                    }
                    else if (h2.StartsWith("Migration", StringComparison.OrdinalIgnoreCase))
                    {
                        currentSection = "migration";
                        currentEntitySectionKind = string.Empty;
                    }
                    else if (h2.StartsWith("Retention", StringComparison.OrdinalIgnoreCase))
                    {
                        currentSection = "retention";
                        currentEntitySectionKind = string.Empty;
                    }
                    else if (h2.Equals("Relationships", StringComparison.OrdinalIgnoreCase))
                    {
                        currentSection = "relationships";
                        currentEntitySectionKind = string.Empty;
                    }
                    else if (h2.Equals("Indexes", StringComparison.OrdinalIgnoreCase) ||
                             h2.Equals("Indices", StringComparison.OrdinalIgnoreCase))
                    {
                        currentSection = "indexes";
                        currentEntitySectionKind = string.Empty;
                    }
                    else if (h2.Equals("Constraints", StringComparison.OrdinalIgnoreCase))
                    {
                        currentSection = "constraints";
                        currentEntitySectionKind = string.Empty;
                    }
                    else if (IsEntitySectionHeading(h2))
                    {
                        currentSection = "entity-section";
                        currentEntitySectionKind = ClassifyEntitySection(h2);
                    }
                    else
                    {
                        currentSection = string.Empty;
                        currentEntitySectionKind = string.Empty;
                    }
                    continue;
                }

                if (token.HeadingLevel == 3)
                {
                    var h3 = token.Content;

                    // List of subsection headings that should not be treated as entity names
                    var isSubsectionHeading = h3.Equals("Columns", StringComparison.OrdinalIgnoreCase) ||
                                              h3.Equals("Fields", StringComparison.OrdinalIgnoreCase) ||
                                              h3.Equals("Properties", StringComparison.OrdinalIgnoreCase) ||
                                              h3.Equals("Relationships", StringComparison.OrdinalIgnoreCase) ||
                                              h3.Equals("Indexes", StringComparison.OrdinalIgnoreCase) ||
                                              h3.Equals("Indices", StringComparison.OrdinalIgnoreCase) ||
                                              h3.Equals("Constraints", StringComparison.OrdinalIgnoreCase) ||
                                              h3.Equals("Traceability", StringComparison.OrdinalIgnoreCase) ||
                                              h3.Equals("Open Questions", StringComparison.OrdinalIgnoreCase) ||
                                              h3.Equals("EF Configuration", StringComparison.OrdinalIgnoreCase) ||
                                              h3.Equals("Notes", StringComparison.OrdinalIgnoreCase) ||
                                              h3.Equals("Invariants", StringComparison.OrdinalIgnoreCase) ||
                                              h3.Equals("Seed Data", StringComparison.OrdinalIgnoreCase) ||
                                              h3.Equals("Source Reference", StringComparison.OrdinalIgnoreCase) ||
                                              h3.Equals("Validation rules", StringComparison.OrdinalIgnoreCase) ||
                                              h3.Equals("Design notes", StringComparison.OrdinalIgnoreCase);

                    // Check if this is an entity name within a persistent-entities or other entity sections
                    // Do this check BEFORE subsection name checks so entity names take precedence
                    if (!string.IsNullOrEmpty(currentEntitySectionKind) &&
                        !isSubsectionHeading &&
                        !NumericH3Re.IsMatch(h3))
                    {
                        // Handle H3 entities in entity-section contexts
                        // Pattern: "### EntityName" or "### EntityName — tableName description"
                        FlushEntity();
                        FlushEnum();

                        var (entityName, tableName) = ExtractEntityAndTableName(h3);
                        currentEntityName    = NormalizeStructuredName(entityName);
                        currentEntityIsTable = !string.IsNullOrEmpty(tableName);

                        // If a table name was extracted, set it as a description hint
                        if (!string.IsNullOrEmpty(tableName))
                            currentEntityDesc = $"Table: {NormalizeStructuredName(tableName)}";

                        // Continue processing subsections
                        currentSection = string.Empty;
                    }
                    else if (h3.Equals("Columns", StringComparison.OrdinalIgnoreCase))
                        currentSection = "columns";
                    else if (h3.Equals("Fields", StringComparison.OrdinalIgnoreCase) ||
                             h3.Equals("Properties", StringComparison.OrdinalIgnoreCase))
                        currentSection = "columns";
                    else if (h3.Equals("Relationships", StringComparison.OrdinalIgnoreCase))
                        currentSection = "relationships";
                    else if (h3.Equals("Indexes", StringComparison.OrdinalIgnoreCase) ||
                             h3.Equals("Indices", StringComparison.OrdinalIgnoreCase))
                        currentSection = "indexes";
                    else if (h3.Equals("Constraints", StringComparison.OrdinalIgnoreCase))
                        currentSection = "constraints";
                    else if (h3.Equals("Traceability", StringComparison.OrdinalIgnoreCase))
                        currentSection = "traceability";
                    else if (h3.Equals("Open Questions", StringComparison.OrdinalIgnoreCase))
                        currentSection = string.Empty;
                    else if (NumericH3Re.IsMatch(h3))
                    {
                        // "### 2.1 Person", "### 3.4 SikkerhetsnivaaType", etc.
                        FlushEntity();
                        FlushEnum();
                        currentEntityName    = NormalizeStructuredName(StripNumericPrefix(h3));
                        currentEntityIsTable = false;
                        currentSection       = string.Empty;
                        currentEntitySectionKind = string.Empty;
                    }
                    else
                        currentSection = string.Empty;
                    continue;
                }

                // h4+ — silently ignored (original does the same)
                continue;
            }

            // ── Bold property labels (**Table**: Name, **Indexes**:, etc.) ──────
            //    Intercept before section dispatch so labels work in any context.

            if (token.Kind == MarkdownTokenKind.Text)
            {
                var pm = InlinePropRe.Match(token.Content);
                if (pm.Success)
                {
                    var keyLow = pm.Groups[1].Value.Trim().ToLowerInvariant();
                    var val    = pm.Groups[2].Value.Trim();

                    switch (keyLow)
                    {
                        case "table":
                        {
                            var tableName = NormalizeStructuredName(CleanEntityName(val));
                            if (!string.IsNullOrWhiteSpace(tableName))
                            {
                                // H3 may have already opened an entity — reuse it
                                if (currentEntityName is null) { FlushEntity(); FlushEnum(); }
                                currentEntityName    = tableName;
                                currentEntityIsTable = true;
                                currentSection       = "columns";
                            }
                            continue;
                        }
                        case "indexes":
                        case "indices":
                        case "index":
                            if (currentEntityName is not null)
                                currentSection = "indexes";
                            continue;
                        default:
                            // Any other bold label (Invariants, Seed data, State machine, …)
                            // terminates schema-parsing sections so their following content
                            // isn't treated as columns or indexes.
                            if (currentSection is "columns" or "indexes")
                            {
                                currentSection = string.Empty;
                                continue;
                            }
                            break;
                    }
                }
            }

            // ── Entity description (any non-heading, non-blank content before
            //    the first subsection, matching the original line-based check) ──

            if (currentEntityName is not null && currentSection == string.Empty)
            {
                currentEntityDesc = (currentEntityDesc ?? "") + " " + token.RawLine.Trim();
                continue;
            }

            // ── Enum description (non-bullet text before enum values) ──────────

            if (currentSection == "enum" && token.Kind != MarkdownTokenKind.BulletItem)
            {
                currentEnumDesc = (currentEnumDesc ?? "") + " " + token.RawLine.Trim();
                continue;
            }

            // ── Section content ────────────────────────────────────────────────

            // Auto-detect column tables after entities with no explicit section
            if (currentEntityName is not null && currentSection == string.Empty &&
                token.Kind == MarkdownTokenKind.TableRow && token.TableCells is not null)
            {
                if (IsColumnHeaderRow(token.TableCells))
                {
                    currentSection = "columns";
                    // Continue to fall through to columns case
                }
            }

            switch (currentSection)
            {
                case "overview":
                    overviewLines.Add(token.RawLine.TrimEnd());
                    break;

                case "migration":
                    migrationLines.Add(token.RawLine.TrimEnd());
                    break;

                case "retention":
                    retentionLines.Add(token.RawLine.TrimEnd());
                    break;

                case "columns":
                    if (token.Kind == MarkdownTokenKind.TableRow &&
                        !IsColumnHeaderRow(token.TableCells!))
                    {
                        var col = ParseColumnRow(token.TableCells!);
                        if (col is not null)
                        {
                            currentColumns.Add(col);

                            // Extract FK relationship from Constraints column (index 3)
                            if (token.TableCells!.Count > 3 && currentEntityName is not null)
                            {
                                var fkM = FkArrowRe.Match(token.TableCells![3]);
                                if (fkM.Success)
                                    currentEntityRelations.Add(new DataRelationship
                                    {
                                        Source           = $"{currentEntityName}.{col.Name}",
                                        Target           = NormalizeStructuredName(fkM.Groups[1].Value.Trim()),
                                        RelationshipType = "FK",
                                    });
                            }

                            // Extract traceability refs from Notes and Constraints columns using RefIdRe.
                            var lastCell = token.TableCells!.Count - 1;
                            for (var ci = Math.Max(0, token.TableCells!.Count - 2); ci <= lastCell; ci++)
                            {
                                foreach (var refM in MarkdownHelpers.RefIdRe.Matches(token.TableCells![ci]).Cast<Match>())
                                {
                                    var rid = refM.Value.ToUpperInvariant();
                                    if (!currentTraceIds.Contains(rid))
                                        currentTraceIds.Add(rid);
                                }
                            }
                        }
                    }
                    break;

                case "relationships":
                    if (token.Kind == MarkdownTokenKind.BulletItem)
                    {
                        var rel = ParseRelationshipLine(token.Content, currentEntityName);
                        if (rel is not null)
                        {
                            if (currentEntityName is not null)
                                currentEntityRelations.Add(rel);
                            else
                                globalRelationships.Add(rel);
                        }
                    }
                    break;

                case "indexes":
                    if (token.Kind == MarkdownTokenKind.BulletItem)
                    {
                        var idx = ParseIndexLine(token.Content, currentEntityName ?? string.Empty);
                        if (idx is not null)
                        {
                            if (currentEntityName is not null)
                                currentEntityIndexes.Add(idx);
                            else
                                globalIndexes.Add(idx);
                        }
                    }
                    break;

                case "constraints":
                    if (token.Kind == MarkdownTokenKind.BulletItem)
                    {
                        var con = ParseConstraintLine(token.Content, currentEntityName ?? string.Empty);
                        if (con is not null)
                        {
                            if (currentEntityName is not null)
                                currentEntityConstraints.Add(con);
                            else
                                globalConstraints.Add(con);
                        }
                    }
                    break;

                case "traceability":
                    if (token.Kind == MarkdownTokenKind.BulletItem &&
                        currentEntityName is not null &&
                        !string.IsNullOrWhiteSpace(token.Content))
                    {
                        currentTraceIds.Add(token.Content);
                    }
                    break;

                case "enum":
                    if (token.Kind == MarkdownTokenKind.BulletItem &&
                        !string.IsNullOrWhiteSpace(token.Content))
                    {
                        currentEnumValues.Add(token.Content);
                    }
                    break;
            }
        }

        FlushEntity();
        FlushEnum();

        relationships.AddRange(globalRelationships);
        indexes.AddRange(globalIndexes);
        constraints.AddRange(globalConstraints);

        var findings = GenerateFindings(entities, relationships, indexes,
            overviewLines.Count > 0, migrationLines.Count > 0);

        return new DataModelDocument
        {
            Title           = title,
            Overview        = overviewLines.Count > 0 ? string.Join(" ", overviewLines) : null,
            MigrationNotes  = migrationLines.Count > 0 ? string.Join(" ", migrationLines) : null,
            RetentionPolicy = retentionLines.Count > 0 ? string.Join(" ", retentionLines) : null,
            Entities        = entities,
            Relationships   = relationships,
            Indexes         = indexes,
            Constraints     = constraints,
            Enums           = enums,
            Findings        = findings,
        };
    }

    // ── Parsing helpers ────────────────────────────────────────────────────────

    private static bool IsColumnHeaderRow(IReadOnlyList<string> cells)
    {
        if (cells.Count == 0) return false;
        var first = cells[0].Trim().ToLowerInvariant();
        if (first is "name" or "column" or "field" or "column name" or "field name" or "property")
            return true;

        var cellsLower = cells.Select(c => c.Trim().ToLowerInvariant()).ToList();

        // Header must have name/column/field AND (type OR description)
        var hasNameCol = cellsLower.Any(c => c is "name" or "column" or "field" or "property" or "column name" or "field name");
        var hasTypeCol = cellsLower.Any(c => c is "type" or "sql type");
        var hasDescCol = cellsLower.Any(c => c is "description" or "notes" or "required" or "nullable" or "constraints");

        return hasNameCol && (hasTypeCol || hasDescCol);
    }

    private static DataColumn? ParseColumnRow(IReadOnlyList<string> cells)
    {
        if (cells.Count < 2) return null;

        var name = NormalizeStructuredName(cells[0]);
        if (string.IsNullOrWhiteSpace(name)) return null;

        var type        = cells.Count > 1 ? NullIfEmpty(NormalizeStructuredName(cells[1])) : null;
        var nullableRaw = cells.Count > 2 ? cells[2].ToLowerInvariant() : null;
        var desc        = cells.Count > 3 ? NullIfEmpty(cells[3]) : null;

        bool? nullable = nullableRaw switch
        {
            "yes" or "true" or "y" or "null" or "nullable"    => true,
            "no"  or "false" or "n" or "not null" or "not_null" or "required" => false,
            _ => null,
        };

        var descLow  = (desc ?? "").ToLowerInvariant();
        var nameLow  = name.ToLowerInvariant();
        var isPk     = descLow.Contains("primary key") || descLow.Contains("pk") ||
                       nameLow == "id" || nameLow.EndsWith(".id");
        var isFk     = descLow.Contains("foreign key") || descLow.Contains("fk") ||
                       (nameLow.EndsWith("_id") && nameLow.Length > 3);
        var isUnique = descLow.Contains("unique");

        return new DataColumn
        {
            Name         = name,
            Type         = type,
            Nullable     = nullable,
            IsPrimaryKey = isPk,
            IsForeignKey = isFk,
            IsUnique     = isUnique,
            Description  = desc,
        };
    }

    private static DataRelationship? ParseRelationshipLine(string text, string? defaultEntity)
    {
        var arrowIdx = text.IndexOf("->", StringComparison.Ordinal);
        if (arrowIdx < 0) return null;

        var source = NormalizeStructuredName(text[..arrowIdx].Trim());
        var rest   = NormalizeStructuredName(text[(arrowIdx + 2)..].Trim());

        // Optional type in parentheses: "users.id (many-to-one)"
        string? relType = null;
        var parenOpen = rest.IndexOf('(');
        var parenClose = rest.IndexOf(')');
        if (parenOpen >= 0 && parenClose > parenOpen)
        {
            relType = rest[(parenOpen + 1)..parenClose].Trim();
            rest    = rest[..parenOpen].Trim();
        }

        var target = rest;

        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
            return null;

        // If source has no dot and we're inside an entity block, prefix it
        if (!source.Contains('.') && defaultEntity is not null)
            source = defaultEntity + "." + source;

        return new DataRelationship
        {
            Source           = source,
            Target           = target,
            RelationshipType = relType,
        };
    }

    private static DataIndex? ParseIndexLine(string text, string entityName)
    {
        // Strip backtick quoting from index names like `IX_Person_EksternId`
        text = NormalizeStructuredName(text).Trim();

        // Reject obvious prose (must start with recognized index patterns)
        if (!IsValidIndexSignature(text))
            return null;

        // Extract metadata flags
        var isUnique = text.Contains("(unique)", StringComparison.OrdinalIgnoreCase) ||
                       text.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase);

        // Strip "Composite:" prefix if present (for composite indexes)
        var working = text;
        if (working.StartsWith("composite:", StringComparison.OrdinalIgnoreCase))
            working = working[10..].Trim();

        // Extract canonical index name (up to first metadata marker: space+parens, " on ", " for ")
        var metadataIdx = FindFirstMetadataMarker(working);
        string name;

        if (metadataIdx > 0)
            name = working[..metadataIdx].Trim();
        else
            name = working.Trim();

        // Check for "on (col1, col2)" pattern for extracting columns
        var onIdx = working.IndexOf(" on ", StringComparison.OrdinalIgnoreCase);
        var cols = new List<string>();

        if (onIdx > 0)
        {
            var afterOn = working[(onIdx + 4)..];

            // Extract columns from (col1, col2) or from col1, col2
            var parenIdx = afterOn.IndexOf('(');
            var parenEndIdx = afterOn.LastIndexOf(')');

            string columnsPart;
            if (parenIdx >= 0 && parenEndIdx > parenIdx)
                columnsPart = afterOn.Substring(parenIdx + 1, parenEndIdx - parenIdx - 1);
            else
                columnsPart = afterOn;

            cols = columnsPart.Split(',', StringSplitOptions.TrimEntries)
                             .Where(c => !string.IsNullOrEmpty(c) && c.Trim().Length > 0)
                             .Select(c => NormalizeStructuredName(c.Trim().TrimEnd(')')))
                             .Where(c => c.Length > 0)
                             .ToList();
        }

        return new DataIndex
        {
            Name       = name,
            EntityName = entityName,
            Columns    = cols,
            IsUnique   = isUnique,
        };
    }

    private static bool IsValidIndexSignature(string text)
    {
        // Must start with recognized index patterns; reject arbitrary prose
        // Valid: IX_*, Unique index *, Full-text index *, Composite: IX_*
        var lower = text.ToLowerInvariant().Trim();
        return lower.StartsWith("ix_") ||
               lower.StartsWith("composite:") ||
               lower.StartsWith("full-text") ||
               lower.StartsWith("unique index") ||
               lower.StartsWith("clustered");
    }

    private static int FindFirstMetadataMarker(string text)
    {
        // Find where the index name ends and metadata begins
        // Markers: " (" for (non-clustered, ...) or " on " for column list
        var spaceParenIdx = text.IndexOf(" (");
        var onIdx = text.IndexOf(" on ", StringComparison.OrdinalIgnoreCase);
        var forIdx = text.IndexOf(" for ", StringComparison.OrdinalIgnoreCase);

        var candidates = new[] { spaceParenIdx, onIdx, forIdx }
            .Where(i => i > 0)
            .ToList();

        return candidates.Count > 0 ? candidates.Min() : -1;
    }

    private static DataConstraint? ParseConstraintLine(string text, string entityName)
    {
        // "FK_name: definition" or "PK_name: definition"
        var colonIdx = text.IndexOf(':');
        if (colonIdx < 0)
            return new DataConstraint { Name = NormalizeStructuredName(text), EntityName = entityName, ConstraintType = "CK" };

        var name       = NormalizeStructuredName(text[..colonIdx].Trim());
        var definition = text[(colonIdx + 1)..].Trim();
        var typePart   = name.ToUpperInvariant();

        var constraintType =
            typePart.StartsWith("FK") ? "FK" :
            typePart.StartsWith("PK") ? "PK" :
            typePart.StartsWith("UQ") || typePart.StartsWith("UX") ? "UQ" :
            "CK";

        return new DataConstraint
        {
            Name           = name,
            EntityName     = entityName,
            ConstraintType = constraintType,
            Definition     = NullIfEmpty(definition),
        };
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // Strips a leading "N.M " or "N.M.P " numeric prefix from a heading.
    private static string StripNumericPrefix(string heading)
    {
        var m = Regex.Match(heading, @"^\d+(?:\.\d+)*\s+");
        return m.Success ? heading[m.Length..].Trim() : heading.Trim();
    }

    // Cleans a raw **Table**: value — strips backticks, drops " — ..." suffixes.
    private static string CleanEntityName(string raw)
    {
        var s = raw.Replace("`", "").Trim();
        var i = s.IndexOf(" — "); // " — "
        if (i > 0) s = s[..i].Trim();
        i = s.IndexOf(" - ");
        if (i > 0) s = s[..i].Trim();
        return s;
    }

    // Normalizes structured field names by removing inline-code (backtick) syntax.
    // Handles both fully-backticked identifiers and inline backticks within text.
    // Examples:
    //   `UserName` → UserName
    //   Source Reference (`KildeReferanse`) Format → Source Reference (KildeReferanse) Format
    //   ScimListResponse<T> → ScimListResponse<T> (unchanged)
    //   PlainName → PlainName (unchanged)
    private static string NormalizeStructuredName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;

        var s = raw.Trim();

        // Replace backtick pairs with the content between them (removes the backticks)
        // Pattern: `text` → text
        // This handles both fully-wrapped identifiers and inline-code within longer text
        s = System.Text.RegularExpressions.Regex.Replace(s, @"`([^`]*)`", "$1");

        return s;
    }

    // Extracts entity name and table name from H3 format like "### EntityName" or "### EntityName — tableName description"
    private static (string EntityName, string? TableName) ExtractEntityAndTableName(string heading)
    {
        var s = NormalizeStructuredName(heading.Trim());

        // First, handle backticked names like "### `birk_tiltak`" or "### `birk_tiltak`, `birk_tiltakstype`"
        // For comma-separated backticked names, take the first one as the entity name
        // (Note: backticks should already be removed by NormalizeStructuredName, but keep logic for safety)
        if (s.StartsWith("`"))
        {
            var closeIdx = s.IndexOf("`", 1);
            if (closeIdx > 0)
            {
                var tableName = s[1..closeIdx].Trim();
                return (tableName, tableName);  // Use table name as entity name
            }
        }

        // Check for "EntityName — tableName" pattern
        var emdashIdx = s.IndexOf(" — "); // " — "
        if (emdashIdx > 0)
        {
            var entityName = s[..emdashIdx].Trim();
            var rest = s[(emdashIdx + 3)..].Trim();

            // Extract table name — it's typically the first word(s) before "table" keyword or space
            // Pattern: "### FaultQueueEntry — feilkoe table" → table name is "feilkoe"
            var tableName = rest;
            var tableKeywordIdx = rest.IndexOf(" table", StringComparison.OrdinalIgnoreCase);
            if (tableKeywordIdx > 0)
                tableName = rest[..tableKeywordIdx].Trim();
            else
            {
                // No "table" keyword, take first word (could be backticked)
                var spaceIdx = rest.IndexOf(' ');
                if (spaceIdx > 0)
                    tableName = rest[..spaceIdx].Trim();
            }

            // Clean backticks from table name (should already be normalized, but keep for safety)
            tableName = tableName.Replace("`", "").Trim();

            return (entityName, tableName);
        }

        // No table name, just entity name
        return (s, null);
    }

    // ── Findings ───────────────────────────────────────────────────────────────

    private static List<DataModelFinding> GenerateFindings(
        List<DataEntity>       entities,
        List<DataRelationship> relationships,
        List<DataIndex>        indexes,
        bool                   hasOverview,
        bool                   hasMigrationNotes)
    {
        var findings = new List<DataModelFinding>();
        var entityNames = new HashSet<string>(
            entities.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);

        if (entities.Count == 0)
        {
            // If no entities found, the document may be stateless or configuration-only
            // Don't show a hard error; use INFO instead to indicate the document was parsed successfully
            // but contains no persistent data model
            findings.Add(new DataModelFinding
            {
                Severity    = DataModelSeverity.Info,
                Category    = "Structure",
                Description = "No persistent entities or tables were found. This document may describe configuration, runtime models, or be intentionally stateless.",
            });
            return findings;
        }

        foreach (var entity in entities)
        {
            if (entity.Columns.Count == 0)
            {
                findings.Add(new DataModelFinding
                {
                    Severity    = DataModelSeverity.Warning,
                    Category    = "Schema",
                    Description = $"No columns defined.",
                    EntityName  = entity.Name,
                });
                continue;
            }

            var hasPk = entity.Columns.Any(c => c.IsPrimaryKey);
            if (!hasPk)
            {
                findings.Add(new DataModelFinding
                {
                    Severity    = DataModelSeverity.Warning,
                    Category    = "Schema",
                    Description = "No primary key column identified.",
                    EntityName  = entity.Name,
                });
            }

            foreach (var col in entity.Columns)
            {
                if (string.IsNullOrWhiteSpace(col.Type))
                {
                    findings.Add(new DataModelFinding
                    {
                        Severity    = DataModelSeverity.Warning,
                        Category    = "Schema",
                        Description = $"Column \"{col.Name}\" is missing a type.",
                        EntityName  = entity.Name,
                    });
                }

                if (col.Nullable is null && !col.IsPrimaryKey)
                {
                    findings.Add(new DataModelFinding
                    {
                        Severity    = DataModelSeverity.Info,
                        Category    = "Schema",
                        Description = $"Column \"{col.Name}\" does not specify nullable/not-null.",
                        EntityName  = entity.Name,
                    });
                }

                // FK column without a defined relationship
                if (col.IsForeignKey)
                {
                    var colNameLow = col.Name.ToLowerInvariant();
                    var hasRel = relationships.Any(r =>
                        r.SourceEntity.Equals(entity.Name, StringComparison.OrdinalIgnoreCase) &&
                        (r.SourceColumn.Equals(col.Name, StringComparison.OrdinalIgnoreCase) ||
                         r.Source.Equals($"{entity.Name}.{col.Name}", StringComparison.OrdinalIgnoreCase)));

                    if (!hasRel)
                    {
                        findings.Add(new DataModelFinding
                        {
                            Severity    = DataModelSeverity.Warning,
                            Category    = "Relationships",
                            Description = $"Column \"{col.Name}\" looks like a foreign key but no relationship is defined for it.",
                            EntityName  = entity.Name,
                        });
                    }
                }

                if (IsColumnSensitive(col))
                {
                    var descLow    = (col.Description ?? "").ToLowerInvariant();
                    var classified = descLow.Contains("pii")         || descLow.Contains("gdpr")        ||
                                     descLow.Contains("hipaa")       || descLow.Contains("classified")  ||
                                     descLow.Contains("sensitive")   || descLow.Contains("encrypted")   ||
                                     descLow.Contains("hashed")      || descLow.Contains("anonymized")  ||
                                     descLow.Contains("masked");
                    if (!classified)
                    {
                        findings.Add(new DataModelFinding
                        {
                            Severity    = DataModelSeverity.Warning,
                            Category    = "Security",
                            Description = $"Column \"{col.Name}\" appears to contain sensitive data but has no classification annotation.",
                            EntityName  = entity.Name,
                        });
                    }
                }
            }

            if (entity.TraceabilityIds.Count == 0)
            {
                findings.Add(new DataModelFinding
                {
                    Severity    = DataModelSeverity.Warning,
                    Category    = "Traceability",
                    Description = "No requirement IDs linked.",
                    EntityName  = entity.Name,
                });
            }
        }

        // Relationships referencing undefined entities
        foreach (var rel in relationships)
        {
            if (!entityNames.Contains(rel.SourceEntity))
            {
                findings.Add(new DataModelFinding
                {
                    Severity    = DataModelSeverity.Warning,
                    Category    = "Relationships",
                    Description = $"Relationship source \"{rel.SourceEntity}\" is not a defined entity.",
                });
            }
            if (!entityNames.Contains(rel.TargetEntity))
            {
                findings.Add(new DataModelFinding
                {
                    Severity    = DataModelSeverity.Warning,
                    Category    = "Relationships",
                    Description = $"Relationship target \"{rel.TargetEntity}\" is not a defined entity.",
                });
            }
        }

        // No indexes at all
        if (indexes.Count == 0 && entities.Count > 0)
        {
            findings.Add(new DataModelFinding
            {
                Severity    = DataModelSeverity.Info,
                Category    = "Performance",
                Description = "No indexes are defined. Consider adding indexes for foreign key and frequently queried columns.",
            });
        }

        if (!hasOverview)
        {
            findings.Add(new DataModelFinding
            {
                Severity    = DataModelSeverity.Info,
                Category    = "Documentation",
                Description = "No ## Overview section found.",
            });
        }

        if (!hasMigrationNotes)
        {
            findings.Add(new DataModelFinding
            {
                Severity    = DataModelSeverity.Info,
                Category    = "Documentation",
                Description = "No ## Migration Notes section found.",
            });
        }

        return findings;
    }

    // ── Entity section detection ───────────────────────────────────────────────

    private static bool IsEntitySectionHeading(string heading)
    {
        var h = NormalizeSectionHeading(heading);
        return PersistentEntitySections.Contains(h, StringComparer.OrdinalIgnoreCase) ||
               NonPersistentEntitySections.Contains(h, StringComparer.OrdinalIgnoreCase);
    }

    private static string ClassifyEntitySection(string heading)
    {
        var h = NormalizeSectionHeading(heading);
        if (PersistentEntitySections.Contains(h, StringComparer.OrdinalIgnoreCase))
            return "persistent";
        if (NonPersistentEntitySections.Contains(h, StringComparer.OrdinalIgnoreCase))
            return "runtime";
        return string.Empty;
    }

    // Normalize section headings by removing parenthetical suffixes
    // "In-Transit Objects (not persisted by adapter)" → "In-Transit Objects"
    private static string NormalizeSectionHeading(string heading)
    {
        var h = heading.Trim();
        var parenIdx = h.IndexOf('(');
        if (parenIdx > 0)
            h = h[..parenIdx].Trim();
        return h;
    }

    private static readonly HashSet<string> PersistentEntitySections = new(StringComparer.OrdinalIgnoreCase)
    {
        "Domain Entities",
        "Persistent Entities",
        "Core Entities",
        "Core Tables",
        "Reference Tables",
        "Staging Entities",
        "Infrastructure Entity",
        "Outbox Tables",
        "Entities"
    };

    private static readonly HashSet<string> NonPersistentEntitySections = new(StringComparer.OrdinalIgnoreCase)
    {
        "Configuration Entities",
        "Runtime Context Structures",
        "In-Transit Objects",
        "Event Contracts",
        "Events",
        "SCIM Request/Response Models",
        "Frontend-Only View Models",
        "Domain Interface",
        "Infrastructure Implementation",
        "Derived Value",
        "Non-Entities"
    };

    // ── Document-structure patterns ───────────────────────────────────────────

    // Matches H3 headings with numeric prefix, e.g. "2.1 Person", "3.4 SikkerhetsnivaaType"
    private static readonly Regex NumericH3Re = new(
        @"^\d+(?:\.\d+)*\s+\S", RegexOptions.Compiled);

    // Matches "**Label**: value" or "**Label**:" (colon outside the bold span)
    private static readonly Regex InlinePropRe = new(
        @"^\*\*([^*]+)\*\*\s*:?\s*(.*)$", RegexOptions.Compiled);

    // Matches "FK → Target", "FK -> Target", "FK —> Target"
    private static readonly Regex FkArrowRe = new(
        @"\bFK\s*(?:→|->|—>)\s*(\w+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Sensitive column detection ─────────────────────────────────────────────

    private static readonly string[] SensitiveKeywords =
    [
        "password", "passwd", "pwd", "passphrase",
        "ssn", "social_security",
        "email",
        "phone", "mobile", "telephone",
        "credit_card", "card_num", "card_number", "cvv", "cvc",
        "api_key", "api_secret", "secret_key", "private_key",
        "access_token", "refresh_token", "auth_token", "session_token",
        "dob", "date_of_birth", "birthdate", "birth_date",
        "salary", "income", "wage",
        "ip_address", "ip_addr",
        "passport", "national_id",
    ];

    private static bool IsColumnSensitive(DataColumn column)
    {
        var nameLow = column.Name.ToLowerInvariant();
        return SensitiveKeywords.Any(kw => nameLow.Contains(kw));
    }

    public bool IsSensitiveColumn(DataColumn column) => IsColumnSensitive(column);

    // ── Filter helpers ─────────────────────────────────────────────────────────

    public IEnumerable<DataEntity> FilterEntities(IEnumerable<DataEntity> entities, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return entities;
        var q = query.Trim().ToLowerInvariant();
        return entities.Where(e =>
            e.Name.ToLowerInvariant().Contains(q) ||
            (e.Description?.ToLowerInvariant().Contains(q) ?? false) ||
            e.Columns.Any(c => c.Name.ToLowerInvariant().Contains(q) ||
                               (c.Type?.ToLowerInvariant().Contains(q) ?? false)));
    }

    public IEnumerable<DataRelationship> FilterRelationships(IEnumerable<DataRelationship> rels, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return rels;
        var q = query.Trim().ToLowerInvariant();
        return rels.Where(r =>
            r.Source.ToLowerInvariant().Contains(q) ||
            r.Target.ToLowerInvariant().Contains(q) ||
            (r.RelationshipType?.ToLowerInvariant().Contains(q) ?? false));
    }

    public IEnumerable<DataIndex> FilterIndexes(IEnumerable<DataIndex> indexes, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return indexes;
        var q = query.Trim().ToLowerInvariant();
        return indexes.Where(i =>
            i.Name.ToLowerInvariant().Contains(q) ||
            i.EntityName.ToLowerInvariant().Contains(q) ||
            i.Columns.Any(c => c.ToLowerInvariant().Contains(q)));
    }

    public IEnumerable<DataConstraint> FilterConstraints(IEnumerable<DataConstraint> constraints, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return constraints;
        var q = query.Trim().ToLowerInvariant();
        return constraints.Where(c =>
            c.Name.ToLowerInvariant().Contains(q) ||
            c.EntityName.ToLowerInvariant().Contains(q) ||
            c.ConstraintType.ToLowerInvariant().Contains(q) ||
            (c.Definition?.ToLowerInvariant().Contains(q) ?? false));
    }

    // ── Build Semantic Model ───────────────────────────────────────────────

    public static DataModelSemanticModel BuildSemanticModel(DataModelDocument document)
    {
        var entities = document.Entities
            .Select(e => new SemanticDataEntity
            {
                Id = e.Name,
                Name = e.Name,
                Description = e.Description,
                Attributes = e.Columns
                    .Select(c => new SemanticDataAttribute
                    {
                        Name = c.Name,
                        Type = c.Type ?? "Unknown",
                        IsRequired = !(c.Nullable ?? true),
                        IsIdentifier = c.IsPrimaryKey,
                        Description = c.Description,
                        Constraint = c.IsForeignKey ? "ForeignKey" : (c.IsUnique ? "Unique" : null),
                    })
                    .ToList(),
                IdentifierFields = e.Columns
                    .Where(c => c.IsPrimaryKey)
                    .Select(c => c.Name)
                    .ToList(),
                ValidationRules = e.Columns
                    .Where(c => !string.IsNullOrEmpty(c.Description))
                    .Select(c => $"{c.Name}: {c.Description}")
                    .ToList(),
                Methods = [],
                Lifecycle = null,
                RelatedTraceabilityIds = e.TraceabilityIds,
                RelationshipIds = document.Relationships
                    .Where(r => r.SourceEntity == e.Name || r.TargetEntity == e.Name)
                    .Select(r => $"{r.Source}->{r.Target}")
                    .ToList(),
            })
            .ToList();

        var relationships = document.Relationships
            .Select((r, idx) => new SemanticDataRelationship
            {
                Id = $"Rel-{idx + 1}",
                SourceEntityId = r.SourceEntity,
                TargetEntityId = r.TargetEntity,
                Type = r.RelationshipType ?? "Unknown",
                Cardinality = null,
                IsBidirectional = false,
                Description = $"{r.Source} -> {r.Target}",
            })
            .ToList();

        var enumerations = document.Enums
            .Select(e => new SemanticDataEnumeration
            {
                Id = e.Name,
                Name = e.Name,
                Description = e.Description,
                Values = e.Values
                    .Select((v, idx) => new SemanticDataEnumerationValue
                    {
                        Name = v,
                        Value = idx.ToString(),
                        Description = null,
                    })
                    .ToList(),
                UsedByEntityIds = [],
            })
            .ToList();

        return new DataModelSemanticModel
        {
            Title = document.Title,
            Version = null,
            Description = document.Overview,
            CreatedDate = null,
            LastUpdated = null,
            Entities = entities,
            Relationships = relationships,
            Enumerations = enumerations,
            ValueObjects = [],
            AggregateRoots = [],
            EntityToTraceability = BuildEntityToTraceabilityMap(entities),
        };
    }

    private static Dictionary<string, List<string>> BuildEntityToTraceabilityMap(List<SemanticDataEntity> entities)
    {
        var map = new Dictionary<string, List<string>>();
        foreach (var entity in entities)
        {
            if (entity.RelatedTraceabilityIds.Count > 0)
                map[entity.Id] = entity.RelatedTraceabilityIds;
        }
        return map;
    }
}
