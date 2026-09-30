using System.Security.Cryptography;
using System.Text;
using BirkNext.DatabaseArchitecture;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BirkNext.Api.Services.DatabaseArchitecture;

public sealed record DatabaseExtractionInput(Guid SourceSnapshotId, IqrSourceArchiveReader.Workspace Workspace);
public sealed record DatabaseExtractionResult(List<DatabaseModel> Databases, List<DatabaseEvidence> Unresolved, List<string> Diagnostics);
public interface IDatabaseSchemaExtractor
{
    string Name { get; }
    string Version { get; }
    bool CanAnalyze(DatabaseExtractionInput input);
    Task<DatabaseExtractionResult> AnalyzeAsync(DatabaseExtractionInput input, CancellationToken cancellationToken);
}
internal static class Extraction
{
    internal static string Safe(string value) => IqrSourceArchiveReader.SafeLabel(value);
    internal static string Id(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24];
    internal static string Project(DatabaseExtractionInput input, string path) => input.Workspace.Files.Where(f => f.Path.EndsWith(".csproj") && path.StartsWith(f.Path[..(f.Path.LastIndexOf('/') + 1)], StringComparison.Ordinal)).OrderByDescending(f => f.Path.Length).Select(f => f.Path).FirstOrDefault() ?? path[..Math.Max(0, path.LastIndexOf('/'))];
    internal static DatabaseEvidence Evidence(DatabaseExtractionInput input, string path, SyntaxNode node, string extractor, string type, DatabaseEvidenceState state) =>
        new(input.SourceSnapshotId, input.Workspace.Archive.Sha256, Safe(Project(input, path)), Safe(path), Safe(node.AncestorsAndSelf().OfType<ClassDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText ?? ""), node.GetLocation().GetLineSpan().StartLinePosition.Line + 1, node.GetLocation().GetLineSpan().EndLinePosition.Line + 1, extractor, type, type + " source assertion; runtime not assessed", state);
    internal static void Add(DatabaseFact fact, DatabaseEvidence evidence)
    {
        fact.Evidence.Add(evidence);
        if (fact.Evidence.Count == 1 || evidence.State == DatabaseEvidenceState.Confirmed || fact.EvidenceState is DatabaseEvidenceState.Inferred or DatabaseEvidenceState.Unresolved && evidence.State is DatabaseEvidenceState.ObservedFromMigration or DatabaseEvidenceState.ObservedFromDDL) fact.EvidenceState = evidence.State;
        fact.Confidence = fact.EvidenceState == DatabaseEvidenceState.Inferred ? "Convention only" : "Explicit source assertion";
    }
    internal static void Assert(DatabaseFact fact, string name, object? value, DatabaseEvidence evidence)
    {
        if (value is not null) fact.Assertions.Add(new DatabaseAssertion(name, name == "StoreType" ? StoreType(value.ToString()) ?? "Unresolved type" : Safe(value.ToString()!), evidence));
    }
    internal static string? StoreType(string? value)
    {
        if (value is null || value.Length > 300 || !System.Text.RegularExpressions.Regex.IsMatch(value, "^[\\p{L}_][\\p{L}\\p{N}_ .,\\[\\]\\\"()]*$")) return null;
        return value.Replace(" (", "(").Replace("( ", "(").Replace(" )", ")").Replace(" ,", ",").Replace(", ", ",").Replace(" [", "[").Replace("[ ", "[").Replace(" ]", "]");
    }
    internal static string? SqlType(ExpressionSyntax? expression) => expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression) ? StoreType(literal.Token.ValueText) : null;
    internal static string Method(InvocationExpressionSyntax node) => (node.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.ValueText ?? (node.Expression as IdentifierNameSyntax)?.Identifier.ValueText ?? "";
    internal static ExpressionSyntax? Arg(InvocationExpressionSyntax node, string name, int position = -1) => node.ArgumentList.Arguments.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == name)?.Expression ?? (position >= 0 ? node.ArgumentList.Arguments.Where(a => a.NameColon is null).ElementAtOrDefault(position)?.Expression : null);
    internal static string? Literal(ExpressionSyntax? node) => node is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression) ? Safe(literal.Token.ValueText) : node is InvocationExpressionSyntax { Expression: IdentifierNameSyntax identifier } invocation && identifier.Identifier.ValueText == "nameof" && invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression is { } symbol ? Safe(symbol.ToString().Split('.').Last()) : null;
    internal static bool? Bool(ExpressionSyntax? node) => node?.IsKind(SyntaxKind.TrueLiteralExpression) == true ? true : node?.IsKind(SyntaxKind.FalseLiteralExpression) == true ? false : null;
    internal static int? Number(ExpressionSyntax? node) => node is LiteralExpressionSyntax l && l.Token.Value is int n ? n : null;
    internal static List<string> Columns(ExpressionSyntax? node)
    {
        if (Literal(node) is { } s) return [s];
        if (node is LambdaExpressionSyntax lambda) node = lambda.Body as ExpressionSyntax;
        if (node is MemberAccessExpressionSyntax member) return [Safe(member.Name.Identifier.ValueText)];
        if (node is AnonymousObjectCreationExpressionSyntax anon) return anon.Initializers.Select(i => i.Expression).OfType<MemberAccessExpressionSyntax>().Select(m => Safe(m.Name.Identifier.ValueText)).ToList();
        if (node is ArrayCreationExpressionSyntax array) return array.Initializer?.Expressions.Select(Literal).OfType<string>().ToList() ?? [];
        if (node is ImplicitArrayCreationExpressionSyntax implicitArray) return implicitArray.Initializer.Expressions.Select(Literal).OfType<string>().ToList();
        return [];
    }
    internal static IEnumerable<InvocationExpressionSyntax> Chain(InvocationExpressionSyntax node)
    {
        yield return node;
        if (node.Expression is MemberAccessExpressionSyntax { Expression: InvocationExpressionSyntax inner }) foreach (var i in Chain(inner)) yield return i;
    }
    internal static TableModel Table(DatabaseModel db, string name, string? schema, DatabaseEvidence evidence, string? entity = null)
    {
        var group = db.Schemas.FirstOrDefault(s => s.Name == schema);
        if (group is null) { group = new SchemaModel { Name = schema }; db.Schemas.Add(group); Add(group, evidence); }
        var table = group.Tables.FirstOrDefault(t => t.PhysicalName == name || t.EntityTypeName == entity && entity is not null);
        if (table is null) { table = new TableModel { Id = Id(db.Id + ":" + schema + ":" + name), LogicalName = name, PhysicalName = evidence.State == DatabaseEvidenceState.Inferred ? null : name, SchemaName = schema, EntityTypeName = entity, SourceProject = db.SourceProject }; group.Tables.Add(table); }
        Add(table, evidence); return table;
    }
    internal static ColumnModel Column(TableModel table, string name, DatabaseEvidence evidence)
    {
        var column = table.Columns.FirstOrDefault(c => c.Name == name || c.PropertyName == name);
        if (column is null) { column = new ColumnModel { Name = name }; table.Columns.Add(column); }
        Add(column, evidence); return column;
    }
    internal static void Key(TableModel table, List<string> columns, DatabaseEvidence evidence)
    {
        if (columns.Count == 0) return;
        Assert(table, "PrimaryKey", string.Join(',', columns), evidence);
        if (evidence.State == DatabaseEvidenceState.Inferred && table.PrimaryKey is { EvidenceState: not DatabaseEvidenceState.Inferred }) return;
        foreach (var column in table.Columns) column.IsPrimaryKey = false;
        table.PrimaryKey = new KeyModel { Columns = columns }; Add(table.PrimaryKey, evidence);
        foreach (var c in columns) Column(table, c, evidence).IsPrimaryKey = true;
    }
    internal static void Relationship(TableModel table, string target, List<string> from, List<string> to, DatabaseEvidence evidence, string cardinality = "N:1", string? delete = null)
    {
        if (from.Count == 0) return;
        var r = new RelationshipModel { Id = Id(table.Id + string.Join(',', from) + target), FromTable = table.Id, ToTable = target, FromColumns = from, ToColumns = to, Cardinality = cardinality, DeleteBehavior = delete };
        Add(r, evidence); table.ForeignKeys.Add(r); table.Relationships.Add(r);
        foreach (var c in from) Column(table, c, evidence).IsForeignKey = true;
    }
}

public sealed class EfCoreSchemaExtractor : IDatabaseSchemaExtractor
{
    public string Name => "EF Core syntax";
    public string Version => "1";
    public bool CanAnalyze(DatabaseExtractionInput input) => input.Workspace.Files.Any(f => f.Content.Contains("DbContext") || f.Content.Contains("IEntityTypeConfiguration"));
    public Task<DatabaseExtractionResult> AnalyzeAsync(DatabaseExtractionInput input, CancellationToken cancellationToken)
    {
        var databases = new List<DatabaseModel>(); var unresolved = new List<DatabaseEvidence>(); var diagnostics = new List<string>();
        var files = input.Workspace.Files.Where(f => f.Path.EndsWith(".cs") && !f.Path.EndsWith(".Designer.cs") && !f.Path.EndsWith("ModelSnapshot.cs"))
            .Select(f => (f.Path, Root: CSharpSyntaxTree.ParseText(BirkNext.Api.Services.Integrations.ApplicationMessaging.WolverineSourceAnalyzer.BlankPrimaryConstructors(f.Content), new CSharpParseOptions(LanguageVersion.Preview), cancellationToken: cancellationToken).GetRoot())).ToList();
        var classes = files.SelectMany(f => f.Root.DescendantNodes().OfType<ClassDeclarationSyntax>().Select(c => (f.Path, Class: c))).ToList();
        foreach (var context in classes.Where(c => c.Class.BaseList?.Types.Any(t => t.Type.ToString().Split('.').Last().Split('(')[0] == "DbContext") == true))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var project = Extraction.Project(input, context.Path);
            var qualifiedContext = string.Join('.', context.Class.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString()).Append(context.Class.Identifier.ValueText));
            var db = new DatabaseModel { Id = Extraction.Id(project + ":" + qualifiedContext), LogicalName = Extraction.Safe(context.Class.Identifier.ValueText) + " (database candidate)", DbContext = Extraction.Safe(context.Class.Identifier.ValueText), SourceProject = Extraction.Safe(project) };
            Extraction.Add(db, Extraction.Evidence(input, context.Path, context.Class, Name, "DbContext; physical database identity unknown", DatabaseEvidenceState.Confirmed)); databases.Add(db);
            var projectFiles = files.Where(f => Extraction.Project(input, f.Path) == project).ToList();
            var projectSource = input.Workspace.Files.FirstOrDefault(f => f.Path == project);
            if (projectSource is not null)
            {
                try { using var reader = System.Xml.XmlReader.Create(new StringReader(projectSource.Content), new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null }); var xml = System.Xml.Linq.XDocument.Load(reader); var packages = xml.Descendants().Where(e => e.Name.LocalName == "PackageReference").Select(e => e.Attribute("Include")?.Value).ToList();
                    if (packages.Contains("Microsoft.EntityFrameworkCore.SqlServer")) db.Provider = "SQL Server";
                    if (packages.Contains("Npgsql.EntityFrameworkCore.PostgreSQL")) db.Provider = db.Provider is null ? "PostgreSQL" : "Ambiguous";
                    if (db.Provider is not null) Extraction.Add(db, new DatabaseEvidence(input.SourceSnapshotId, input.Workspace.Archive.Sha256, Extraction.Safe(project), Extraction.Safe(project), db.DbContext!, 1, 1, Name, "Provider package dependency", "Provider dependency exists; runtime provider selection not verified", DatabaseEvidenceState.Confirmed));
                } catch (System.Xml.XmlException) { diagnostics.Add("Provider project XML could not be parsed: " + Extraction.Safe(project)); }
            }
            foreach (var f in files) foreach (var call in f.Root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(i => Extraction.Method(i) is "UseSqlServer" or "UseNpgsql"))
            {
                var registrations = call.Ancestors().OfType<InvocationExpressionSyntax>().Where(i => i.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax g } && g.Identifier.ValueText is "AddDbContext" or "AddDbContextFactory" && g.TypeArgumentList.Arguments.First().ToString().Split('.').Last() == db.DbContext).ToList();
                if (registrations.Count == 0 && Extraction.Project(input, f.Path) != project) continue;
                if (registrations.Count == 0 && classes.Count(c => Extraction.Project(input, c.Path) == project && c.Class.BaseList?.Types.Any(t => t.Type.ToString().Split('.').Last().Split('(')[0] == "DbContext") == true) > 1) continue;
                var provider = Extraction.Method(call) == "UseNpgsql" ? "PostgreSQL" : "SQL Server";
                if (db.Provider is not null && db.Provider != provider) { db.Provider = "Ambiguous"; diagnostics.Add("Multiple provider assertions in " + db.SourceProject); } else db.Provider = provider;
                Extraction.Add(db, Extraction.Evidence(input, f.Path, call, Name, "Provider configuration " + Extraction.Method(call), DatabaseEvidenceState.Confirmed));
                var references = call.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(i => Extraction.Method(i) == "GetConnectionString").Select(i => Extraction.Literal(Extraction.Arg(i, "", 0))).OfType<string>().Distinct().ToList();
                if (references.Count == 1) { db.ConnectionReference = "ConnectionStrings:" + references[0]; Extraction.Add(db, Extraction.Evidence(input, f.Path, call, Name, "Connection configuration key only; value excluded", DatabaseEvidenceState.Confirmed)); }
            }
            var candidates = context.Class.Members.OfType<PropertyDeclarationSyntax>().Select(p => (Property: p, Generic: p.Type.DescendantNodesAndSelf().OfType<GenericNameSyntax>().FirstOrDefault(g => g.Identifier.ValueText == "DbSet"))).Where(p => p.Generic is not null)
                .Select(p => (Entity: p.Generic!.TypeArgumentList.Arguments.First().ToString(), Name: p.Property.Identifier.ValueText, Node: (SyntaxNode)p.Property)).ToList();
            foreach (var call in context.Class.DescendantNodes().OfType<InvocationExpressionSyntax>())
                if (call.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax g } && g.Identifier.ValueText == "Entity") candidates.Add((g.TypeArgumentList.Arguments.First().ToString(), g.TypeArgumentList.Arguments.First().ToString().Split('.').Last(), call));
            foreach (var candidate in candidates.DistinctBy(c => c.Entity))
            {
                var entityName = candidate.Entity.Split('.').Last();
                var ev = Extraction.Evidence(input, context.Path, candidate.Node, Name, "Persistence candidate; conventions are inferred", DatabaseEvidenceState.Inferred);
                var configs = classes.Where(c => Extraction.Project(input, c.Path) == project && c.Class.BaseList?.Types.Any(t => t.Type.DescendantNodesAndSelf().OfType<GenericNameSyntax>().Any(g => g.Identifier.ValueText == "IEntityTypeConfiguration" && g.TypeArgumentList.Arguments.First().ToString().Split('.').Last() == entityName)) == true).ToList();
                var calls = configs.SelectMany(c => c.Class.DescendantNodes().OfType<InvocationExpressionSyntax>().Select(i => (c.Path, Call: i)))
                    .Concat(context.Class.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(i => Extraction.Chain(i).Concat(i.Ancestors().OfType<InvocationExpressionSyntax>()).Any(x => x.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax g } && g.Identifier.ValueText == "Entity" && g.TypeArgumentList.Arguments.First().ToString().Split('.').Last() == entityName)).Select(i => (context.Path, Call: i)))
                    .Where(c => !c.Call.Ancestors().OfType<InvocationExpressionSyntax>().Any(i => Extraction.Method(i) is "OwnsOne" or "OwnsMany")).OrderBy(c => c.Call.Span.Length).ToList();
                var mapping = calls.FirstOrDefault(c => Extraction.Method(c.Call) == "ToTable");
                var name = mapping.Call is null ? candidate.Name : Extraction.Literal(Extraction.Arg(mapping.Call, "name", 0)) ?? candidate.Name;
                var tableAttributes = classes.Where(c => c.Class.Identifier.ValueText == entityName).SelectMany(c => c.Class.AttributeLists.SelectMany(a => a.Attributes).Where(a => a.Name.ToString().Split('.').Last() is "Table" or "TableAttribute").Select(a => (c.Path, Attribute: a))).ToList();
                if (mapping.Call is null && tableAttributes.Count == 1) name = Extraction.Literal(tableAttributes[0].Attribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression) ?? name;
                var defaultSchema = context.Class.DescendantNodes().OfType<InvocationExpressionSyntax>().FirstOrDefault(i => Extraction.Method(i) == "HasDefaultSchema");
                var schema = (mapping.Call is null ? null : Extraction.Literal(Extraction.Arg(mapping.Call, "schema", 1))) ?? (defaultSchema is null ? null : Extraction.Literal(Extraction.Arg(defaultSchema, "schema", 0)));
                if (mapping.Call is null && tableAttributes.Count == 1) schema = Extraction.Literal(tableAttributes[0].Attribute.ArgumentList?.Arguments.FirstOrDefault(a => a.NameEquals?.Name.Identifier.ValueText == "Schema")?.Expression) ?? schema;
                var mappingLiteral = mapping.Call is null ? null : Extraction.Literal(Extraction.Arg(mapping.Call, "name", 0));
                var mappingEvidence = mapping.Call is not null && mappingLiteral is not null ? Extraction.Evidence(input, mapping.Path, mapping.Call, Name, "ToTable", DatabaseEvidenceState.Confirmed) : tableAttributes.Count == 1 ? Extraction.Evidence(input, tableAttributes[0].Path, tableAttributes[0].Attribute, Name, "Table annotation", DatabaseEvidenceState.Confirmed) : ev;
                if (mapping.Call is not null && mappingLiteral is null) unresolved.Add(Extraction.Evidence(input, mapping.Path, mapping.Call, Name, "Dynamic ToTable name not resolved", DatabaseEvidenceState.Unresolved));
                var table = Extraction.Table(db, name, schema, mappingEvidence, entityName);
                if (tableAttributes.Count == 1 && Extraction.Literal(tableAttributes[0].Attribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression) is { } annotationName) Extraction.Assert(table, "PhysicalName", annotationName, Extraction.Evidence(input, tableAttributes[0].Path, tableAttributes[0].Attribute, Name, "Table annotation", DatabaseEvidenceState.Confirmed));
                var entities = classes.Where(c => c.Class.Identifier.ValueText == entityName).ToList();
                if (entities.Count > 1) { var module = context.Path.Split('/').First(); var local = entities.Where(c => c.Path.StartsWith(module + "/", StringComparison.Ordinal)).ToList(); if (local.Count == 1) entities = local; }
                if (entities.Count != 1) { unresolved.Add(ev with { Explanation = "Entity symbol is absent or ambiguous", State = DatabaseEvidenceState.Unresolved }); continue; }
                var entity = entities.Single();
                foreach (var property in entity.Class.Members.OfType<PropertyDeclarationSyntax>())
                {
                    var type = property.Type.ToString();
                    if (property.AttributeLists.ToString().Contains("NotMapped") || classes.Any(c => c.Class.Identifier.ValueText == type.TrimEnd('?').Split('.').Last()) || type.Contains('<') && !type.StartsWith("Nullable<")) continue;
                    var pe = Extraction.Evidence(input, entity.Path, property, Name, "Entity property convention", DatabaseEvidenceState.Inferred);
                    var column = Extraction.Column(table, Extraction.Safe(property.Identifier.ValueText), pe); column.PropertyName = column.Name; column.ClrType = Extraction.Safe(type);
                    column.Nullable = type.EndsWith('?') ? true : type is "string" or "object" ? null : false;
                    Extraction.Assert(column, "Nullable", column.Nullable, pe);
                    if (column.Name == "Id" || column.Name == entityName + "Id") Extraction.Key(table, [column.Name], pe);
                    foreach (var attr in property.AttributeLists.SelectMany(a => a.Attributes))
                    {
                        var ae = Extraction.Evidence(input, entity.Path, attr, Name, "Data annotation " + attr.Name, DatabaseEvidenceState.Confirmed);
                        var arg = attr.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
                        switch (attr.Name.ToString().Split('.').Last().Replace("Attribute", ""))
                        {
                            case "Key": Extraction.Key(table, [column.Name], ae); break;
                            case "Required": column.Nullable = false; Extraction.Assert(column, "Nullable", false, ae); Extraction.Add(column, ae); break;
                            case "MaxLength": case "StringLength": column.MaxLength = Extraction.Number(arg); Extraction.Add(column, ae); break;
                            case "Column": column.Name = Extraction.Literal(arg) ?? column.Name; Extraction.Add(column, ae); break;
                        }
                    }
                }
                foreach (var (path, call) in calls)
                {
                    var method = Extraction.Method(call); var ce = Extraction.Evidence(input, path, call, Name, method, DatabaseEvidenceState.Confirmed); var chain = Extraction.Chain(call).ToList();
                    var args = Extraction.Columns(Extraction.Arg(call, "", 0));
                    if (method is "HasKey" or "HasForeignKey" or "HasAlternateKey" or "HasIndex" && call.ArgumentList.Arguments.Count > 1 && call.ArgumentList.Arguments.All(a => Extraction.Literal(a.Expression) is not null)) args = call.ArgumentList.Arguments.Select(a => Extraction.Literal(a.Expression)!).ToList();
                    if (method == "ToTable" && Extraction.Literal(Extraction.Arg(call, "name", 0)) is { } declaredName) Extraction.Assert(table, "PhysicalName", declaredName, ce);
                    if (method == "HasKey") Extraction.Key(table, args, ce);
                    if (method == "HasAlternateKey") { var constraint = new ConstraintModel { Type = "Alternate key", Columns = args }; Extraction.Add(constraint, ce); table.Constraints.Add(constraint); }
                    if (method == "HasIndex") { var ix = new IndexModel { Columns = args }; Extraction.Add(ix, ce); table.Indexes.Add(ix); }
                    var prop = chain.FirstOrDefault(i => Extraction.Method(i) == "Property");
                    if (prop is not null && Extraction.Columns(Extraction.Arg(prop, "", 0)).FirstOrDefault() is { } propertyName)
                    {
                        var column = Extraction.Column(table, propertyName, ce);
                        switch (method)
                        {
                            case "HasColumnName": column.Name = Extraction.Literal(Extraction.Arg(call, "", 0)) ?? column.Name; break;
                            case "HasColumnType": column.StoreType = Extraction.SqlType(Extraction.Arg(call, "", 0)); break;
                            case "IsRequired": column.Nullable = !(Extraction.Bool(Extraction.Arg(call, "", 0)) ?? true); break;
                            case "HasMaxLength": column.MaxLength = Extraction.Number(Extraction.Arg(call, "", 0)); break;
                            case "HasPrecision": column.Precision = Extraction.Number(Extraction.Arg(call, "", 0)); column.Scale = Extraction.Number(Extraction.Arg(call, "", 1)); break;
                            case "ValueGeneratedOnAdd": column.IsGeneratedOnAdd = true; break;
                            case "HasComputedColumnSql": column.IsComputed = true; break;
                        }
                        if (method == "IsRequired") Extraction.Assert(column, "Nullable", column.Nullable, ce);
                        if (method == "HasColumnType") Extraction.Assert(column, "StoreType", column.StoreType, ce);
                        if (method == "HasMaxLength") Extraction.Assert(column, "MaxLength", column.MaxLength, ce);
                        if (method == "HasPrecision") { Extraction.Assert(column, "Precision", column.Precision, ce); Extraction.Assert(column, "Scale", column.Scale, ce); }
                    }
                    if (method == "IsUnique" && chain.FirstOrDefault(i => Extraction.Method(i) == "HasIndex") is { } indexCall)
                    { var ix = table.Indexes.FirstOrDefault(i => i.Columns.SequenceEqual(Extraction.Columns(Extraction.Arg(indexCall, "", 0)))); if (ix is not null) { ix.Unique = Extraction.Bool(Extraction.Arg(call, "", 0)) ?? true; Extraction.Add(ix, ce); } }
                    if (method == "HasForeignKey")
                    {
                        var nav = chain.FirstOrDefault(i => Extraction.Method(i) is "HasOne" or "HasMany"); string? target = null;
                        if (nav is not null && Extraction.Method(nav) == "HasMany" || call.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax dependent } && dependent.TypeArgumentList.Arguments.First().ToString().Split('.').Last() != entityName) { unresolved.Add(ce with { State = DatabaseEvidenceState.Unresolved, Explanation = "Principal-side relationship mapping requires type binding; no confirmed edge emitted" }); continue; }
                        if (nav?.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax g }) target = g.TypeArgumentList.Arguments.First().ToString().Split('.').Last();
                        if (target is null && nav is not null && Extraction.Columns(Extraction.Arg(nav, "", 0)).FirstOrDefault() is { } navigation) target = entity.Class.Members.OfType<PropertyDeclarationSyntax>().FirstOrDefault(p => p.Identifier.ValueText == navigation)?.Type.ToString().TrimEnd('?').Split('.').Last();
                        if (target is null) unresolved.Add(ce with { State = DatabaseEvidenceState.Unresolved, Explanation = "FK target could not be resolved" });
                        else Extraction.Relationship(table, target, args, [], ce, chain.Any(i => Extraction.Method(i) == "WithOne") ? "1:1" : "N:1");
                    }
                    if (method is "OwnsOne" or "OwnsMany" or "UsingEntity" or "SplitToTable") unresolved.Add(ce with { State = DatabaseEvidenceState.Unresolved, Explanation = "Owned/join/split mapping requires review; no separate physical table inferred" });
                    if (method == "OnDelete" && chain.FirstOrDefault(i => Extraction.Method(i) == "HasForeignKey") is { } foreignKey && Extraction.Arg(call, "", 0) is MemberAccessExpressionSyntax behavior)
                    { var columns = Extraction.Columns(Extraction.Arg(foreignKey, "", 0)); var relationship = table.ForeignKeys.FirstOrDefault(r => r.FromColumns.SequenceEqual(columns)); if (relationship is not null) { relationship.DeleteBehavior = Extraction.Safe(behavior.Name.Identifier.ValueText); Extraction.Add(relationship, ce); } }
                }
                foreach (var navigation in entity.Class.Members.OfType<PropertyDeclarationSyntax>())
                {
                    var target = navigation.Type.ToString().TrimEnd('?').Split('.').Last();
                    if (!candidates.Any(c => c.Entity.Split('.').Last() == target)) continue;
                    var annotation = navigation.AttributeLists.SelectMany(a => a.Attributes).FirstOrDefault(a => a.Name.ToString().Split('.').Last() is "ForeignKey" or "ForeignKeyAttribute");
                    var fkColumn = table.Columns.FirstOrDefault(c => c.PropertyName == navigation.Identifier.ValueText + "Id" || c.PropertyName == target + "Id");
                    if (annotation is null && fkColumn is null) { unresolved.Add(Extraction.Evidence(input, entity.Path, navigation, Name, "Navigation without resolved FK; relationship existence remains unknown", DatabaseEvidenceState.Unresolved)); continue; }
                    if (fkColumn is not null && table.ForeignKeys.Any(r => r.FromColumns.Contains(fkColumn.Name))) continue;
                    var state = annotation is null ? DatabaseEvidenceState.Inferred : DatabaseEvidenceState.Confirmed;
                    var inferred = Extraction.Evidence(input, entity.Path, navigation, Name, annotation is null ? "Navigation and FK naming convention" : "ForeignKey annotation", state);
                    var annotatedColumns = annotation?.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
                    Extraction.Relationship(table, target, annotation is null ? [fkColumn!.Name] : (Extraction.Literal(annotatedColumns)?.Split(',').Select(s => s.Trim()).ToList() ?? []), [], inferred);
                }
            }
            diagnostics.Add("Physical database identity unknown for " + db.DbContext + "; contexts remain separate candidates.");
        }
        return Task.FromResult(new DatabaseExtractionResult(databases, unresolved, diagnostics));
    }
}
