using BirkNext.DatabaseArchitecture;

namespace BirkNext.Api.Services.DatabaseArchitecture;

/// <summary>Bounded token parser for a deliberately small DDL grammar. Comments and string values are never evidence.</summary>
public sealed class SqlDdlSchemaExtractor : IDatabaseSchemaExtractor
{
    public string Name => "SQL DDL tokens";
    public string Version => "1";
    public bool CanAnalyze(DatabaseExtractionInput input) => input.Workspace.Files.Any(f => f.Path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase));
    internal sealed record Token(string Text, int Line, bool Quoted = false);
    internal static List<Token> Tokenize(string sql)
    {
        var result = new List<Token>(); var line = 1;
        for (var i = 0; i < sql.Length;)
        {
            var c = sql[i]; if (c == '\n') { line++; i++; continue; } if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-') { while (i < sql.Length && sql[i] != '\n') i++; continue; }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*') { i += 2; var depth = 1; while (i < sql.Length && depth > 0) { if (sql[i] == '\n') line++; if (i + 1 < sql.Length && sql[i] == '/' && sql[i + 1] == '*') { depth++; i += 2; } else if (i + 1 < sql.Length && sql[i] == '*' && sql[i + 1] == '/') { depth--; i += 2; } else i++; } continue; }
            if (c is '\'' or '"' or '[')
            {
                var end = c == '[' ? ']' : c; var startLine = line; i++; var value = new System.Text.StringBuilder();
                while (i < sql.Length) { var x = sql[i++]; if (x == '\n') line++; if (x == end) { if (i < sql.Length && sql[i] == end) { i++; value.Append(end); continue; } break; } value.Append(x); }
                result.Add(new Token(c == '\'' ? "<value excluded>" : Extraction.Safe(value.ToString()), startLine, true)); continue;
            }
            if (char.IsLetterOrDigit(c) || c is '_' or '$' or '#') { var start = i++; while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '$' or '#')) i++; result.Add(new(sql[start..i], line)); }
            else { result.Add(new(c.ToString(), line)); i++; }
        }
        return result;
    }
    public Task<DatabaseExtractionResult> AnalyzeAsync(DatabaseExtractionInput input, CancellationToken cancellationToken)
    {
        var databases = new List<DatabaseModel>(); var unresolved = new List<DatabaseEvidence>(); var diagnostics = new List<string>();
        foreach (var file in input.Workspace.Files.Where(f => f.Path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested(); var tokens = Tokenize(file.Content); var p = 0;
            var project = Extraction.Project(input, file.Path);
            var db = databases.FirstOrDefault(d => d.SourceProject == Extraction.Safe(project));
            if (db is null) { db = new DatabaseModel { Id = Extraction.Id(project + ":ddl"), LogicalName = "DDL source group (database identity unknown)", SourceProject = Extraction.Safe(project) }; databases.Add(db); }
            bool Is(int index, string text) => index < tokens.Count && !tokens[index].Quoted && tokens[index].Text.Equals(text, StringComparison.OrdinalIgnoreCase);
            (string Name, string? Schema) ObjectName()
            {
                if (p >= tokens.Count) return ("", null); var first = tokens[p++].Text;
                if (p < tokens.Count && tokens[p].Text == ".") { p++; return (p < tokens.Count ? tokens[p++].Text : "", first); } return (first, null);
            }
            DatabaseEvidence Evidence(int start, string type) => new(input.SourceSnapshotId, input.Workspace.Archive.Sha256, Extraction.Safe(project), Extraction.Safe(file.Path), "DDL", tokens[start].Line, p > 0 ? tokens[Math.Min(p - 1, tokens.Count - 1)].Line : tokens[start].Line, Name, type, "Explicit DDL declaration; execution and deployed schema unknown", DatabaseEvidenceState.ObservedFromDDL);
            while (p < tokens.Count)
            {
                cancellationToken.ThrowIfCancellationRequested(); var start = p;
                if (Is(p, "CREATE") && Is(p + 1, "TABLE"))
                {
                    p += 2; if (Is(p, "IF") && Is(p + 1, "NOT") && Is(p + 2, "EXISTS")) p += 3;
                    var (name, schema) = ObjectName(); if (p >= tokens.Count || tokens[p].Text != "(") { unresolved.Add(Evidence(start, "Unsupported CREATE TABLE form") with { State = DatabaseEvidenceState.Unresolved }); continue; }
                    p++; var clauses = new List<List<Token>>(); var clause = new List<Token>(); var depth = 0; var closed = false;
                    while (p < tokens.Count) { var t = tokens[p++]; if (t.Text == ")" && depth == 0) { if (clause.Count > 0) clauses.Add(clause); closed = true; break; } if (t.Text == "," && depth == 0) { clauses.Add(clause); clause = []; continue; } if (t.Text == "(") depth++; if (t.Text == ")") depth--; clause.Add(t); }
                    if (!closed) { unresolved.Add(Evidence(start, "Unterminated CREATE TABLE") with { State = DatabaseEvidenceState.Unresolved }); continue; }
                    var ev = Evidence(start, "CREATE TABLE"); var table = Extraction.Table(db, Extraction.Safe(name), schema is null ? null : Extraction.Safe(schema), ev); Extraction.Add(db, ev);
                    foreach (var part in clauses) ParseClause(table, part, ev, db);
                }
                else if (Is(p, "ALTER") && Is(p + 1, "TABLE"))
                {
                    p += 2; var (name, schema) = ObjectName(); var part = new List<Token>(); while (p < tokens.Count && tokens[p].Text != ";" && !Is(p, "GO")) part.Add(tokens[p++]);
                    var ev = Evidence(start, "ALTER TABLE"); var table = Extraction.Table(db, Extraction.Safe(name), schema, ev);
                    if (part.Count > 0 && part[0].Text.Equals("ADD", StringComparison.OrdinalIgnoreCase)) ParseClause(table, part.Skip(1).ToList(), ev, db);
                    else unresolved.Add(ev with { State = DatabaseEvidenceState.Unresolved, Explanation = "ALTER operation unsupported; not applied" });
                }
                else if (Is(p, "CREATE") && (Is(p + 1, "INDEX") || Is(p + 1, "UNIQUE") && Is(p + 2, "INDEX")))
                {
                    var unique = Is(p + 1, "UNIQUE"); p += unique ? 3 : 2; var (indexName, _) = ObjectName();
                    if (!Is(p, "ON")) continue; p++; var (tableName, schema) = ObjectName();
                    if (Is(p, "USING")) { p += 2; }
                    var cols = new List<string>(); if (p < tokens.Count && tokens[p].Text == "(") { p++; while (p < tokens.Count && tokens[p].Text != ")") { var t = tokens[p++]; if (t.Text != "," && !t.Text.Equals("ASC", StringComparison.OrdinalIgnoreCase) && !t.Text.Equals("DESC", StringComparison.OrdinalIgnoreCase)) cols.Add(Extraction.Safe(t.Text)); } if (p < tokens.Count) p++; }
                    var ev = Evidence(start, "CREATE INDEX"); var table = Extraction.Table(db, tableName, schema, ev); var ix = new IndexModel { Name = indexName, Columns = cols, Unique = unique }; Extraction.Add(ix, ev); table.Indexes.Add(ix);
                }
                else p++;
                if (p == start) p++;
            }
        }
        databases.RemoveAll(d => d.Schemas.Count == 0);
        diagnostics.Add("DDL supports basic CREATE TABLE, ALTER TABLE ADD constraints/columns and CREATE INDEX. Dynamic SQL, dialect extensions, expressions and execution order are not assessed.");
        return Task.FromResult(new DatabaseExtractionResult(databases, unresolved, diagnostics));
    }
    private static void ParseClause(TableModel table, List<Token> part, DatabaseEvidence ev, DatabaseModel db)
    {
        if (part.Count == 0) return;
        int Find(string value) => part.FindIndex(t => !t.Quoted && t.Text.Equals(value, StringComparison.OrdinalIgnoreCase));
        List<string> ListAt(int start) { var values = new List<string>(); if (start < 0) return values; var open = part.FindIndex(start, t => t.Text == "("); if (open < 0) return values; for (var i = open + 1; i < part.Count && part[i].Text != ")"; i++) if (part[i].Text != ",") values.Add(Extraction.Safe(part[i].Text)); return values; }
        var constraint = Find("CONSTRAINT"); var offset = constraint == 0 ? 2 : 0; if (offset >= part.Count) return;
        var first = part[offset].Text.ToUpperInvariant(); var tableConstraint = first is "PRIMARY" or "FOREIGN" or "UNIQUE" or "CHECK";
        ColumnModel? column = null;
        if (!tableConstraint)
        {
            column = Extraction.Column(table, Extraction.Safe(part[0].Text), ev);
            var type = new List<string>(); var depth = 0;
            for (var i = 1; i < part.Count; i++) { var text = part[i].Text; if (depth == 0 && new[] { "NULL", "NOT", "PRIMARY", "REFERENCES", "UNIQUE", "DEFAULT", "CONSTRAINT", "CHECK", "IDENTITY", "GENERATED" }.Contains(text.ToUpperInvariant())) break; if (text == "(") depth++; if (text == ")") depth--; type.Add(text); }
            column.StoreType = Extraction.StoreType(string.Join(' ', type)); column.Nullable = Find("NOT") >= 0 && Find("NULL") > Find("NOT") ? false : Find("NULL") >= 0 ? true : null;
            column.IsIdentity = Find("IDENTITY") >= 0 || type.Any(t => t.Equals("serial", StringComparison.OrdinalIgnoreCase) || t.Equals("bigserial", StringComparison.OrdinalIgnoreCase)) ? true : null;
            if (Find("IDENTITY") >= 0 || type.Any(t => t.Equals("nvarchar", StringComparison.OrdinalIgnoreCase))) db.Provider = "SQL Server";
            if (type.Any(t => t.Equals("serial", StringComparison.OrdinalIgnoreCase) || t.Equals("jsonb", StringComparison.OrdinalIgnoreCase) || t.Equals("uuid", StringComparison.OrdinalIgnoreCase))) db.Provider = "PostgreSQL";
        }
        if (Find("PRIMARY") >= 0) Extraction.Key(table, column is null ? ListAt(Find("PRIMARY")) : [column.Name], ev);
        if (Find("UNIQUE") >= 0) { var u = new ConstraintModel { Name = constraint == 0 ? part[1].Text : null, Type = "Unique", Columns = column is null ? ListAt(Find("UNIQUE")) : [column.Name] }; Extraction.Add(u, ev); table.Constraints.Add(u); }
        var references = Find("REFERENCES");
        if (references >= 0 && references + 1 < part.Count)
        {
            var target = part[references + 1].Text; string? targetSchema = null; if (references + 3 < part.Count && part[references + 2].Text == ".") { targetSchema = target; target = part[references + 3].Text; }
            Extraction.Relationship(table, target, column is null ? ListAt(Find("FOREIGN")) : [column.Name], ListAt(references), ev);
            if (table.ForeignKeys.LastOrDefault() is { } relationship) relationship.ToSchema = targetSchema;
        }
        if (Find("CHECK") >= 0) { var check = new ConstraintModel { Name = constraint == 0 ? part[1].Text : null, Type = "Check (expression excluded)" }; Extraction.Add(check, ev); table.Constraints.Add(check); }
    }
}
