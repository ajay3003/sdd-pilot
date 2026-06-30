# Data Model: M2LB.Revisjon M01

**Branch**: `001-M2LB.Revisjon-m01` | **Date**: 2026-04-27

M2LB.Revisjon has exactly one entity: `LeseloggHendelse`. There is no database, no outbox,
and no local state. This document describes the entity shape, the derived blob path, and the
domain interface boundary.

---

## Entity: LeseloggHendelse

**Defined by**: GL-32 (platform development guidelines)  
**Source**: Azure Service Bus queue `leselogg`  
**Stored as**: One JSON file per event in Azure Immutable Blob Storage

```csharp
// M2LB.Revisjon.Domain/LeseloggHendelse.cs
public record LeseloggHendelse(
    Guid HendelsesId,
    DateTimeOffset HendelsesTidspunkt,
    Guid BrukerId,
    Guid BarnId,
    string OperasjonNavn,
    string Tjenestenavn,
    Guid KorrelasjonId
);
```

| Field | Type | Required | Description |
|---|---|---|---|
| `HendelsesId` | `Guid` (UUID v4) | Yes | Unique event ID. Used as blob filename and idempotency key. |
| `HendelsesTidspunkt` | `DateTimeOffset` (ISO 8601 + tz) | Yes | When the operation occurred. UTC-normalised to derive the folder path. |
| `BrukerId` | `Guid` (UUID v4) | Yes | Entra Object ID of the user who performed the lookup. |
| `BarnId` | `Guid` (UUID v4) | Yes | Internal M2LB UUID of the child (from Personmodulen). |
| `OperasjonNavn` | `string` | Yes | Operation performed, e.g. `"Henvisning:Se"`. Free text — not validated. |
| `Tjenestenavn` | `string` | Yes | Service that published the event, e.g. `"Henvisningstjenesten"`. Free text. |
| `KorrelasjonId` | `Guid` (UUID v4) | Yes | Trace ID for end-to-end correlation in Azure Monitor. Used as structured log scope key. |

**Invariants**:
- No field may contain personal data (names, national identity numbers, addresses). Enforced by
  the publishing service (GL-32); M2LB.Revisjon does not validate content.
- All fields are required. A message missing any field cannot be deserialised to `LeseloggHendelse`
  and MUST be routed to the dead letter queue (FR-002).
- `HendelsesId` is globally unique across all events. Duplicate detection relies on Blob
  Storage returning HTTP 412 on `IfNoneMatch: *` (FR-005).

---

## Derived Value: BlobPath

The blob file path is computed from the event — it is never stored separately.

**Formula**: `{year}/{month:D2}/{day:D2}/{hendelsesId}.json`

Where `year`, `month`, and `day` are extracted from `HendelsesTidspunkt` **after normalisation
to UTC** (`HendelsesTidspunkt.ToUniversalTime()`).

```csharp
// M2LB.Revisjon.Domain/BlobPath.cs
public static class BlobPath
{
    public static string Compute(LeseloggHendelse hendelse)
    {
        var utc = hendelse.HendelsesTidspunkt.ToUniversalTime();
        return $"{utc.Year}/{utc.Month:D2}/{utc.Day:D2}/{hendelse.HendelsesId}.json";
    }
}
```

**Examples**:

| HendelsesTidspunkt | UTC equivalent | BlobPath |
|---|---|---|
| `2026-03-12T10:23:45.123+01:00` | `2026-03-12T09:23:45.123Z` | `2026/03/12/{id}.json` |
| `2026-03-12T00:05:00.000+01:00` | `2026-03-11T23:05:00.000Z` | `2026/03/11/{id}.json` |
| `2026-01-05T08:00:00.000Z` | `2026-01-05T08:00:00.000Z` | `2026/01/05/{id}.json` |

**Critical edge case**: A timestamp at `00:05 CET` crosses midnight in UTC → stored under the
previous day's folder. This is correct and intentional (spec FR-003, Clarifications 2026-04-27).

---

## Domain Interface: IBlobLeseLoggCreator

```csharp
// M2LB.Revisjon.Domain/IBlobLeseLoggCreator.cs
public interface IBlobLeseLoggCreator
{
    /// <summary>
    /// Writes rawJson to the blob at blobPath.
    /// Returns true if written (HTTP 201).
    /// Returns false if already exists (HTTP 412 — idempotent success).
    /// Throws on transient failure (caller handles retry via Wolverine policy).
    /// </summary>
    Task<bool> WriteAsync(string blobPath, byte[] rawJson, CancellationToken ct = default);
}
```

**Design notes**:
- The interface accepts `byte[]` (raw bytes from Service Bus), not a `LeseloggHendelse` object.
  This ensures FR-004 (no transformation) is enforced at the domain boundary.
- Returns `bool` (written vs already-existed) so the caller can log the distinction without
  the interface itself needing to know about structured logging.
- `string blobPath` is computed by the caller using `BlobPath.Compute(hendelse)` before calling
  this interface — keeps the interface focused on storage, not path logic.
- Transient errors (`RequestFailedException` with 5xx/429) propagate as exceptions.
  HTTP 412 is caught internally and returns `false`.

---

## Infrastructure Implementation: AzureBlobLeseLoggCreator

```csharp
// M2LB.Revisjon.Infrastructure/AzureBlobLeseLoggCreator.cs
public sealed class AzureBlobLeseLoggCreator(BlobContainerClient container) : IBlobLeseLoggCreator
{
    public async Task<bool> WriteAsync(string blobPath, byte[] rawJson,
        CancellationToken ct = default)
    {
        var blob = container.GetBlobClient(blobPath);
        var options = new BlobUploadOptions
        {
            Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" }
        };
        try
        {
            await blob.UploadAsync(BinaryData.FromBytes(rawJson), options, ct);
            return true;   // HTTP 201 — written
        }
        catch (RequestFailedException ex) when (ex.Status == 412)
        {
            return false;  // HTTP 412 — already exists, idempotent success
        }
    }
}
```

---

## Non-Entities (explicitly absent)

| Concept | Status | Reason |
|---|---|---|
| Outbox row | **Absent** | No SQL database (FR-011). Outbox pattern (GL-33) applies to source services only. |
| Event tracking table | **Absent** | Idempotency is provided by Blob Storage (`IfNoneMatch: *`), not by a local tracking table. |
| LeseloggHendelse read model | **Absent** | Service is write-only in M01 (spec Assumptions). |
| Dead letter queue entry | **Absent** (as code) | Service Bus's built-in DLQ. No code model needed; the service calls `DeadLetterAsync` via Wolverine. |
