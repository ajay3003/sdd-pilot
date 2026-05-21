namespace BirkNext.Api.GraphQL;

/// <summary>
/// Input for creating multiple scenarios from a completed extraction review session.
/// Raw pasted specification text must not appear in any field of this input.
/// </summary>
public record CreateScenariosInput(
    IReadOnlyList<CreateScenarioInput> Items,
    ExtractionMetadataInput? ExtractionMetadata);

/// <summary>
/// Observability metadata forwarded from the Blazor WASM extraction pipeline to the server.
/// Privacy constraint: carries no text content — only numeric counts or opaque identifiers.
/// SessionId is a client-generated GUID that correlates events within one extraction session;
/// it must never be derived from pasted text and is intentionally omitted from server-side logs.
/// </summary>
public record ExtractionMetadataInput(
    int TotalExtracted,
    int SelectedCount,
    int ExtractionDurationMs,
    string SessionId);
