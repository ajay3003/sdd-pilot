# BirkNext Windows Tester Package — software bill of materials

- **Pinned inputs:** `AIAssisted/versioning/native-runtime.lock.json`.
- **Per-package record:** `config/runtime-manifest.json` (component versions; the SHA-256 of the extracted `k6.exe`) and `package-manifest.json` (the SHA-256 of every packaged file).
- **Build rules:**
  - inputs are downloaded only with `-AllowDownload`, only from the pinned URL, and are refused unless their SHA-256 matches the lock;
  - no `latest`, no mirror, no substitute.

| Component | Version | Source | Integrity | License | Packaged as |
|---|---|---|---|---|---|
| BirkNext backend (`BirkNext.Api.exe`) | from `AIAssisted/versioning/BirkNext.Version.props` + build identity | this repository | `build-manifest.json` + `package-manifest.json` | proprietary (BirkNext) | `AIAssisted/backend/` (self-contained win-x64) |
| BirkNext frontend | same build | this repository | same | proprietary | `AIAssisted/frontend/wwwroot/` |
| BirkNext launcher (`Start-BirkNext.exe`) | same build | this repository | same | proprietary | package root (single file, self-contained) |
| .NET runtime | the .NET 8 runtime of the building SDK (recorded as `dotNetRuntime`) | Microsoft (NuGet runtime packs) | NuGet package signatures at restore | MIT | inside the backend and launcher |
| PostgreSQL | 16.13 (EDB build 1) | `https://get.enterprisedb.com/postgresql/postgresql-16.13-1-windows-x64-binaries.zip` | SHA-256 `9b4c7a1b…f20f00a9` (trust on first use: EDB publishes no checksum; binaries are not Authenticode-signed) | PostgreSQL License (`licenses/PostgreSQL-LICENSE.html`) | `runtime/postgresql/{bin,lib,share}` |
| k6 | 1.0.0 | `https://github.com/grafana/k6/releases/download/v1.0.0/k6-v1.0.0-windows-amd64.zip` | SHA-256 `0bfbab5c…a890c70d` = upstream `k6-v1.0.0-checksums.txt`; the extracted `k6.exe` hash is pinned in the runtime manifest and verified by BirkNext before every use; not Authenticode-signed | AGPL-3.0-only (`licenses/k6-LICENSE.md`; source: `licenses/k6-SOURCE.txt` → github.com/grafana/k6/tree/v1.0.0) | `runtime/k6/k6.exe` (unmodified; no extensions) |

## Libraries bundled inside the PostgreSQL binaries (`runtime/postgresql/bin`)

The DLLs are as shipped by EDB. Their license texts are **not** in the upstream archive; see gap SC-G4.

| Library | DLL | License |
|---|---|---|
| OpenSSL 3 | `libssl-3-x64.dll`, `libcrypto-3-x64.dll` | Apache-2.0 |
| ICU 67 | `icu*67.dll` | Unicode-DFS-2016 / ICU |
| libxml2, libxslt | `libxml2.dll`, `libxslt.dll` | MIT |
| zlib | `zlib1.dll` | Zlib |
| LZ4 | `liblz4.dll` | BSD-2-Clause |
| Zstandard | `libzstd.dll` | BSD-3-Clause |
| gettext libintl | `libintl-9.dll` | LGPL-2.1-or-later |
| libiconv | `libiconv-2.dll` | LGPL-2.1-or-later |
| winpthreads | `libwinpthread-1.dll` | MIT / ZPL-2.1 |
| libcurl | `libcurl.dll` | curl |

## Removed from the PostgreSQL archive

- pgAdmin 4 and StackBuilder (with its wxWidgets DLLs);
- documentation (except the license notice) and headers;
- regression and test programs (`pg_regress*`, `isolationtester`, `libpq_*` tests, `test_cloexec`, `testplug.dll`);
- import libraries (`lib/*.lib`) and `lib/pgxs`.

## Windows prerequisites not bundled

- **Microsoft Visual C++ 2015–2022 Redistributable (x64):** `vcruntime140.dll` and `msvcp140.dll` are imported by the PostgreSQL binaries (verified from their PE import tables). The launcher checks for them and names the missing component.
- **Universal C Runtime:** part of Windows 10/11.
