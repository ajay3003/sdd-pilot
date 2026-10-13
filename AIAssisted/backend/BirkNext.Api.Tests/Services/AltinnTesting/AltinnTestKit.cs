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
    public const string ClientId = "birknext-test-client";
    public const string ReporterAlias = "reporter-a";
    public const string HeadlessRedirect = "https://birknext.test/idporten/headless-redirect";
    public const string HeadlessCode = "headless-authorization-code-DO-NOT-PERSIST";
    /// <summary>A synthetic (month + 80, valid control digits) identifier made up for these in-process fakes; it is never sent anywhere.</summary>
    public static readonly string ReporterIdentifier = FirstSynthetic("0384");
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

    /// <summary>Approved synthetic reporter, ID-porten TEST client secret and registered redirect for unattended authentication; optionally the
    /// service owner's app-policy confirmation (without it, submissions stay Not ready).</summary>
    public AltinnTestKit ConfigureUnattended(bool confirmPolicy = true, bool enabled = true, bool secret = true, bool reporter = true, bool profile = true)
    {
        Options.UnattendedIdPortenTest = new UnattendedIdPortenTestOptions
        {
            Enabled = enabled, RedirectUri = HeadlessRedirect, ClientSecret = secret ? ClientSecret : null,
            Reporters = reporter ? [new SyntheticReporterOptions { Alias = ReporterAlias, Label = "Skolenærvær reporter A", TestIdPersonIdentifier = ReporterIdentifier, AltinnProfileConfirmed = profile }] : [],
        };
        Options.ConfirmedAppPolicies = confirmPolicy
            ? [new AltinnAppPolicyConfirmation { AppId = AppId, Reference = "TEST-POLICY-1", ConfirmedBy = "service owner (test)", ConfirmedOn = new DateOnly(2026, 10, 13) }]
            : [];
        return this;
    }

    public UnattendedIdPortenTestAuthentication Unattended(AltinnTestSessionCache cache, TimeProvider? clock = null, ListLogger<UnattendedIdPortenTestAuthentication>? logger = null) =>
        new(Factory, Policy, cache, clock ?? TimeProvider.System, logger ?? new ListLogger<UnattendedIdPortenTestAuthentication>());

    /// <summary>The production authenticator with both providers: interactive (the given held tokens) and unattended (in-process fakes).</summary>
    public AltinnTestAuthenticator Authenticator(IAltinnTestTokens tokens, AltinnTestSessionCache? cache = null, TimeProvider? clock = null,
        ListLogger<UnattendedIdPortenTestAuthentication>? logger = null) =>
        new([new InteractiveIdPortenTestAuthentication(tokens, clock ?? TimeProvider.System), Unattended(cache ?? new AltinnTestSessionCache(clock ?? TimeProvider.System), clock, logger)]);

    private static string FirstSynthetic(string dayMonth)
    {
        for (var individual = 500; individual < 1000; individual++)
            for (var k = 0; k < 100; k++)
            {
                var candidate = $"{dayMonth}10{individual:000}{k:00}";
                if (SyntheticPersonIdentifier.IsSynthetic(candidate)) return candidate;
            }
        throw new InvalidOperationException("No synthetic identifier found.");
    }

    public AltinnAppContractService Contracts(IntegrationJourneyHarness.FakeMessageFlows flows, IntegrationJourneyHarness.FakeSources? sources = null, TimeProvider? clock = null) =>
        new(Factory, Policy, flows, sources ?? new IntegrationJourneyHarness.FakeSources(), new SkoleNaervaerAltinnForm(), clock ?? TimeProvider.System,
            new BirkNext.Api.Services.ContractAnalysis.OpenApiExtractor(NullLogger<BirkNext.Api.Services.ContractAnalysis.OpenApiExtractor>.Instance));

    public IdPortenTestSignIn SignIn(IntegrationJourneyHarness.FakeMessageFlows flows, AltinnTestSessionCache cache, TimeProvider? clock = null, ListLogger<IdPortenTestSignIn>? logger = null) =>
        new(Factory, Policy, flows, cache, clock ?? TimeProvider.System, logger ?? new ListLogger<IdPortenTestSignIn>());

    /// <summary>An IQR Altinn test configuration that satisfies every Rapportmottak prerequisite (references only, no values).</summary>
    public static AltinnTestConfiguration ReadyConfiguration => MessageFlowReviewExamples.SkolenærværInitialConfiguration with
    {
        Environment = ExternalTestEnvironment.Test, AppIdentifier = AppId, AuthenticationType = ExternalAuthenticationType.IDPorten,
        ClientReference = ClientId, ConfiguredScopes = ["openid", "altinn:instances.read", "altinn:instances.write"],
        ReporterReference = "approved-test-reporter", ReporterApprovedForTest = true, ApprovedSyntheticData = true, SyntheticIdentitySetReference = "testnorge-set-1",
    };

    public static string ApplicationMetadata(string id = AppId, string allowed = "\"application/xml\", \"application/json\"", bool secondFormType = false, string formTask = "Task_1") => $$"""
        { "id": "{{id}}", "org": "buf",
          "dataTypes": [
            { "id": "model", "allowedContentTypes": [{{allowed}}], "appLogic": { "autoCreate": true, "classRef": "Altinn.App.Models.Skolenaervaer" }, "taskId": "{{formTask}}", "maxCount": 1 },
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

    /// <summary>The data type id the real buf/skolenaervaer app binds its form to (public TT02 metadata and deployed OpenAPI, read 2026-10-13).</summary>
    public const string RealFormDataType = "bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn";

    /// <summary>A synthetic stand-in for the app's XACML policy document (only its exact bytes matter: BirkNext keeps their SHA-256).</summary>
    public const string PolicyXml = "<xacml:Policy PolicyId=\"urn:altinn:policyid:test\" xmlns:xacml=\"urn:oasis:names:tc:xacml:3.0:core:schema:wd-17\" />";
    public static string PolicySha256(string xml = PolicyXml) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(xml))).ToLowerInvariant();

    /// <summary>One process-task row exactly as app-lib's generated OpenAPI writes it (an empty action list is an empty cell).</summary>
    public const string RealTaskRow = "| Task_1 | Utfylling | data |  |";

    /// <summary>
    /// An app's deployed OpenAPI document (v1/customOpenapi.json) in the shape app-lib 8 generates — including its numeric (integer) enums,
    /// which the real TT02 document has (ReadStatus, ValidationIssueSeverity): the data-type and process-task tables in
    /// info.description and the process/next operation with its AppProcessState response. Defaults mirror the facts of the real
    /// buf/skolenaervaer TT02 document (one data task Task_1 · Utfylling, no actions, optional body); only what BirkNext reads is included.
    /// </summary>
    public static string DeployedOpenApi(string dataType = "model", string taskRows = RealTaskRow, bool processNext = true, bool bodyRequired = false,
        string stateFields = "\"started\": {}, \"startEvent\": {}, \"currentTask\": {}, \"ended\": {}, \"endEvent\": {}, \"actions\": {}", string app = AppId, bool taskTable = true,
        string validatePath = "validate", string appLib = "8.12.7.264", string formTask = "Task_1", string partyParam = "instanceOwnerPartyId")
    {
        // The instance, data and validation operations the real document declares (the submission uses them; their paths come from here).
        var instance = $"/{app}/instances/{{{partyParam}}}/{{instanceGuid}}";
        var operations = $$"""
            ,"{{instance}}": { "get": { "summary": "Get instance", "responses": { "200": { "description": "OK" } } } }
            ,"{{instance}}/data": { "post": { "summary": "Create data element", "responses": { "201": { "description": "Created" } } } }
            ,"{{instance}}/data/{dataGuid}": { "put": { "summary": "Update data element", "responses": { "201": { "description": "Updated" } } } }
            ,"{{instance}}/{{validatePath}}": { "get": { "summary": "Validate instance", "responses": { "200": { "description": "OK" } } } }
            """;
        var next = processNext ? $$"""
            ,"{{instance}}/process/next": { "put": { "summary": "Move instance to next process step",
                "requestBody": { "description": "Optional body with specification of the action to perform", {{(bodyRequired ? "\"required\": true," : "")}}
                  "content": { "application/json": { "schema": { "$ref": "#/components/schemas/ProcessNext" } } } },
                "responses": { "200": { "description": "Instance moved to next process step. Returns the new process state",
                  "content": { "application/json": { "schema": { "$ref": "#/components/schemas/AppProcessState" } } } }, "409": { "description": "Problem details" } } } }
            """ : "";
        // JSON escapes (\r\n), not control characters: the table lives inside the description string.
        var rows = taskRows.Replace("\n", @"\n", StringComparison.Ordinal);
        var tasks = taskTable ? @"\r\n## This app has the following process tasks:\r\n| TaskId | Name | Type | Actions |\r\n|--------|------|------|---------|\r\n" + rows + @"\n" : "";
        return $$"""
            { "openapi": "3.0.4",
              "info": { "title": "Altinn 3 App API for School attendance", "version": "{{appLib}}",
                "description": "App API description\r\n\nThis app has the following data types:\n| DataTypeId | Type | Allowed number | MimeTypes | TaskId |\n|------------|------|----------------|-----------|--------|\n|ref-data-as-pdf|Attachment|-|application/pdf||\n|{{dataType}}|FormData (AutoCreate)|1|application/xml|{{formTask}}|\n{{tasks}}" },
              "paths": {
                "/{{app}}/instances": { "post": { "summary": "Create new instance", "responses": { "201": { "description": "Instance created" } } } }
                {{operations}}
                {{next}} },
              "components": { "schemas": {
                "ProcessNext": { "type": "object", "properties": { "action": { "type": "string", "nullable": true }, "actionOnBehalfOf": { "type": "string", "nullable": true } }, "additionalProperties": false },
                "ReadStatus": { "enum": [ 0, 1, 2 ], "type": "integer", "format": "int32" },
                "ValidationIssueSeverity": { "enum": [ 0, 1, 2, 3, 4, 5 ], "type": "integer", "format": "int32" },
                "InstanceStatus": { "type": "object", "properties": { "readStatus": { "$ref": "#/components/schemas/ReadStatus" }, "isArchived": { "type": "boolean" } }, "additionalProperties": false },
                "AppProcessState": { "type": "object", "properties": { {{stateFields}} }, "additionalProperties": false } } } }
            """;
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
        ConfigureUnattended();
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
    /// <summary>The app's deployed OpenAPI document (v1/customOpenapi.json); null answers 404 like an app that does not serve one.</summary>
    public string? OpenApiJson { get; set; } = AltinnTestKit.DeployedOpenApi();
    /// <summary>The app's application metadata (api/v1/applicationmetadata).</summary>
    public string MetadataJson { get; set; } = AltinnTestKit.ApplicationMetadata();
    /// <summary>The app's authorization policy as the TT02 Resource Registry serves it (anonymous); null answers 404.</summary>
    public string? PolicyXml { get; set; } = AltinnTestKit.PolicyXml;
    /// <summary>The validation path segment the app serves (changes together with the deployed document's validate path).</summary>
    public string ValidatePath { get; set; } = "validate";
    /// <summary>The live instance read before process/next (defaults to an active instance at Task_1, a completable data task).</summary>
    public string? LiveInstanceJson { get; set; }
    /// <summary>The AppProcessState process/next returns (defaults to an ended process).</summary>
    public string NextJson { get; set; } = """{"started":"2026-10-12T09:00:01Z","currentTask":null,"ended":"2026-10-12T10:00:00Z","endEvent":"EndEvent_1"}""";
    /// <summary>Set once process/next has answered successfully: later instance reads return the completed instance.</summary>
    public bool Advanced { get; private set; }
    /// <summary>The Altinn Storage GET instance answer (read-only evidence). Defaults to an instance at Task_1.</summary>
    public string StorageInstanceJson { get; set; } = StorageInstances.ActiveTask();
    public string DiscoveryIssuer { get; set; } = "https://test.idporten.no";
    public string AuthorizationEndpoint { get; set; } = "https://login.test.idporten.no/authorize";
    public string[] AuthMethods { get; set; } = ["client_secret_basic", "client_secret_post", "none"];
    /// <summary>How the headless /authorize answers: ok, error, wrong-state, wrong-iss, foreign-redirect, no-redirect, no-code, no-location.</summary>
    public string Headless { get; set; } = "ok";
    /// <summary>id_token claim overrides (null value removes the claim).</summary>
    public Dictionary<string, object?> IdTokenClaims { get; } = new(StringComparer.Ordinal);
    public bool TokenMalformed { get; set; }
    public int TokenExpiresIn { get; set; } = 120;
    public Dictionary<string, string> LastAuthorize { get; private set; } = new(StringComparer.Ordinal);
    private int _codeUses;

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
                $$"""{"issuer":"{{DiscoveryIssuer}}","authorization_endpoint":"{{AuthorizationEndpoint}}","token_endpoint":"https://test.idporten.no/token","token_endpoint_auth_methods_supported":[{{string.Join(",", AuthMethods.Select(item => $"\"{item}\""))}}],"response_modes_supported":["query","form_post"]}"""),
            ("GET", var u) when u.StartsWith("https://login.test.idporten.no/authorize?", StringComparison.Ordinal) => ("authorize", ""),
            ("POST", "https://test.idporten.no/token") => ("token", $$"""{"access_token":"{{AltinnTestKit.IdPortenToken}}","expires_in":120,"id_token":"not-used"}"""),
            ("GET", AltinnTestingPolicy.AltinnTokenExchange) => ("exchange", $"\"{AltinnTestKit.AltinnToken}\""),
            ("GET", $"{AltinnTestKit.AppBase}api/v1/applicationmetadata") => ("metadata", MetadataJson),
            ("GET", $"{AltinnTestKit.AppBase}api/jsonschema/model") => ("schema", AltinnTestKit.Schema(doubleEncoded: true)),
            ("GET", $"{AltinnTestKit.AppBase}v1/customOpenapi.json") when OpenApiJson is not null => ("openapi", OpenApiJson),
            ("GET", "https://platform.tt02.altinn.no/resourceregistry/api/v1/resource/app_buf_skolenaervaer/policy") when PolicyXml is not null => ("policy", PolicyXml),
            ("GET", $"{AltinnTestKit.AppBase}api/authorization/parties/current?returnPartyObject=true") => ("party", $$"""{"partyId":{{AltinnTestKit.PartyId}},"name":"Synthetic reporter"}"""),
            ("POST", var u) when u == $"{AltinnTestKit.AppBase}instances?instanceOwnerPartyId={AltinnTestKit.PartyId}" => ("instance", Instance(ended: false)),
            ("PUT", var u) when u == $"{instance}/data/{AltinnTestKit.DataGuid:D}" => ("data", "{}"),
            ("GET", var u) when u == $"{instance}/{ValidatePath}" => ("validate", ValidationJson),
            ("PUT", var u) when u == $"{instance}/process/next" => ("next", NextJson),
            ("GET", var u) when u == instance && !Advanced => ("live", LiveInstanceJson ?? Instance(ended: false)),
            ("GET", var u) when u == instance => ("read", Instance(ended: true)),
            ("GET", var u) when u == $"{AltinnTestingPolicy.Tt02StorageInstances}{AltinnTestKit.PartyId}/{AltinnTestKit.InstanceGuid:D}" => ("storage", StorageInstanceJson),
            _ => ("unknown", ""),
        };
        if (stage == "unknown") return new HttpResponseMessage(HttpStatusCode.NotFound);
        if (stage == "authorize" && !Fail.ContainsKey(stage)) return Authorize(request.RequestUri!);
        if (stage == "token" && request.Headers.Authorization?.Scheme == "Basic" && !Fail.ContainsKey(stage)) return HeadlessToken(request.Headers.Authorization.Parameter, body ?? "");
        if (Fail.TryGetValue(stage, out var status)) return new HttpResponseMessage(status) { Content = new StringContent("""{"title":"failure with a SECRET-LOOKING body"}""") };
        if ((stage is "exchange") && request.Headers.Authorization?.Parameter != AltinnTestKit.IdPortenToken) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        if (stage is "party" or "instance" or "data" or "validate" or "next" or "live" or "read" or "storage" && request.Headers.Authorization?.Parameter != AltinnTestKit.AltinnToken)
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        if (stage == "next") Advanced = true;
        if (stage == "instance") Advanced = false;
        return new HttpResponseMessage(stage == "instance" ? HttpStatusCode.Created : HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    /// <summary>ID-porten TEST headless login: 302 to the registered redirect with code, state and iss (docs.digdir.no oidc_func_apitesting).</summary>
    private HttpResponseMessage Authorize(Uri uri)
    {
        _codeUses = 0;
        LastAuthorize = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query).ToDictionary(item => item.Key, item => item.Value.ToString(), StringComparer.Ordinal);
        var redirect = LastAuthorize.GetValueOrDefault("redirect_uri") ?? "";
        var state = LastAuthorize.GetValueOrDefault("state") ?? "";
        string Location(string target, string query) => $"{target}?{query}";
        var location = Headless switch
        {
            "error" => Location(redirect, $"error=access_denied&state={Uri.EscapeDataString(state)}"),
            "wrong-state" => Location(redirect, $"code={AltinnTestKit.HeadlessCode}&state=forged&iss=https%3A%2F%2Ftest.idporten.no"),
            "wrong-iss" => Location(redirect, $"code={AltinnTestKit.HeadlessCode}&state={Uri.EscapeDataString(state)}&iss=https%3A%2F%2Fevil.example"),
            "foreign-redirect" => Location("https://attacker.example/cb", $"code={AltinnTestKit.HeadlessCode}&state={Uri.EscapeDataString(state)}"),
            "no-code" => Location(redirect, $"state={Uri.EscapeDataString(state)}&iss=https%3A%2F%2Ftest.idporten.no"),
            _ => Location(redirect, $"code={AltinnTestKit.HeadlessCode}&state={Uri.EscapeDataString(state)}&iss=https%3A%2F%2Ftest.idporten.no"),
        };
        if (Headless == "no-redirect") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>TestID login page</html>") };
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        if (Headless != "no-location") response.Headers.Location = new Uri(location);
        return response;
    }

    /// <summary>The token endpoint for client_secret_basic: checks the client, code (single use), redirect and PKCE verifier like ID-porten would.</summary>
    private HttpResponseMessage HeadlessToken(string? basic, string body)
    {
        var form = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery("?" + body);
        var credentials = Encoding.UTF8.GetString(Convert.FromBase64String(basic ?? ""));
        var challenge = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"].ToString()))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        if (credentials != $"{AltinnTestKit.ClientId}:{AltinnTestKit.ClientSecret}" || form["grant_type"] != "authorization_code" || form["code"] != AltinnTestKit.HeadlessCode
            || form["redirect_uri"] != LastAuthorize.GetValueOrDefault("redirect_uri") || challenge != LastAuthorize.GetValueOrDefault("code_challenge") || ++_codeUses > 1)
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"invalid_grant"}""") };
        if (TokenMalformed) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json") };
        var claims = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["iss"] = "https://test.idporten.no", ["aud"] = AltinnTestKit.ClientId, ["nonce"] = LastAuthorize.GetValueOrDefault("nonce"),
            ["amr"] = new[] { "TestID" }, ["pid"] = AltinnTestKit.ReporterIdentifier, ["acr"] = "idporten-loa-substantial",
        };
        foreach (var (key, value) in IdTokenClaims) { if (value is null) claims.Remove(key); else claims[key] = value; }
        static string Part(object value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(value))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var idToken = $"{Part(new { alg = "RS256", typ = "JWT" })}.{Part(claims)}.signature";
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{"access_token":"{{AltinnTestKit.IdPortenToken}}","token_type":"Bearer","expires_in":{{TokenExpiresIn}},"refresh_token":"refresh-DO-NOT-PERSIST","id_token":"{{idToken}}"}""", Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>An active process at a task, shaped like Altinn's ProcessState (elementId, name, altinnTaskType, validated).</summary>
    public static string ActiveProcess(string task = "Task_1", string type = "data", bool? canComplete = true) =>
        $$"""{"started":"2026-10-12T09:00:01Z","startEvent":"StartEvent_1","currentTask":{"elementId":"{{task}}","name":"Utfylling","altinnTaskType":"{{type}}"{{(canComplete is { } can ? $$""","validated":{"canCompleteTask":{{(can ? "true" : "false")}}}""" : "")}}},"ended":null,"endEvent":null}""";

    /// <summary>The live instance with the given process JSON (for pre-transition checks).</summary>
    public static string InstanceWith(string process) => $$"""
        {"id":"{{AltinnTestKit.PartyId}}/{{AltinnTestKit.InstanceGuid:D}}","data":[{"id":"{{AltinnTestKit.DataGuid:D}}","dataType":"model"}],"process":{{process}},"lastChanged":"2026-10-12T10:00:00Z"}
        """;

    private static string Instance(bool ended) => $$"""
        {"id":"{{AltinnTestKit.PartyId}}/{{AltinnTestKit.InstanceGuid:D}}","instanceOwner":{"partyId":"{{AltinnTestKit.PartyId}}"},
         "data":[{"id":"{{AltinnTestKit.DataGuid:D}}","dataType":"model"}],
         "process":{{(ended ? """{"currentTask":null,"ended":"2026-10-12T10:00:00Z","endEvent":"EndEvent_1"}""" : ActiveProcess())}},
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
