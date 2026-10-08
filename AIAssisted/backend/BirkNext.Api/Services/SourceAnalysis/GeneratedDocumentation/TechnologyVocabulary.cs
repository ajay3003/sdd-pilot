using System.Text.RegularExpressions;

namespace BirkNext.Api.Services.SourceAnalysis.GeneratedDocumentation;

/// <summary>
/// A small, generic technology vocabulary for deterministic term matching in documentation (generated and authored). Each term has explicit
/// aliases and a category; matching is whole-word and case-insensitive on those aliases only — never text similarity, never inference.
/// Ids reuse the Technology Coverage registry ids where one exists. Categories marked exclusive are the only ones where two different
/// technologies in the same scope can be reported as a value mismatch (e.g. a planned relational store vs a documented one).
/// </summary>
internal static class TechnologyVocabulary
{
    internal sealed record Term(string Id, string Name, string Category, bool Exclusive, Regex Pattern);

    private static Term T(string id, string name, string category, bool exclusive, params string[] aliases) =>
        new(id, name, category, exclusive, new Regex(@"(?<![\p{L}\p{N}_./-])(" + string.Join("|", aliases.Select(Regex.Escape).Select(a => a.Replace(@"\ ", @"[\s-]?"))) + @")(?![\p{L}\p{N}_/-]|\.[\p{L}\p{N}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled));

    public static readonly IReadOnlyList<Term> Terms =
    [
        T("db.sqlserver", "SQL Server", "Relational database", true, "SQL Server", "MSSQL", "Azure SQL"),
        T("db.postgresql", "PostgreSQL", "Relational database", true, "PostgreSQL", "Postgres", "Npgsql"),
        T("db.mysql", "MySQL / MariaDB", "Relational database", true, "MySQL", "MariaDB"),
        T("db.oracle", "Oracle Database", "Relational database", true, "Oracle Database", "Oracle DB"),
        T("db.sqlite", "SQLite", "Relational database", true, "SQLite"),
        T("db.mongodb", "MongoDB", "Document database", false, "MongoDB"),
        T("db.cosmosdb", "Azure Cosmos DB", "Document database", false, "Cosmos DB", "CosmosDB"),
        T("cache.redis", "Redis", "Cache", false, "Redis"),
        T("integration.servicebus", "Azure Service Bus", "Messaging", false, "Service Bus", "ServiceBus"),
        T("integration.eventhub", "Azure Event Hubs", "Messaging", false, "Event Hubs", "Event Hub", "EventHubs", "EventHub"),
        T("integration.kafka", "Apache Kafka", "Messaging", false, "Kafka"),
        T("integration.rabbitmq", "RabbitMQ", "Messaging", false, "RabbitMQ"),
        T("messaging.wolverine", "Wolverine", "Messaging framework", true, "Wolverine"),
        T("messaging.masstransit", "MassTransit", "Messaging framework", true, "MassTransit"),
        T("messaging.nservicebus", "NServiceBus", "Messaging framework", true, "NServiceBus"),
        T("integration.graphql", "GraphQL", "API style", false, "GraphQL"),
        T("graphql.hotchocolate", "Hot Chocolate", "GraphQL server", true, "Hot Chocolate", "HotChocolate"),
        T("integration.grpc", "gRPC", "API style", false, "gRPC"),
        T("contract.openapi", "OpenAPI", "API description", false, "OpenAPI", "Swagger"),
        T("orm.efcore", "Entity Framework Core", "Data access", false, "Entity Framework", "EF Core", "EntityFrameworkCore"),
        T("orm.dapper", "Dapper", "Data access", false, "Dapper"),
        T("framework.blazor-wasm", "Blazor", "Frontend framework", true, "Blazor"),
        T("frontend.react", "React", "Frontend framework", true, "React"),
        T("frontend.angular", "Angular", "Frontend framework", true, "Angular"),
        T("frontend.vue", "Vue", "Frontend framework", true, "Vue.js", "VueJS"),
        T("framework.aspnetcore", "ASP.NET Core", "Web framework", false, "ASP.NET Core", "ASP.NET"),
        T("identity.entra", "Microsoft Entra ID", "Identity", false, "Entra ID", "EntraId", "Azure AD", "AzureAD", "Microsoft Identity Web", "Microsoft.Identity.Web"),
        T("hosting.aspire", ".NET Aspire", "Hosting", false, ".NET Aspire", "Aspire AppHost", "Aspire.Hosting"),
        T("cloud.kubernetes", "Kubernetes", "Hosting", false, "Kubernetes", "AKS"),
        T("hosting.functions", "Azure Functions", "Hosting", false, "Azure Functions"),
        T("hosting.docker", "Docker / containers", "Hosting", false, "Docker", "Dockerfile", "Podman"),
        T("lang.java", "Java", "Language", false, "Java"),
        T("lang.python", "Python", "Language", false, "Python"),
        T("lang.typescript", "TypeScript", "Language", false, "TypeScript"),
        T("lang.csharp", "C#", "Language", false, "C#", "CSharp"),
    ];

    public static Term? ById(string id) => Terms.FirstOrDefault(t => t.Id == id);

    public static string Name(string id) => ById(id)?.Name ?? id;

    /// <summary>Vocabulary ids named in <paramref name="text"/> (whole-word aliases only).</summary>
    public static List<string> Match(string text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        return Terms.Where(t => t.Pattern.IsMatch(text)).Select(t => t.Id).ToList();
    }
}
