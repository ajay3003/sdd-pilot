using System.Text.Json;
using BirkNext.Api.Services.DatabaseArchitecture;
using BirkNext.Api.Services.Integrations.SourceEvidence;
using BirkNext.Api.Tests.Services.Integrations;
using BirkNext.DatabaseArchitecture;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace BirkNext.Api.Tests.Services;

public sealed class DatabaseArchitectureTests
{
    private const string Project = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>";
    private const string Model = """
        class WarehouseContext : DbContext { public DbSet<Customer> Customers { get; set; } public DbSet<Purchase> Purchases { get; set; } }
        class Customer { public int Id { get; set; } public string Name { get; set; } }
        class Purchase { public int Id { get; set; } public int CustomerId { get; set; } public Customer Customer { get; set; } }
        class CustomerConfig : IEntityTypeConfiguration<Customer> { void Configure(EntityTypeBuilder<Customer> b) { b.ToTable("Customers"); b.HasKey(x => x.Id); } }
        class PurchaseConfig : IEntityTypeConfiguration<Purchase> { void Configure(EntityTypeBuilder<Purchase> b) { b.ToTable("Purchases"); b.HasKey(x => x.Id); b.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId); b.Property(x => x.CustomerId).IsRequired(false); b.HasIndex(x => x.Id).IsUnique(); } }
        """;
    private static async Task<DatabaseArchitectureSnapshot> Analyze(string? model = Model, params (string Path, string Content)[] extra)
    {
        var files = new List<(string, string)> { ("src/Warehouse.csproj", Project) }; if (model is not null) files.Add(("src/Model.cs", model)); files.AddRange(extra);
        var (workspace, error) = IqrSourceArchiveReader.Read("generic.zip", IqrSourceEvidenceTests.Zip(files.ToArray())); error.Should().BeNull();
        return await DatabaseArchitectureAnalyzer.AnalyzeAsync(Guid.NewGuid(), workspace!, DateTimeOffset.UtcNow);
    }
    private static List<TableModel> Tables(DatabaseArchitectureSnapshot s) => s.Databases.SelectMany(d => d.Schemas.SelectMany(x => x.Tables)).ToList();
    [Fact] public async Task ExplicitFluentFkHasTraceAndDoesNotCreateNavigationTable()
    {
        var s = await Analyze(); var tables = Tables(s); tables.Should().HaveCount(2);
        var purchase = tables.Single(t => t.LogicalName == "Purchases"); purchase.PrimaryKey!.Columns.Should().Equal("Id");
        purchase.ForeignKeys.Should().ContainSingle(); var fk = purchase.ForeignKeys.Single(); fk.EvidenceState.Should().Be(DatabaseEvidenceState.Confirmed); fk.Cardinality.Should().Be("N:1"); fk.FromColumns.Should().Equal("CustomerId"); fk.ToColumns.Should().Equal("Id");
        fk.Evidence.Should().Contain(e => e.Type == "HasForeignKey" && e.Line > 0 && e.SourceSnapshotId == s.SourceSnapshotId);
        purchase.Indexes.Single().Unique.Should().BeTrue();
    }
    [Fact] public async Task ConventionIsNeverConfirmed()
    {
        var s = await Analyze(Model[..Model.IndexOf("class CustomerConfig", StringComparison.Ordinal)]);
        Tables(s).Should().OnlyContain(t => t.EvidenceState == DatabaseEvidenceState.Inferred && t.PhysicalName == null);
        Tables(s).SelectMany(t => t.Relationships).Should().ContainSingle().Which.EvidenceState.Should().Be(DatabaseEvidenceState.Inferred);
    }
    [Fact] public async Task CompositeKeysAndForeignKeysPreserveOrder()
    {
        var s = await Analyze(Model.Replace("b.HasKey(x => x.Id)", "b.HasKey(x => new { x.Id, x.Region })").Replace("HasForeignKey(x => x.CustomerId)", "HasForeignKey(x => new { x.CustomerId, x.Region })"));
        var t = Tables(s).Single(t => t.LogicalName == "Purchases"); t.PrimaryKey!.Columns.Should().Equal("Id", "Region"); t.ForeignKeys.Single().FromColumns.Should().Equal("CustomerId", "Region");
    }
    [Fact] public async Task TwoContextsRemainSeparateCandidates()
    {
        var s = await Analyze(Model + "class AuditContext : DbContext { public DbSet<Customer> Entries { get; set; } }"); s.Databases.Should().HaveCount(2); s.Databases.Select(d => d.Id).Distinct().Should().HaveCount(2);
    }
    [Fact] public async Task DomainObjectsEnumsAndOwnedTypesDoNotBecomeFakeTables()
    {
        var s = await Analyze(Model + "class ValueObject { public int Id {get;set;} } enum Category { One, Two } class Owned { public string Value {get;set;} }"); Tables(s).Should().HaveCount(2);
    }
    [Fact] public async Task MigrationsRemainSeparateEvidenceAndConflictsSurviveMerge()
    {
        var s = await Analyze(Model, ("src/Migrations/001.cs", """
            class Initial : Migration { void Up(MigrationBuilder migrationBuilder) {
                migrationBuilder.CreateTable(name: "Purchases", columns: table => new { Id = table.Column<int>(type: "integer", nullable: false), CustomerId = table.Column<int>(type: "integer", nullable: false) }, constraints: table => { table.PrimaryKey("PK", x => x.Id); });
                migrationBuilder.AddForeignKey(name: "FK", table: "Purchases", column: "CustomerId", principalTable: "Customers", principalColumn: "Id");
                migrationBuilder.CreateIndex(name: "IX", table: "Purchases", column: "CustomerId");
            } }
            """));
        Tables(s).Should().HaveCount(2); s.Databases.Should().ContainSingle(); var table = Tables(s).Single(t => t.LogicalName == "Purchases"); table.Evidence.Should().Contain(e => e.State == DatabaseEvidenceState.ObservedFromMigration);
        s.Conflicts.Should().Contain(c => c.Column == "CustomerId" && c.Fact == "Nullable"); s.Status.Should().Be(DatabaseAnalysisStatus.NeedsReview);
        table.Indexes.Should().HaveCount(2);
    }
    [Theory]
    [InlineData("dbo", "nvarchar(100)", "SQL Server")]
    [InlineData("public", "uuid", "PostgreSQL")]
    public async Task DdlOnlyPreservesStructureAndProvider(string schema, string type, string provider)
    {
        var sql = $"CREATE TABLE {schema}.Parent (Id int NOT NULL PRIMARY KEY); CREATE TABLE {schema}.Child (Id int PRIMARY KEY, ParentId int NOT NULL, Label {type} NULL, CONSTRAINT FK FOREIGN KEY (ParentId) REFERENCES {schema}.Parent(Id)); CREATE UNIQUE INDEX IX ON {schema}.Child(ParentId);";
        var s = await Analyze(null, ("src/schema.sql", sql)); var tables = Tables(s); tables.Should().HaveCount(2); var child = tables.Single(t => t.LogicalName == "Child"); child.Columns.Should().HaveCount(3); child.Columns.Single(c => c.Name == "Label").StoreType.Should().Be(type); child.ForeignKeys.Single().FromColumns.Should().Equal("ParentId"); child.ForeignKeys.Single().ToColumns.Should().Equal("Id"); child.Indexes.Single().Unique.Should().BeTrue(); child.EvidenceState.Should().Be(DatabaseEvidenceState.ObservedFromDDL); s.Databases.Single().Provider.Should().Be(provider);
    }
    [Fact] public async Task DdlTokenizerIgnoresCommentsAndLiteralBodies()
    {
        var s = await Analyze(null, ("src/schema.sql", "-- CREATE TABLE Fake(Id int);\n CREATE TABLE Real(Id int PRIMARY KEY, Label text DEFAULT 'CREATE TABLE Secret(Id int);'); /* CREATE TABLE Nope(Id int); */")); Tables(s).Should().ContainSingle(t => t.LogicalName == "Real");
    }
    [Fact] public async Task SecretsExcludedFromSerializedEvidence()
    {
        var s = await Analyze(Model + "class Setup { void Configure() { options.UseNpgsql(\"Host=server;Password=SECRET_DB_123\"); } }", ("src/appsettings.json", "{\"ConnectionStrings\":{\"Main\":\"Password=SECRET_DB_123\"}}"), ("src/schema.sql", "CREATE TABLE Config(Id int, Secret text DEFAULT 'SECRET_DB_123');"));
        JsonSerializer.Serialize(s).Should().NotContain("SECRET_DB_123").And.NotContain("Password=");
    }
    [Fact] public async Task DiffIsSourceSchemaChangeAndDoesNotMutateHistory()
    {
        var a = await Analyze(); var serialized = JsonSerializer.Serialize(a); var b = await Analyze(Model.Replace("public string Name", "public string? Name"));
        DatabaseSchemaComparison.Compare(a, b).Should().Contain(c => c.Kind == "Changed column" && c.Object.EndsWith("Name")); JsonSerializer.Serialize(a).Should().Be(serialized);
    }
    [Fact] public async Task UnsupportedSourceHasExplicitStatus()
    {
        var s = await Analyze("class Plain { public int Id {get;set;} }"); s.Status.Should().Be(DatabaseAnalysisStatus.Unsupported); Tables(s).Should().BeEmpty();
    }
    [Fact] public async Task OwnedMappingIsUnresolvedWithoutCreatingOrRenamingTheOwnerTable()
    {
        var s = await Analyze("""
            class Context : DbContext { public DbSet<Owner> Owners {get;set;} }
            class Owner { public int Id {get;set;} public Address Address {get;set;} }
            class Address { public string Street {get;set;} }
            class Config : IEntityTypeConfiguration<Owner> { void Configure(EntityTypeBuilder<Owner> b) { b.ToTable("Owners"); b.OwnsOne(x => x.Address, owned => { owned.ToTable("Addresses"); owned.Property(x => x.Street); }); } }
            """);
        Tables(s).Should().ContainSingle(t => t.PhysicalName == "Owners"); s.UnresolvedEvidence.Should().Contain(e => e.Explanation.Contains("Owned"));
    }
    [Fact] public async Task DynamicTableNamesRemainUnconfirmed()
    {
        var s = await Analyze(Model.Replace("b.ToTable(\"Purchases\")", "b.ToTable(runtimeName)")); var candidate = Tables(s).Single(t => t.EntityTypeName == "Purchase"); candidate.PhysicalName.Should().BeNull(); candidate.EvidenceState.Should().Be(DatabaseEvidenceState.Inferred); s.UnresolvedEvidence.Should().Contain(e => e.Type.Contains("Dynamic ToTable"));
    }
    [Fact] public async Task AnnotationAndFluentContradictionsAreRetained()
    {
        var s = await Analyze(Model.Replace("public int CustomerId", "[Required] public int CustomerId")); s.Conflicts.Should().Contain(c => c.Fact == "Nullable" && c.Column == "CustomerId");
    }
    [Fact] public async Task ArchiveIngestionPersistsOnlySafeImmutableDatabaseEvidence()
    {
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<BirkNext.Api.Data.AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new BirkNext.Api.Data.AppDbContext(options);
        var store = new IqrSourceStore(db); var bytes = IqrSourceEvidenceTests.Zip(("src/Warehouse.csproj", Project), ("src/Model.cs", Model + "class Setup { void Configure() { options.UseSqlServer(\"Password=SECRET_DB_123\"); } }"));
        var (snapshot, error) = await store.AnalyzeAsync("generic-env", "generic-source", "generic.zip", bytes); error.Should().BeNull(); snapshot!.DatabaseArchitecture!.SourceSnapshotId.Should().Be(snapshot.Id);
        var records = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(db.IqrSourceSnapshots); records.Single().EvidenceJson.Should().NotContain("SECRET_DB_123").And.NotContain("Password=").And.NotContain("public class");
        var historical = await store.GetAsync("generic-env", "generic-source", snapshot.Id); historical!.DatabaseArchitecture!.SnapshotId.Should().Be(snapshot.DatabaseArchitecture.SnapshotId);
    }
    [Fact] public async Task DesignerMetadataResolvesMigrationOwnershipWithoutMergingContexts()
    {
        var s = await Analyze(Model + "class AuditContext : DbContext { public DbSet<Customer> Entries {get;set;} }", ("src/Migrations/001.cs", "class Initial : Migration { void Up(MigrationBuilder migrationBuilder) { migrationBuilder.CreateTable(name: \"Customers\", columns: t => new { Id = t.Column<int>(type: \"integer\", nullable: false) }, constraints: t => t.PrimaryKey(\"PK\", x => x.Id)); } }"), ("src/Migrations/001.Designer.cs", "[DbContext(typeof(AuditContext))] partial class Initial { }"));
        s.Databases.Should().HaveCount(2); s.Databases.Single(d => d.DbContext == "AuditContext").Evidence.Should().Contain(e => e.Type.Contains("ownership metadata")); s.Databases.Single(d => d.DbContext == "WarehouseContext").Evidence.Should().NotContain(e => e.State == DatabaseEvidenceState.ObservedFromMigration);
    }
    [Fact] public async Task ExplicitKeyIsNotReplacedByAnIdNamingConvention()
    {
        var s = await Analyze("class Context : DbContext { public DbSet<Row> Rows {get;set;} } class Row { [Key] public int RowKey {get;set;} public int Id {get;set;} }");
        var table = Tables(s).Single(); table.PrimaryKey!.Columns.Should().Equal("RowKey"); table.Columns.Single(c => c.Name == "Id").IsPrimaryKey.Should().BeFalse();
    }
    [Fact] public async Task ContextNamesInDifferentNamespacesHaveDistinctCandidateIds()
    {
        var s = await Analyze("namespace First { class Context : DbContext { public DbSet<Row> Rows {get;set;} } } namespace Second { class Context : DbContext {public DbSet<Row> Rows {get;set;} } } class Row {public int Id {get;set;} }");
        s.Databases.Select(d => d.Id).Distinct().Should().HaveCount(2); Tables(s).Select(t => t.Id).Distinct().Should().HaveCount(2);
    }
}
