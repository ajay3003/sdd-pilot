# BirkNext — embedded offline translation (2026-11-20)

English → Norwegian (bokmål) translation of Manual Testing text that works in the internal AVD environment without internet, Docker/Podman,
containers, Azure, API keys, administrator rights, a separately installed Python or translation server, and without downloading anything on the
tester's PC. Everything ships inside the Windows Tester Package.

## 1. Audit of what existed

| Part | Before | Reused / changed |
|---|---|---|
| Provider interface | `ITestCaseTranslator` (texts with ⟦n⟧ identifier placeholders in, same count out) | Reused unchanged; new `EmbeddedOfflineTestCaseTranslator` implements it |
| Settings | `TranslationSettings` + `TranslationSettingsStore` (App_Data JSON, Azure key DPAPI) + System Settings card | Reused; new provider kind `EmbeddedOffline`, glossary list, default changed |
| Resolver | `TestCaseTranslatorResolver` reads settings per request | Reused; gets the offline engine |
| Fallback | online service → local LibreTranslate | Online → embedded offline (else LibreTranslate); offline never falls back to the internet |
| Manual Testing | Translate icon → `TestPlanSuggestionService.TranslateAsync` → resolver; originals kept, translation editable and reviewed | Unchanged: it uses whatever the resolver returns |
| Packaging | `build-tester-package.ps1` with pinned inputs in `native-runtime.lock.json`, `package-manifest.json` hashes, launcher integrity check, `Update-BirkNext.exe` | Extended (section 5) |

No second settings system, provider interface or packaging mechanism was introduced.

## 2. Engine and model evaluation

| Candidate | Finding | Decision |
|---|---|---|
| `HPLT/translate-en-nb-v2.0-hplt_opus` (named in the brief) | The Hugging Face repository holds only README.md and .gitattributes (no weights); data.statmt.org/hplt-models/translate/v2.0/ answers 404 | Not usable |
| `HPLT/translate-en-nb-v2.0-hplt` (same release, HPLT data only) | Weights published (Marian `.npz`, 308 MB fp32, Transformer-base), SentencePiece unigram vocabulary, CC BY 4.0, revision `24a48dba7fa6`; LFS hashes match | **Selected** |
| Helsinki-NLP `opus-mt-tc-big-en-gmq` | CC BY 4.0, multi-target (needs a `>>nob<<` token), Transformer-big (~3× larger); conversion needs PyTorch | Not evaluated further: size and build-time dependencies; can be revisited |
| NLLB-200 | CC BY-NC 4.0 (non-commercial) | Excluded on licence |
| CTranslate2 | Converts Marian `.npz` directly (no PyTorch); Windows x64 CPU wheel; int8 | **Selected** (model 82 MB int8) |
| ONNX Runtime | Needs Marian → Hugging Face → ONNX conversion and a hand-written decoder in C# | Not chosen (more risk for the same result) |
| Marian NMT native | No maintained Windows CPU binary; build from source | Not chosen |

Measured on this PC (int8, 2 threads): model load 0.2 s, 13 sentences 0.48 s, helper start to Ready ≈ 1–2 s.

Translation behaviour found while evaluating, and how it is handled:

* The model splits the existing ⟦n⟧ identifier placeholders (1/5 kept) but keeps `<xN>` markers (5/5). Identifiers and glossary terms therefore
  travel as `<xN>`; each must come back exactly once, otherwise the text is translated again piece by piece between the markers. An identifier is
  never lost or altered.
* Norwegian words written inside English text are garbled ("korrigering" → "Bourbon", "Skolenærvær" → "barnehagevær"); domain terms are kept by the
  glossary instead, and text that is already Norwegian is returned unchanged.
* Without a glossary the model translates "school attendance" as "skole", "withdrawal" as "uttak", "correction" as "korreksjon". The starting
  glossary (`TranslationGlossary.DefaultEnNb`, editable under Advanced) fixes the nine M2LB terms; it holds terms only, no business rules.

## 3. Runtime architecture

* `runtime/translation/` in the package (159 MB): embeddable Python 3.12.10 **with `_socket`, `_ssl`, OpenSSL, `_hashlib`, sqlite, asyncio removed** (the
  helper cannot open a network connection), CTranslate2 (CPU; CUDA DNN library and converters removed), SentencePiece, the model, the helper
  `birknext_translate.py`, licences, `translation-manifest.json` (SHA-256 of every file).
* `OfflineTranslationEngine` (backend singleton) verifies the bundle, starts one helper process on first use (stdin/stdout JSON lines, no port),
  serialises requests, applies timeouts (60 s + 1 s per 40 characters, max 10 min), stops the helper after 10 minutes idle, on timeout and when
  BirkNext stops. On Windows the helper runs in a job object: kill-on-close (it never outlives the backend) and a 2 GB process memory cap. Threads:
  half the logical processors, 1–4. The helper's environment has no Python, proxy or BirkNext secret variables.
* A missing, changed or incompatible bundle (any file's size or SHA-256, manifest protocol) makes translation unavailable with the reason; nothing
  is downloaded to repair it.

## 4. Settings and UI

Default for new installations: **Offline translator (packaged with BirkNext)**, English → Norwegian (bokmål), internet fallback disabled. Saved
settings keep their own provider. System Settings → Translation now shows: default translator, From, To, offline model status, Test translation,
Advanced (Azure/Anthropic/LibreTranslate, more target languages, glossary). "Offline translation — Ready" appears only after the packaged model
loaded and translated the probe sentence on this PC; Podman controls appear only when LibreTranslate is chosen under Advanced.

## 5. Packaging, launcher, updater

* `scripts/build-offline-translation.ps1` builds the bundle from pinned inputs (python.org embeddable zip, PyPI wheels, Hugging Face files at a
  fixed revision, licence texts; each SHA-256 in the lock file) and verifies it by translating a sample. `build-tester-package.ps1` runs it and
  places the result in `runtime/translation`; a failing bundle stops the package build.
* `config/runtime-manifest.json` declares `offlineTranslation` (directory, protocol, model, revision, engine, manifest SHA-256).
* The launcher passes the folder to the backend (`ManualTesting__Translation__Offline__Directory`), checks the translation manifest, Python and model
  among its critical files on every start (every file on the first start, as for all files), and `--status` names the translator.
* `Update-BirkNext.exe` rejects a package whose offline translation entry is malformed or whose translator files are missing, shows the translator in
  the plan, and still installs older packages without it.

## 6. Results

### Terminology (real model, `RealModelTests.M2LB_terminology_uses_the_reviewed_Norwegian_terms`)

Reference wording is a proposal for a Norwegian reviewer to confirm; the test checks the term, not the whole sentence.

| English | Offline translator | Reference | Without glossary |
|---|---|---|---|
| The child lives in a foster home. | Barnet bor i et fosterhjem. | Barnet bor i et fosterhjem. | same |
| The school reports school attendance every month. | Skolen rapporterer skolenærvær hver måned. | Skolen rapporterer skolenærvær hver måned. | "rapporterer skolen" |
| Register the child's educational relationship with the school. | Registrer barnets utdanningsforhold med skolen. | … utdanningsforhold til skolen. | sometimes "pedagogisk forhold" |
| Check that the period lock prevents new reports. | Kontroller at periodelåsen forhindrer nye rapporter. | … periodelåsen hindrer … | same |
| The municipality has a reporting obligation for the child. | Kommunen har rapporteringsplikt for barnet. | same | "meldeplikt" |
| The child is not part of expected reporting. | Barnet er ikke en del av forventet rapportering. | … ikke med i forventet rapportering. | same |
| Children with code 6/7 must not be shown. | Barn med kode 6/7 må ikke vises. | … skal ikke vises. | same |
| A correction replaces the previous report. | En korrigering erstatter forrige rapport. | same | "korreksjon" |
| A withdrawal removes the report from the statistics. | En tilbaketrekking fjerner rapporten fra statistikken. | same | "uttak" |

The starting glossary was reduced to the terms the model gets wrong or inconsistent: forcing terms it already translates made the grammar worse
("en fosterhjem", "periodelås hindrer"). A title without closing punctuation is translated as a sentence ("Duplicate message" was "Uformell
melding", now "Duplisert melding").

### Memory and CPU

| Measure | Value |
|---|---|
| Helper private memory with Intel MKL (default) | 888 MB after load, ~950 MB after translating |
| Helper private memory with `CT2_USE_MKL=0` (shipped) | 119 MB after load, 129 MB in the package acceptance |
| Translation time, 3 sentences | 81 ms (MKL) → 110 ms (shipped) |
| Helper CPU time for the whole package acceptance | 7.7 s |
| Backend working set during acceptance | 280 MB |

### AVD-style acceptance of the real Release ZIP

`BirkNext-Tester-Windows-x64-1.0.0-local.zip` built by `build-tester-package.ps1` from `692c8e51` (365 MB, 2817 files), extracted to a new
folder, started with `Start-BirkNext.exe --data-dir <own folder>` (own database and ports 5480/5481; ports 5000/5173 untouched), with Python
removed from PATH (no `python` resolvable at all), no Docker/Podman involved, no Azure key, standard user. Driven through the same API the browser
uses (`scratchpad/avd/accept.py`).

| # | Check | Result |
|---|---|---|
| 1 | BirkNext starts (every file verified on first start) | Pass |
| 2 | Offline engine loads; "Offline translation — Ready" only after the probe translated (0.8 s incl. start) | Pass |
| 3 | English acceptance scenarios → 3 test cases translated by the offline translator (0.9 s) | Pass |
| 4 | Multiple test cases, user story title and description translated | Pass |
| 5 | FR ids, `MessageId`, `ReportIntakeIdempotencyTests` intact | Pass |
| 6 | Originals unchanged | Pass |
| 7 | A tester's Norwegian edit survives reload | Pass |
| 8 | Azure DevOps export preview uses the saved Norwegian text (language `nb` for every case) | Pass |
| 9 | After stopping and starting BirkNext: edit kept, offline translation works (Ready in 0.4 s) | Pass |
| 10 | No network: the helper had 0 TCP and 0 UDP endpoints; its runtime has no socket module | Pass |
| 11 | Memory/CPU acceptable (above) | Pass |
| 12 | Changed `model.bin`: "Damaged" with the file named, translation refused with the reason, nothing fetched; restored file → Ready again | Pass |
| — | Stopping BirkNext leaves no helper process (0 processes from the package) | Pass |

31 of 31 checks passed (18 first run, 7 damaged model, 6 after restart). Automated tests: backend translation, launcher and updater 140/140 with
the real model; frontend 9318/9318.

Not covered here: a machine with the network adapter disabled (the helper cannot open a socket, and BirkNext makes no call for translation, but
the rest of BirkNext was not run offline as a whole in this test), and AVD application-control policies.

## 7. Limitations and open items

* The brief's preferred HPLT+OPUS model has no published weights; the HPLT-data model is used. If HPLT publishes it, only the lock file changes.
* Quality is that of a Transformer-base model: fluent for short test text, literal for long or idiomatic sentences. Every translation is a draft a
  tester reviews; nothing is approved because translation succeeded.
* `ctranslate2.dll` includes Intel oneMKL/OpenMP code from the upstream wheel (Intel Simplified Software License); legal review of the notice text is
  recommended before wide distribution.
* AVD application control (AppLocker/WDAC) may block `python.exe` from a user folder exactly as it may block `Start-BirkNext.exe`; the error message
  says so. This was not testable here.
