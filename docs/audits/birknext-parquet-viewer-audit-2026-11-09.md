# BirkNext — Parquet Data Explorer — audit (2026-11-09)

Branch `008-traceability-first` (local only). Baseline `514478d2`.

## Existing support

| Capability | Before | Notes |
|---|---|---|
| Parquet reader / writer (Parquet.Net, ParquetSharp, Apache Arrow, DuckDB) | **Missing** | no package referenced; none in the local NuGet cache |
| Format detection / preview of Parquet | **Missing** | |
| Blob Storage explorer | **Missing** | `Azure.Storage.Blobs` is referenced only for Event Hub checkpoint listing |
| Schema Studio (XSD / JSON) | Existing | not Parquet-aware |
| Test Data, Execution Evidence, IQR, Difference Analysis | Existing | no Parquet artifact type |

## Library decision

- Third-party .NET libraries would have to be downloaded (not in the local package cache); this task runs without external network access, so
  no package was added.
- Decision: a **bounded, dependency-free reader** in BirkNext (`Services/Parquet`): Thrift compact footer, schema tree, column chunks with PLAIN and
  dictionary encodings, RLE / bit-packed levels, data pages v1 and v2, codecs UNCOMPRESSED / SNAPPY (own decoder) / GZIP (.NET), physical types
  incl. INT96, logical types STRING/ENUM/JSON, DATE, TIMESTAMP (millis/micros/nanos), DECIMAL (int32/int64/fixed), flat and non-repeated nested
  columns. **Not supported** (reported per column, never guessed): repeated values (lists, maps), DELTA_* and BYTE_STREAM_SPLIT encodings,
  ZSTD / LZ4 / LZ4_RAW / BROTLI / LZO codecs, encrypted footers, column chunks in external files.
- No Python runtime dependency: Python (standard library only) is used for the independent fixture writer / oracle, never by BirkNext.
- Limitation to keep visible: the reader has been verified against an independent writer written from the specification, **not** against files
  produced by pyarrow / Spark / Parquet.Net (that needs a dependency download; NOT RUN).
