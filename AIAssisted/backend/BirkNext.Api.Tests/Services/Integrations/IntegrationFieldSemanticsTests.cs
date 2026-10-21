using System.Text.Json;
using BirkNext.Integrations;
using FluentAssertions;

namespace BirkNext.Api.Tests.Services.Integrations;

/// <summary>
/// Field applicability, requirement and resolution (shared by the Integrations pane, IQR and the export) and causal per-integration readiness.
/// "Not configured" means applicable + required + empty + not resolved by evidence — never Not applicable, Not assessed, Needs confirmation or optional.
/// Generic fixtures: an HTTP/API webhook between two named systems and an Event Hub CDC topic; no product is special-cased.
/// </summary>
public sealed class IntegrationFieldSemanticsTests
{
    private static IntegrationDefinition Webhook(Func<IntegrationDefinition, IntegrationDefinition>? change = null)
    {
        var definition = new IntegrationDefinition
        {
            Id = "dev:http:report-intake", EnvironmentId = "dev", DisplayName = "Partner – report intake", Kind = IntegrationKind.HttpApi,
            EndpointOrTopic = "https://receiver.example.invalid/api/partner/webhook", Producer = "Partner platform",
            Consumer = new IntegrationConsumer { DisplayName = "Message receiver", MappingState = ConsumerMappingState.NeedsConfirmation },
            Origin = IntegrationRecordOrigin.Template, TemplateKey = "report-intake",
        };
        return change is null ? definition : change(definition);
    }

    private static readonly IntegrationPlatform Hubs = new() { Id = "dev:eventhub:cdc", Name = "CDC Event Hubs", Kind = IntegrationKind.EventHub, Namespace = "evhns-example" };

    private static IntegrationDefinition Topic(Func<IntegrationDefinition, IntegrationDefinition>? change = null)
    {
        var definition = new IntegrationDefinition
        {
            Id = "dev:eventhub:cdc:dbo.Person", EnvironmentId = "dev", DisplayName = "dbo.Person", Kind = IntegrationKind.EventHub, PlatformId = Hubs.Id,
            EndpointOrTopic = "cdc.dbo.Person", Producer = "Debezium", ProducerAuthentication = IntegrationAuthMechanism.ManagedIdentity,
            Consumer = new IntegrationConsumer { DisplayName = "Person adapter", MappingState = ConsumerMappingState.Confirmed }, ConsumerAuthentication = IntegrationAuthMechanism.ManagedIdentity,
        };
        return change is null ? definition : change(definition);
    }

    private static IntegrationFieldStatus Field(IntegrationConfigurationAssessment assessment, IntegrationField field) => assessment.Field(field)!;

    // §56
    [Fact]
    public void NotConfigured_OnlyForAnApplicableRequiredEmptyField_AndItIsMissing()
    {
        var assessment = IntegrationFieldRules.Assess(Topic(t => t with { ProducerAuthentication = IntegrationAuthMechanism.NotConfigured }), Hubs);
        var auth = Field(assessment, IntegrationField.ProducerAuthentication);
        auth.Requirement.Should().Be(IntegrationFieldRequirement.Required);
        auth.Resolution.Should().Be(IntegrationFieldResolution.NotConfigured);
        assessment.Missing.Select(f => f.Label).Should().Equal(["Producer authentication"]);
        assessment.State.Should().Be(IntegrationConfigurationState.NeedsConfiguration);
        IntegrationConfigurationRules.Evaluate(Topic(t => t with { ProducerAuthentication = IntegrationAuthMechanism.NotConfigured }), Hubs).Missing.Should().Equal(["Producer authentication"]);
    }

    // §57 + §68
    [Fact]
    public void HttpApi_ConsumerGroupWorkerSourceResourceAndNamespace_AreNotApplicable_NeverMissing()
    {
        var assessment = IntegrationFieldRules.Assess(Webhook(), null);
        foreach (var field in new[] { IntegrationField.ConsumerGroup, IntegrationField.WorkerEndpoint, IntegrationField.SourceResource, IntegrationField.Namespace })
        {
            Field(assessment, field).Requirement.Should().Be(IntegrationFieldRequirement.NotApplicable, field.ToString());
            Field(assessment, field).Resolution.Should().Be(IntegrationFieldResolution.NotApplicable, field.ToString());
        }
        assessment.Missing.Should().BeEmpty();
        assessment.NotApplicable.Select(f => f.Label).Should().Contain(["Consumer group", "Worker endpoint", "Source resource", "Namespace"]);
    }

    // §58 + §11 + §13 + §14
    [Fact]
    public void HttpApi_EmptyAuthentication_IsNotAssessed_AndDoesNotBlockConfiguration()
    {
        var assessment = IntegrationFieldRules.Assess(Webhook(), null);
        foreach (var field in new[] { IntegrationField.ProducerAuthentication, IntegrationField.ConsumerAuthentication })
        {
            Field(assessment, field).Requirement.Should().Be(IntegrationFieldRequirement.Conditional);
            Field(assessment, field).Resolution.Should().Be(IntegrationFieldResolution.NotAssessed);
            Field(assessment, field).Condition.Should().Contain("requires callers to authenticate");
        }
        var (state, missing, unconfirmed) = IntegrationConfigurationRules.Evaluate(Webhook(), null);
        missing.Should().BeEmpty("authentication is not required configuration for an HTTP/API boundary without evidence that it is");
        unconfirmed.Should().Equal(["Consumer mapping"]);
        state.Should().Be(IntegrationConfigurationState.NeedsConfirmation);
        // A configured mechanism (including None) is Configured.
        Field(IntegrationFieldRules.Assess(Webhook(w => w with { ConsumerAuthentication = IntegrationAuthMechanism.None }), null), IntegrationField.ConsumerAuthentication)
            .Resolution.Should().Be(IntegrationFieldResolution.Configured);
    }

    // §59
    [Fact]
    public void SourceEvidenceForAnEmptyField_IsDetectedFromSource_NotNotConfigured_AndNotStored()
    {
        var definition = Topic();
        var assessment = IntegrationFieldRules.Assess(definition, Hubs, [new(IntegrationField.ConsumerGroup, "person-adapter", IntegrationFieldEvidenceOrigin.Source, "consumer group in the worker's configuration")]);
        var group = Field(assessment, IntegrationField.ConsumerGroup);
        group.Resolution.Should().Be(IntegrationFieldResolution.DetectedFromSource);
        group.Value.Should().Be("person-adapter");
        group.Provenance.Should().Be("Source evidence");
        group.Reason.Should().Contain("Not stored");
        definition.ConsumerGroup.Should().BeNull("evidence never configures by being read");
        Field(IntegrationFieldRules.Assess(definition, Hubs, [new(IntegrationField.ConsumerGroup, "person-adapter", IntegrationFieldEvidenceOrigin.Runtime, "listed by Azure Resource Manager")]), IntegrationField.ConsumerGroup)
            .Resolution.Should().Be(IntegrationFieldResolution.DetectedFromRuntime);
    }

    // §60
    [Fact]
    public void UnconfirmedOrCandidateConsumers_NeedConfirmation_NeverNotConfigured()
    {
        Field(IntegrationFieldRules.Assess(Webhook(), null), IntegrationField.ConsumerMapping).Resolution.Should().Be(IntegrationFieldResolution.NeedsConfirmation);
        var unassigned = IntegrationFieldRules.Assess(Webhook(w => w with { Consumer = new IntegrationConsumer() }), null,
            [new(IntegrationField.Consumer, "Receiver worker", IntegrationFieldEvidenceOrigin.Source, "two components handle the route")]);
        Field(unassigned, IntegrationField.Consumer).Resolution.Should().Be(IntegrationFieldResolution.NeedsConfirmation);
        Field(unassigned, IntegrationField.Consumer).EvidenceValue.Should().Be("Receiver worker");
        Field(unassigned, IntegrationField.ConsumerMapping).Resolution.Should().Be(IntegrationFieldResolution.NotAssessed);
        unassigned.Missing.Should().BeEmpty();
        unassigned.Fields.Where(f => f.Field == IntegrationField.Consumer || f.Field == IntegrationField.ConsumerMapping)
            .Should().NotContain(f => f.Resolution == IntegrationFieldResolution.NotConfigured);
    }

    // §61
    [Fact]
    public void PlatformManaged_OnlyWithEvidence()
    {
        Field(IntegrationFieldRules.Assess(Webhook(), null), IntegrationField.ConsumerAuthentication).Resolution.Should().NotBe(IntegrationFieldResolution.PlatformManaged);
        var managed = IntegrationFieldRules.Assess(Webhook(), null,
            [new(IntegrationField.ConsumerAuthentication, "App Service authentication", IntegrationFieldEvidenceOrigin.PlatformManaged, "the hosting platform authenticates callers (declared in infrastructure)")]);
        var auth = Field(managed, IntegrationField.ConsumerAuthentication);
        auth.Resolution.Should().Be(IntegrationFieldResolution.PlatformManaged);
        auth.Reason.Should().Contain("Handled outside this configuration");
        managed.Missing.Should().BeEmpty();
    }

    // §62 + §67
    [Fact]
    public void OptionalEmptyFields_AreNotAssessed_NeverABlocker()
    {
        var assessment = IntegrationFieldRules.Assess(Webhook(), null);
        var health = Field(assessment, IntegrationField.HealthEndpoint);
        health.Requirement.Should().Be(IntegrationFieldRequirement.Optional);
        health.Resolution.Should().Be(IntegrationFieldResolution.NotAssessed);
        assessment.State.Should().NotBe(IntegrationConfigurationState.NeedsConfiguration);
        Field(IntegrationFieldRules.Assess(Webhook(w => w with { HealthUrl = "https://receiver.example.invalid/health" }), null), IntegrationField.HealthEndpoint)
            .Resolution.Should().Be(IntegrationFieldResolution.Configured);
    }

    // §69 + §43
    [Fact]
    public void MessagingIntegrations_KeepTheirRequirements()
    {
        var assessment = IntegrationFieldRules.Assess(Topic(t => t with { ProducerAuthentication = IntegrationAuthMechanism.NotConfigured, ConsumerAuthentication = IntegrationAuthMechanism.NotConfigured }), Hubs);
        assessment.Missing.Select(f => f.Label).Should().Equal(["Producer authentication", "Consumer authentication"], "messaging reviews read the mechanism");
        var group = Field(assessment, IntegrationField.ConsumerGroup);
        group.Requirement.Should().Be(IntegrationFieldRequirement.Conditional);
        group.Resolution.Should().Be(IntegrationFieldResolution.NotConfigured);
        group.Condition.Should().Contain("checkpoint");
        assessment.ConditionalGaps.Should().ContainSingle(f => f.Field == IntegrationField.ConsumerGroup);
        Field(assessment, IntegrationField.WorkerEndpoint).Requirement.Should().Be(IntegrationFieldRequirement.Optional, "a worker applies to a stream consumer");
        Field(assessment, IntegrationField.Namespace).Requirement.Should().Be(IntegrationFieldRequirement.Required);
        Field(IntegrationFieldRules.Assess(Topic(t => t with { ConsumerGroup = "person-adapter" }), Hubs), IntegrationField.ConsumerGroup).Resolution.Should().Be(IntegrationFieldResolution.Configured);
        // The platform's expected group is a configured assumption: needs confirmation, never configured.
        var assumed = Hubs with { RuntimeEvidence = new IntegrationRuntimeEvidenceSettings { ExpectedConsumerGroup = "$Default" } };
        Field(IntegrationFieldRules.Assess(Topic(), assumed), IntegrationField.ConsumerGroup).Resolution.Should().Be(IntegrationFieldResolution.NeedsConfirmation);
        var broker = IntegrationFieldRules.Assess(Topic(t => t with { Kind = IntegrationKind.ServiceBus, ConsumerAuthentication = IntegrationAuthMechanism.NotConfigured }), Hubs with { Kind = IntegrationKind.ServiceBus });
        broker.Missing.Select(f => f.Label).Should().Equal(["Consumer authentication"]);
        Field(broker, IntegrationField.ConsumerGroup).Requirement.Should().Be(IntegrationFieldRequirement.Optional);
    }

    // §70 + §40
    [Fact]
    public void ConfigurationSummary_SeparatesMissingConfirmationNotAssessedAndNotApplicable()
    {
        var assessment = IntegrationFieldRules.Assess(Webhook(), null);
        assessment.Missing.Should().BeEmpty();
        assessment.ToConfirm.Select(f => f.Label).Should().Equal(["Consumer mapping"]);
        assessment.NotAssessed.Select(f => f.Label).Should().BeEquivalentTo(["Producer authentication", "Consumer authentication", "Health endpoint"]);
        assessment.NotApplicable.Select(f => f.Label).Should().BeEquivalentTo(["Namespace", "Source resource", "Consumer group", "Worker endpoint"]);
        assessment.Fields.Where(f => f.Resolution == IntegrationFieldResolution.NotConfigured).Should().BeEmpty();
    }

    // §71 + §17 + §41
    [Fact]
    public void ReviewReadiness_IsCausal_AndNotApplicableFieldsNeverLimitIt()
    {
        var readiness = IntegrationReadinessRules.Assess(Webhook(), null, new IntegrationReadinessEvidence { SourceBound = true, BoundArchive = "receiver.zip" });
        readiness.Label.Should().Be("Partial");
        readiness.Capability.Should().Be(IntegrationReviewCapability.HttpApi);
        readiness.Ready.Should().Contain(["Endpoint configured (HTTPS)", "Source evidence bound (receiver.zip)"]);
        readiness.NeedsAttention.Should().Contain(["Consumer mapping needs confirmation", "Contract compatibility not assessed"]);
        readiness.NeedsAttention.Should().NotContain(i => i.Contains("Consumer group") || i.Contains("Worker"));
        readiness.NotBlocking.Should().Contain(["Consumer group not applicable", "Worker endpoint not applicable"]);
        readiness.NotAssessed.Should().Contain(i => i.StartsWith("Producer authentication")).And.Contain(i => i.Contains("sends no request"));
        readiness.Detail.Should().NotContain("not implemented").And.NotContain("Configuration only");
        IntegrationReadinessRules.Assess(Webhook(), null).NeedsAttention.Should().Contain("Source evidence not bound (bind it in IQR Evidence)");
        // Kinds without a review profile say so causally.
        var other = IntegrationReadinessRules.Assess(Webhook(w => w with { Kind = IntegrationKind.Database }), null);
        other.Label.Should().Be("Configuration only");
        other.NeedsAttention.Should().Contain(i => i.Contains("No domain review profile for Database"));
        IntegrationReadinessRules.Assess(Webhook(w => w with { Enabled = false }), null).Label.Should().Be("Not included");
    }

    // §72 + §47
    [Fact]
    public void ConfiguredValueContradictedByEvidence_IsReviewRequired_AndNothingIsOverwritten()
    {
        var definition = Topic(t => t with { ConsumerGroup = "person-adapter" });
        var assessment = IntegrationFieldRules.Assess(definition, Hubs,
        [
            new(IntegrationField.ConsumerGroup, "person-adapter-v2", IntegrationFieldEvidenceOrigin.Source, "worker configuration"),
            new(IntegrationField.ConsumerMapping, "Other adapter", IntegrationFieldEvidenceOrigin.Source, "channel match in the latest analyzed source"),
        ]);
        var group = Field(assessment, IntegrationField.ConsumerGroup);
        group.Resolution.Should().Be(IntegrationFieldResolution.ReviewRequired);
        (group.Value, group.EvidenceValue).Should().Be(("person-adapter", "person-adapter-v2"));
        var mapping = Field(assessment, IntegrationField.ConsumerMapping);
        mapping.Resolution.Should().Be(IntegrationFieldResolution.ReviewRequired);
        mapping.Value.Should().Be("Person adapter");
        mapping.Reason.Should().Contain("Nothing is overwritten");
        IntegrationReadinessRules.Assess(definition, Hubs, new IntegrationReadinessEvidence { FieldEvidence = [new(IntegrationField.ConsumerMapping, "Other adapter", IntegrationFieldEvidenceOrigin.Source, "x")] })
            .NeedsAttention.Should().Contain("Consumer mapping differs from source evidence — review required");
        definition.ConsumerGroup.Should().Be("person-adapter");
    }

    [Fact]
    public void TemplateValuesSavedByAPerson_AreConfiguredWithTemplateProvenance_SeedDefaultsAreTemplateDefaults()
    {
        // A record a person saved from a template is stored configuration; the template is its provenance, never verification.
        var channel = Field(IntegrationFieldRules.Assess(Webhook(w => w with { UserModified = true }), null), IntegrationField.ConfiguredChannel);
        channel.Resolution.Should().Be(IntegrationFieldResolution.Configured);
        channel.Provenance.Should().Be("Template");
        channel.Reason.Should().Contain("not source evidence");
        // An unchanged seeded record carries defaults.
        var seeded = Field(IntegrationFieldRules.Assess(Topic(t => t with { Origin = IntegrationRecordOrigin.Seed }), Hubs), IntegrationField.ConfiguredChannel);
        seeded.Resolution.Should().Be(IntegrationFieldResolution.TemplateDefault);
        seeded.Provenance.Should().Be("Seeded default");
        Field(IntegrationFieldRules.Assess(Topic(t => t with { Origin = IntegrationRecordOrigin.Seed, UserModified = true }), Hubs), IntegrationField.ConfiguredChannel)
            .Resolution.Should().Be(IntegrationFieldResolution.Configured);
        // Authentication and other non-identity fields are configuration, whatever the origin.
        Field(IntegrationFieldRules.Assess(Topic(t => t with { Origin = IntegrationRecordOrigin.Seed }), Hubs), IntegrationField.ProducerAuthentication).Resolution.Should().Be(IntegrationFieldResolution.Configured);
    }

    // §82
    [Fact]
    public void DerivedStatesAreNeverPersisted_WithTheConfiguration()
    {
        var json = JsonSerializer.Serialize(Webhook());
        json.Should().NotContain("NotApplicable").And.NotContain("NotAssessed").And.NotContain("Resolution");
    }

    [Fact]
    public void ReviewCapability_IsOneSharedMapping()
    {
        foreach (var kind in Enum.GetValues<IntegrationKind>())
            BirkNext.Api.Services.Integrations.IntegrationReviewProfiles.Capability(kind).Should().Be(IntegrationReviewCapabilities.Of(kind));
        IntegrationReviewCapabilities.Of(IntegrationKind.HttpApi).Should().Be(IntegrationReviewCapability.HttpApi);
    }
}
