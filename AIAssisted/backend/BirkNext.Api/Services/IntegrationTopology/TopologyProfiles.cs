using BirkNext.IntegrationTopology;
using BirkNext.SourceDomains;

namespace BirkNext.Api.Services.IntegrationTopology;

/// <summary>Source capabilities the topology can use; a source role says which it provides, a snapshot shows which it has.</summary>
public static class TopologyCapabilities
{
    public const string ApplicationSource = "application-source";
    public const string HttpClient = "http-client-analysis";
    public const string HttpServer = "http-server-analysis";
    public const string ServiceBus = "servicebus-topology";
    public const string EventHub = "eventhub-topology";
    public const string Cdc = "cdc-topology";
    public const string Terraform = "terraform-infrastructure";
    public const string OpenApi = "openapi-contract";
    public const string RuntimeObservation = "runtime-observation";
    public const string ApplicationMetadata = "application-metadata";
    public const string Reference = "built-in-reference";
}

/// <summary>
/// One logical source a Test Pack's topology relies on. Recognized by content only — the components it contains (each token is one expected
/// part, matched against component and project names), configuration key prefixes, or analyzed Infrastructure as Code of a format — and never by
/// archive name. <see cref="Relationships"/> lists the expected relationships it can provide evidence for.
/// </summary>
public sealed record TopologySourceRoleDefinition(string Id, string DisplayName, TopologySourceRequirement Requirement)
{
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public IReadOnlyList<string> Relationships { get; init; } = [];
    public TopologySourceMatch Match { get; init; } = new();
    public string Guidance { get; init; } = "";
    /// <summary>What may remain unresolved without the source.</summary>
    public string Consequence { get; init; } = "";
}

public sealed record TopologySourceMatch
{
    /// <summary>Expected parts: each token must be found in a component or project name of some matching source (all = Available, some = Partly
    /// available). One part may name explicit alternatives separated by '|' ("Receiver|Mottak") when an implementation was renamed — each is
    /// still an exact substring of a component or project name, never a similarity guess.</summary>
    public IReadOnlyList<string> Components { get; init; } = [];
    /// <summary>Expected parts matched at the start of a component or project name ("Orders." matches Orders.Api but not Shop.Orders.Worker).</summary>
    public IReadOnlyList<string> ComponentPrefixes { get; init; } = [];
    /// <summary>Configuration key prefixes that also identify the role (used when no component token is declared).</summary>
    public IReadOnlyList<string> ConfigurationKeyPrefixes { get; init; } = [];
    /// <summary>The role is infrastructure source: analyzed IaC of one of these formats.</summary>
    public IReadOnlyList<InfrastructureFormat> InfrastructureFormats { get; init; } = [];
}

/// <summary>
/// An expected relationship. <see cref="From"/>/<see cref="To"/> are logical systems (profile components with their aliases), a resource kind
/// written "resource:servicebus" / "resource:eventhub" / "resource:connector", or "role:{id}" (any component of a source role's matching
/// sources — so a system with no known component name is never aliased by guess); <see cref="Via"/> keeps only resources that end feeds.
/// Expectation only: it never creates a connection. <see cref="MissingEvidence"/> is the evidence that would resolve it, in the pack's words.
/// </summary>
public sealed record TopologyExpectedRelationship(string Id, string Label, string From, string To, string Transport, string Dimension)
{
    public string? Via { get; init; }
    public IReadOnlyList<string> EvidenceRoles { get; init; } = [];
    public string MissingEvidence { get; init; } = "";
    /// <summary>The missing evidence is configuration (a base address, a consumer mapping) that a configured integration can state, so once the
    /// sources are bound the fix is "Configure integration" rather than another source.</summary>
    public bool ConfigurationResolvable { get; init; }
}

/// <summary>An external application a Test Pack ships reference metadata for (a built-in reference, no import): its identifiers (e.g. an
/// application id) and addresses. Source configuration naming them is evidence of a connection; the reference itself never is.</summary>
public sealed record TopologyExternalReference(string RoleId, string System, Func<TopologyReferenceFacts?> Load);

public sealed record TopologyReferenceFacts(IReadOnlyList<string> Identifiers, IReadOnlyList<string> Addresses, string Detail);

public static class TopologyDimensions
{
    public const string Application = "Application";
    public const string Infrastructure = "Infrastructure";
    public const string Messaging = "Messaging";
    public const string Contract = "Contract";
    public const string ExternalConsumer = "External consumer";
}
