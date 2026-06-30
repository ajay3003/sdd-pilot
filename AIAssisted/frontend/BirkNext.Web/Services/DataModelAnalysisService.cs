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
                Values      = new List<string>(currentEnumValues),
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
                        currentEntityName    = h2[7..].Trim();
                        currentEntityIsTable = false;
                        currentSection       = string.Empty;
                    }
                    else if (h2.StartsWith("Table:", StringComparison.OrdinalIgnoreCase))
                    {
                        currentEntityName    = h2[6..].Trim();
                        currentEntityIsTable = true;
                        currentSection       = string.Empty;
                    }
                    else if (h2.StartsWith("Enum:", StringComparison.OrdinalIgnoreCase))
                    {
                        currentEnumName = h2[5..].Trim();
                        currentSection  = "enum";
                    }
                    else if (h2.Equals("Overview", StringComparison.OrdinalIgnoreCase))
                    {
                        currentSection = "overview";
                    }
                    else if (h2.StartsWith("Migration", StringComparison.OrdinalIgnoreCase))
                    {
                        currentSection = "migration";
                    }
                    else if (h2.StartsWith("Retention", StringComparison.OrdinalIgnoreCase))
                    {
                        currentSection = "retention";
                    }
                    else if (h2.Equals("Relationships", StringComparison.OrdinalIgnoreCase))
                    {
                        currentSection = "relationships";
                    }
                    else if (h2.Equals("Indexes", StringComparison.OrdinalIgnoreCase) ||
                             h2.Equals("Indices", StringComparison.OrdinalIgnoreCase))
                    {
                        currentSection = "indexes";
                    }
                    else if (h2.Equals("Constraints", StringComparison.OrdinalIgnoreCase))
                    {
                        currentSection = "constraints";
                    }
                    else
                    {
                        currentSection = string.Empty;
                    }
                    continue;
                }

                if (token.HeadingLevel == 3)
                {
                    var h3 = token.Content;

                    if (h3.Equals("Columns", StringComparison.OrdinalIgnoreCase))
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
                        currentEntityName    = StripNumericPrefix(h3);
                        currentEntityIsTable = false;
                        currentSection       = string.Empty;
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
                            var tableName = CleanEntityName(val);
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
                                        Target           = fkM.Groups[1].Value.Trim(),
                                        RelationshipType = "FK",
                                    });
                            }

                            // Extract traceability refs from Notes column (index 4) and
                            // Constraints column (index 3) using the shared engine's RefIdRe.
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
        var first = cells[0].ToLowerInvariant();
        if (first is "name" or "column" or "field" or "column name" or "field name")
            return true;
        return cells.Any(c => c.Equals("name",     StringComparison.OrdinalIgnoreCase)) &&
               cells.Any(c => c.Equals("type",     StringComparison.OrdinalIgnoreCase)) &&
               cells.Any(c => c.Equals("nullable", StringComparison.OrdinalIgnoreCase));
    }

    private static DataColumn? ParseColumnRow(IReadOnlyList<string> cells)
    {
        if (cells.Count < 2) return null;

        var name = cells[0];
        if (string.IsNullOrWhiteSpace(name)) return null;

        var type        = cells.Count > 1 ? NullIfEmpty(cells[1]) : null;
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

        var source = text[..arrowIdx].Trim();
        var rest   = text[(arrowIdx + 2)..].Trim();

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
        text = text.Replace("`", "").Trim();

        // "IX_profiles_email on email (unique)" or "IX_name on col1, col2"
        var isUnique = text.Contains("(unique)", StringComparison.OrdinalIgnoreCase) ||
                       text.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase);
        text = text.Replace("(unique)", "", StringComparison.OrdinalIgnoreCase).Trim();

        var onIdx = text.IndexOf(" on ", StringComparison.OrdinalIgnoreCase);
        if (onIdx < 0) return new DataIndex { Name = text, EntityName = entityName, IsUnique = isUnique };

        var name        = text[..onIdx].Trim();
        var columnsPart = text[(onIdx + 4)..].Trim();
        var cols        = columnsPart.Split(',', StringSplitOptions.TrimEntries)
                                     .Where(c => !string.IsNullOrEmpty(c))
                                     .ToList();

        return new DataIndex
        {
            Name       = name,
            EntityName = entityName,
            Columns    = cols,
            IsUnique   = isUnique,
        };
    }

    private static DataConstraint? ParseConstraintLine(string text, string entityName)
    {
        // "FK_name: definition" or "PK_name: definition"
        var colonIdx = text.IndexOf(':');
        if (colonIdx < 0)
            return new DataConstraint { Name = text, EntityName = entityName, ConstraintType = "CK" };

        var name       = text[..colonIdx].Trim();
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
            findings.Add(new DataModelFinding
            {
                Severity    = DataModelSeverity.Error,
                Category    = "Structure",
                Description = "No entities or tables were found. Add ## Entity: Name or ## Table: Name sections.",
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
}
