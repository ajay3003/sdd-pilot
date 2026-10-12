using System.Net;
using System.Text;
using BirkNext.Api.Services.AltinnTesting;
using BirkNext.Api.Services.IntegrationJourneys.Packs.SkoleNaervaer;
using BirkNext.Api.Tests.Services.IntegrationJourneys;
using BirkNext.Integrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace BirkNext.Api.Tests.Services.AltinnTesting;

/// <summary>
/// In-process stand-ins for ID-porten TEST and Altinn TT02. Nothing leaves the process: every request goes through the production
/// <see cref="AltinnTestingRequestGuard"/> into <see cref="FakeAltinn"/>, which records it. The app documents below are SYNTHETIC fixtures
/// shaped like an Altinn 3 app — they are not the real Skolenærvær app contract, which BirkNext must read from uploaded files or TT02.
/// </summary>
internal sealed class AltinnTestKit
{
    public const string AppId = "buf/skolenaervaer";
    public const string AppBase = "https://buf.apps.tt02.altinn.no/buf/skolenaervaer/";
    public const string IdPortenToken = "idporten-access-token-DO-NOT-PERSIST";
    public const string AltinnToken = "altinn-tt02-token-DO-NOT-PERSIST";
    public const string AuthorizationCode = "authorization-code-DO-NOT-PERSIST";
    public const string ClientSecret = "client-secret-DO-NOT-PERSIST";
    public const long PartyId = 50012345;
    public static readonly Guid InstanceGuid = Guid.Parse("6a1b2c3d-0000-4000-8000-000000000001");
    public static readonly Guid DataGuid = Guid.Parse("7b2c3d4e-0000-4000-8000-000000000002");

    public FakeAltinn Http { get; } = new();
    public AltinnTestingOptions Options { get; } = new()
    {
        ApprovedApps = [AppId],
        IdPortenTest = new IdPortenTestOptions { RedirectUri = "http://localhost:5000/api/altinn-testing/sign-in/callback" },
    };
    public AltinnTestingPolicy Policy => new(Options);
    public IHttpClientFactory Factory => new GuardedFactory(Http);

    public AltinnAppContractService Contracts(IntegrationJourneyHarness.FakeMessageFlows flows, IntegrationJourneyHarness.FakeSources? sources = null, TimeProvider? clock = null) =>
        new(Factory, Policy, flows, sources ?? new IntegrationJourneyHarness.FakeSources(), new SkoleNaervaerAltinnForm(), clock ?? TimeProvider.System);

    public IdPortenTestSignIn SignIn(IntegrationJourneyHarness.FakeMessageFlows flows, AltinnTestSessionCache cache, TimeProvider? clock = null, ListLogger<IdPortenTestSignIn>? logger = null) =>
        new(Factory, Policy, flows, cache, clock ?? TimeProvider.System, logger ?? new ListLogger<IdPortenTestSignIn>());

    /// <summary>An IQR Altinn test configuration that satisfies every Rapportmottak prerequisite (references only, no values).</summary>
    public static AltinnTestConfiguration ReadyConfiguration => MessageFlowReviewExamples.SkolenærværInitialConfiguration with
    {
        Environment = ExternalTestEnvironment.Test, AppIdentifier = AppId, AuthenticationType = ExternalAuthenticationType.IDPorten,
        ClientReference = "birknext-test-client", ConfiguredScopes = ["openid", "altinn:instances.read", "altinn:instances.write"],
        ReporterReference = "approved-test-reporter", ReporterApprovedForTest = true, ApprovedSyntheticData = true, SyntheticIdentitySetReference = "testnorge-set-1",
    };

    public static string ApplicationMetadata(string id = AppId, string allowed = "\"application/xml\", \"application/json\"", bool secondFormType = false) => $$"""
        { "id": "{{id}}", "org": "buf",
          "dataTypes": [
            { "id": "model", "allowedContentTypes": [{{allowed}}], "appLogic": { "autoCreate": true, "classRef": "Altinn.App.Models.Skolenaervaer" }, "taskId": "Task_1" },
            {{(secondFormType ? "{ \"id\": \"model2\", \"appLogic\": { \"classRef\": \"Altinn.App.Models.Other\" } }," : "")}}
            { "id": "ref-data-as-pdf", "allowedContentTypes": ["application/pdf"] } ] }
        """;

    /// <summary>
    /// A synthetic schema with the SHAPE of the buf/skolenaervaer form model as published in TT02 (2026-10-12): XSD attributes, root oneOf →
    /// a type that extends MeldingType through allOf, hours at the root as nonNegativeInteger, dates as date. Not a copy of the real schema.
    /// </summary>
    public static string Schema(bool withPlannedHours = true, string dateFormat = "date", bool doubleEncoded = false)
    {
        var schema = $$"""
            { "$schema": "https://json-schema.org/draft/2020-12/schema", "$id": "schema.json",
              "@xsdNamespaces": { "": "https://example.test/skolenaervaer.v2.altinn", "xs": "http://www.w3.org/2001/XMLSchema" },
              "@xsdSchemaAttributes": { "ElementFormDefault": "Qualified", "TargetNamespace": "https://example.test/skolenaervaer.v2.altinn" },
              "@xsdRootElement": "Skolenaervaer", "type": "object", "oneOf": [ { "$ref": "#/$defs/SkolenaervaerType" } ],
              "$defs": {
                "MeldingType": { "properties": { "Meldingshode": { "$ref": "#/$defs/MeldingshodeType" } }, "required": ["Meldingshode"] },
                "MeldingshodeType": { "properties": { "Id": { "type": "string" }, "SendtTidspunkt": { "type": "string", "format": "date-time" } }, "required": ["Id", "SendtTidspunkt"] },
                "SkolenaervaerType": { "allOf": [ { "$ref": "#/$defs/MeldingType" }, { "properties": {
                    "Klient": { "$ref": "#/$defs/KlientType" },
                    "Periode": { "properties": { "StartDato": { "type": "string", "format": "{{dateFormat}}" }, "SluttDato": { "type": "string", "format": "{{dateFormat}}" } }, "required": ["StartDato", "SluttDato"] },
                    {{(withPlannedHours ? "\"AntallTimerEtterPlanen\": { \"@xsdType\": \"nonNegativeInteger\", \"type\": \"number\" }," : "")}}
                    "AntallTimerFaktiskNaervaer": { "@xsdType": "nonNegativeInteger", "type": "number" } }, "required": ["Klient", "Periode"] } ] },
                "KlientType": { "properties": { "Identifikator": { "@xsdMinOccurs": 0, "$ref": "#/$defs/IdentifikatorType" } } },
                "IdentifikatorType": { "properties": { "Fodselsnummer": { "type": "string", "pattern": "\\d{11}" } } } } }
            """;
        return doubleEncoded ? System.Text.Json.JsonSerializer.Serialize(schema) : schema;
    }

    public static string Process(string secondTaskType = "") => $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <bpmn:definitions xmlns:bpmn="http://www.omg.org/spec/BPMN/20100524/MODEL" xmlns:altinn="http://altinn.no/process">
          <bpmn:process id="Process_1" isExecutable="false">
            <bpmn:startEvent id="StartEvent_1" />
            <bpmn:sequenceFlow id="Flow_1" sourceRef="StartEvent_1" targetRef="Task_1" />
            <bpmn:task id="Task_1"><bpmn:extensionElements><altinn:taskExtension><altinn:taskType>data</altinn:taskType></altinn:taskExtension></bpmn:extensionElements></bpmn:task>
            {{Tail(secondTaskType)}}
            <bpmn:endEvent id="EndEvent_1" />
          </bpmn:process>
        </bpmn:definitions>
        """;

    private static string Tail(string type) => type.Length == 0
        ? "<bpmn:sequenceFlow id=\"Flow_2\" sourceRef=\"Task_1\" targetRef=\"EndEvent_1\" />"
        : $"<bpmn:sequenceFlow id=\"Flow_2\" sourceRef=\"Task_1\" targetRef=\"Task_2\" /><bpmn:task id=\"Task_2\"><bpmn:extensionElements><altinn:taskExtension><altinn:taskType>{type}</altinn:taskType></altinn:taskExtension></bpmn:extensionElements></bpmn:task><bpmn:sequenceFlow id=\"Flow_3\" sourceRef=\"Task_2\" targetRef=\"EndEvent_1\" />";

    /// <summary>The standard Altinn app layout, as Source Analysis would read it from an uploaded app repository.</summary>
    public static (string Path, string Content)[] AppFiles(string metadata = "", string schema = "", string process = "", string root = "skolenaervaer-app/App/") =>
    [
        ($"{root}config/applicationmetadata.json", metadata.Length > 0 ? metadata : ApplicationMetadata()),
        ($"{root}models/Skolenaervaer.schema.json", schema.Length > 0 ? schema : Schema()),
        ($"{root}config/process/process.bpmn", process.Length > 0 ? process : Process()),
        ($"{root}logic/Calculation.cs", "class Calculation { }"),
    ];

    /// <summary>A Source Analysis snapshot whose contracts domain holds what the extractor found in <paramref name="files"/>.</summary>
    public static IqrSourceSnapshot Snapshot(IReadOnlyList<(string Path, string Content)> files, string archive = "skolenaervaer-app.zip")
    {
        var id = Guid.NewGuid();
        return new IqrSourceSnapshot
        {
            Id = id, Archive = new(archive, new string('c', 64), 1000), AnalyzedAt = DateTimeOffset.Parse("2026-10-12T09:00:00Z"),
            EvidenceDomains = new BirkNext.SourceDomains.SourceEvidenceDomainsSnapshot
            {
                Contracts = new BirkNext.SourceDomains.ContractEvidence { AltinnApps = AltinnAppSourceExtractor.Extract(files, id, DateTimeOffset.Parse("2026-10-12T09:00:00Z")) },
            },
        };
    }

    /// <summary>The app's source in Source Analysis, a complete contract taken from it and a ready configuration for the harness.</summary>
    public async Task ConfigureReadyAsync(IntegrationJourneyHarness harness)
    {
        harness.MessageFlows.Configuration = ReadyConfiguration;
        harness.Sources.Snapshots.Add(Snapshot(AppFiles()));
        await Contracts(harness.MessageFlows, harness.Sources).UseSourceAnalysisAsync("dev-env", null, default);
    }

    private sealed class GuardedFactory(HttpMessageHandler inner) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new AltinnTestingRequestGuard { InnerHandler = inner }, disposeHandler: false);
    }
}

/// <summary>Answers like ID-porten TEST and an Altinn 3 app in TT02. Records each request (method, URL, auth header presence, body).</summary>
internal sealed class FakeAltinn : HttpMessageHandler
{
    public sealed record Call(HttpMethod Method, Uri Uri, string? Authorization, string? Body);
    public List<Call> Calls { get; } = [];
    /// <summary>Status overrides per stage: discovery, token, exchange, metadata, schema, party, instance, data, validate, next, read.</summary>
    public Dictionary<string, HttpStatusCode> Fail { get; } = new(StringComparer.Ordinal);
    public string ValidationJson { get; set; } = "[]";
    /// <summary>The Altinn Storage GET instance answer (read-only evidence). Defaults to an instance at Task_1.</summary>
    public string StorageInstanceJson { get; set; } = StorageInstances.ActiveTask();
    public string DiscoveryIssuer { get; set; } = "https://test.idporten.no";
    public string AuthorizationEndpoint { get; set; } = "https://login.test.idporten.no/authorize";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Calls.Add(new Call(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body));
        var url = request.RequestUri!.ToString();
        var instance = $"{AltinnTestKit.AppBase}instances/{AltinnTestKit.PartyId}/{AltinnTestKit.InstanceGuid:D}";
        var (stage, json) = (request.Method.Method, url) switch
        {
            ("GET", "https://test.idporten.no/.well-known/openid-configuration") => ("discovery",
                $$"""{"issuer":"{{DiscoveryIssuer}}","authorization_endpoint":"{{AuthorizationEndpoint}}","token_endpoint":"https://test.idporten.no/token","token_endpoint_auth_methods_supported":["client_secret_post","none"],"response_modes_supported":["query","form_post"]}"""),
            ("POST", "https://test.idporten.no/token") => ("token", $$"""{"access_token":"{{AltinnTestKit.IdPortenToken}}","expires_in":120,"id_token":"not-used"}"""),
            ("GET", AltinnTestingPolicy.AltinnTokenExchange) => ("exchange", $"\"{AltinnTestKit.AltinnToken}\""),
            ("GET", $"{AltinnTestKit.AppBase}api/v1/applicationmetadata") => ("metadata", AltinnTestKit.ApplicationMetadata()),
            ("GET", $"{AltinnTestKit.AppBase}api/jsonschema/model") => ("schema", AltinnTestKit.Schema(doubleEncoded: true)),
            ("GET", $"{AltinnTestKit.AppBase}api/authorization/parties/current?returnPartyObject=true") => ("party", $$"""{"partyId":{{AltinnTestKit.PartyId}},"name":"Synthetic reporter"}"""),
            ("POST", var u) when u == $"{AltinnTestKit.AppBase}instances?instanceOwnerPartyId={AltinnTestKit.PartyId}" => ("instance", Instance(ended: false)),
            ("PUT", var u) when u == $"{instance}/data/{AltinnTestKit.DataGuid:D}" => ("data", "{}"),
            ("GET", var u) when u == $"{instance}/validate" => ("validate", ValidationJson),
            ("PUT", var u) when u == $"{instance}/process/next" => ("next", """{"currentTask":null,"ended":"2026-10-12T10:00:00Z","endEvent":"EndEvent_1"}"""),
            ("GET", var u) when u == instance => ("read", Instance(ended: true)),
            ("GET", var u) when u == $"{AltinnTestingPolicy.Tt02StorageInstances}{AltinnTestKit.PartyId}/{AltinnTestKit.InstanceGuid:D}" => ("storage", StorageInstanceJson),
            _ => ("unknown", ""),
        };
        if (stage == "unknown") return new HttpResponseMessage(HttpStatusCode.NotFound);
        if (Fail.TryGetValue(stage, out var status)) return new HttpResponseMessage(status) { Content = new StringContent("""{"title":"failure with a SECRET-LOOKING body"}""") };
        if ((stage is "exchange") && request.Headers.Authorization?.Parameter != AltinnTestKit.IdPortenToken) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        if (stage is "party" or "instance" or "data" or "validate" or "next" or "read" or "storage" && request.Headers.Authorization?.Parameter != AltinnTestKit.AltinnToken)
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        return new HttpResponseMessage(stage == "instance" ? HttpStatusCode.Created : HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static string Instance(bool ended) => $$"""
        {"id":"{{AltinnTestKit.PartyId}}/{{AltinnTestKit.InstanceGuid:D}}","instanceOwner":{"partyId":"{{AltinnTestKit.PartyId}}"},
         "data":[{"id":"{{AltinnTestKit.DataGuid:D}}","dataType":"model"}],
         "process":{{(ended ? """{"currentTask":null,"ended":"2026-10-12T10:00:00Z","endEvent":"EndEvent_1"}""" : """{"currentTask":{"elementId":"Task_1"}}""")}},
         "lastChanged":"2026-10-12T10:00:00Z"}
        """;
}

/// <summary>Collects formatted log messages so tests can prove secrets never reach a log.</summary>
internal sealed class ListLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    public List<string> Messages { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Messages.Add(formatter(state, exception));
}

/// <summary>A clock tests move by hand.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// Altinn Storage instance documents shaped like Storage's Instance model (synthetic values only). Owner fields are present on purpose, with
/// synthetic marker values, so tests can prove they never reach evidence or history.
/// </summary>
internal static class StorageInstances
{
    public const string OwnerPersonNumber = "01821599999";
    public const string OwnerOrganisationNumber = "991234567";
    public const string OwnerUsername = "synthetic-owner-user";
    public const string OwnerExternalId = "urn:altinn:person:idporten-email:synthetic@example.test";
    public const string ContentHash = "md5:c3ludGhldGljLWNvbnRlbnQtaGFzaA==";
    public static readonly Guid AttachmentGuid = Guid.Parse("8c3d4e5f-0000-4000-8000-000000000003");

    private static string Owner => $$"""
        "instanceOwner": { "partyId": "{{AltinnTestKit.PartyId}}", "personNumber": "{{OwnerPersonNumber}}", "organisationNumber": "{{OwnerOrganisationNumber}}",
                           "username": "{{OwnerUsername}}", "externalIdentifier": "{{OwnerExternalId}}" }
        """;

    private static string Data(Guid id, string dataType, string contentType, long size, bool locked, string? refs = null) => $$"""
        { "id": "{{id:D}}", "instanceGuid": "{{AltinnTestKit.InstanceGuid:D}}", "dataType": "{{dataType}}", "filename": "{{OwnerPersonNumber}}.xml", "contentType": "{{contentType}}",
          "blobStoragePath": "buf/skolenaervaer/{{AltinnTestKit.InstanceGuid:D}}/data/{{id:D}}", "size": {{size}}, "contentHash": "{{ContentHash}}", "locked": {{(locked ? "true" : "false")}},
          "refs": [{{refs ?? ""}}], "isRead": true, "metadata": [ { "key": "source", "value": "{{OwnerPersonNumber}}" } ],
          "created": "2026-10-12T09:00:00Z", "lastChanged": "2026-10-12T09:05:00Z" }
        """;

    public static string ActiveTask(string dataType = "model", string id = "") => $$"""
        { "id": "{{(id.Length > 0 ? id : $"{AltinnTestKit.PartyId}/{AltinnTestKit.InstanceGuid:D}")}}", "appId": "buf/skolenaervaer", "org": "buf", {{Owner}},
          "created": "2026-10-12T09:00:00Z", "lastChanged": "2026-10-12T09:05:00Z", "status": { "isArchived": false, "readStatus": "Unread" },
          "process": { "started": "2026-10-12T09:00:01Z", "startEvent": "StartEvent_1",
            "currentTask": { "flow": 2, "started": "2026-10-12T09:00:01Z", "elementId": "Task_1", "name": "Utfylling", "altinnTaskType": "data",
                             "ended": null, "validated": { "timestamp": "2026-10-12T09:04:00Z", "canCompleteTask": true }, "flowType": "CompleteCurrentMoveToNext" },
            "ended": null, "endEvent": null },
          "data": [ {{Data(AltinnTestKit.DataGuid, dataType, "application/xml", 1532, false)}} ] }
        """;

    public static string Completed() => $$"""
        { "id": "{{AltinnTestKit.PartyId}}/{{AltinnTestKit.InstanceGuid:D}}", "appId": "buf/skolenaervaer", "org": "buf", {{Owner}},
          "created": "2026-10-12T09:00:00Z", "lastChanged": "2026-10-12T09:10:00Z",
          "process": { "started": "2026-10-12T09:00:01Z", "startEvent": "StartEvent_1", "currentTask": null, "ended": "2026-10-12T09:10:00Z", "endEvent": "EndEvent_1" },
          "data": [ {{Data(AltinnTestKit.DataGuid, "model", "application/xml", 1532, true)}},
                    {{Data(AttachmentGuid, "ref-data-as-pdf", "application/pdf", 48211, true, $"\"{AltinnTestKit.DataGuid:D}\"")}} ] }
        """;
}
