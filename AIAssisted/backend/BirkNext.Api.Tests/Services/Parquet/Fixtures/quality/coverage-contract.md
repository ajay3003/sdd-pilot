# Contract: Synthetic coverage delivery (test fixture)

**Consumer**: Synthetic analytics consumer
**Form**: Parquet files in a synthetic storage account
**Contract version**: `1.0.0-draft`. Planning baseline for tests. Renamed from `0.9.0-utkast` → `1.0.0-draft` (`periode` → `period`, `TOTALT` → `TOTAL`, `FORVENTET` → `EXPECTED`).

## Location and access

| | |
|---|---|
| Containers | `synthetic-coverage` (coverage) |
| Path | `period=YYYY-MM/data.parquet` and `period=YYYY-MM/_manifest.json` |
| Immutability | Files are written once and never change |

## Coverage — `synthetic-coverage/period=YYYY-MM/data.parquet`

Long format, sorted by
`category`, `measure`, `reason_code`.
Never present: a child's fødselsnummer or any other personal identification number.
Protected children (Kode 6/7) are left out of every figure.

| Column | Parquet type | Null | Content |
|---|---|---|---|
| `period` | UTF8 | no | `YYYY-MM` |
| `category` | UTF8 | no | `TOTAL`, `PRIVAT_INSTITUSJON`, `FOSTERHJEM` |
| `measure` | UTF8 | no | `EXPECTED`, `RECEIVED`, `REJECTED_ONLY`, `MISSING`, `RECEIVED_NOT_EXPECTED`, `REJECTED_UNATTRIBUTED` |
| `reason_code` | UTF8 | yes | Only for `REJECTED_UNATTRIBUTED` |
| `count` | INT32 | no | Count |

Rules: `RECEIVED + REJECTED_ONLY + MISSING = EXPECTED` for each `category`, and the two categories add up
to `TOTAL`. `RECEIVED_NOT_EXPECTED` and `REJECTED_UNATTRIBUTED` appear only with
`category = TOTAL`. Never a single percentage.

## Manifest — `_manifest.json` (written last)

```json
{
  "period": "2026-10",
  "contractVersion": "1.0.0-draft",
  "files": [
    { "name": "data.parquet", "sha256": "<hex>", "rowCount": 13, "bytes": 1000 }
  ]
}
```

## Determinism

The file bytes are a pure function of the rows: one row group,
fixed compression (Snappy), no wall-clock metadata.
