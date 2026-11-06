# NEXT54 — Test Data and automatic XML fixture workflow redesign (2026-11-06)

Branch `008-traceability-first`. Builds on NEXT50 (799019a9): the Test Data client is registered, so fixture save and the automatic draft after import work in the real host. Frontend only; no API, schema or migration change. Nothing pushed.

**Status: partially completed.**

- Done and verified in a real browser:
  - The page is simplified into four sections.
  - The Overview names one next step.
  - The fixture builder shows XSD status, mapping status, a formatted XML preview, separate validation outcomes, the save state, and the **persisted fixture identity** confirmed from the reloaded catalog.
- **Not verified live**: importing Tiltak 2 and the automatic fixture end to end. The package is not in the local `testdata/` folder, and the M2LB copy has no source snapshot that declares the Skolenærvær XSD. Automated tests cover save → reload → still present. The manual checklist is at the end.

## Audit — UI gaps left after NEXT50

| # | Gap (brief §) | Before |
|---|---|---|
| U1 | One long page (§6) | Ten stacked sections, in this order: readiness, identities, XML fixtures, requested data, assets, candidates, sources, reset, docs. A tester scrolled past plumbing to reach the catalog. |
| U2 | No next action (§6) | The readiness listed facts but did not say what to do. "Generate missing XML fixture" lived three sections down. |
| U3 | The builder did not show where it stood (§6) | Identities, snapshot, XSD, mappings, generate, review and save were one long form, with no step state and no summary of XSD or mapping status. |
| U4 | Validation outcomes mixed (§3, §6) | One "Validation: Valid" line plus a disclaimer. Well-formed, XSD, business and runtime outcomes were not separate facts. |
| U5 | XML review (§6) | A plain textarea with no line numbers. Diagnostics were not linked to lines, and there was no copy, download or format. Unsaved changes were not shown. |
| U6 | Save result (§4) | A success message only. The persisted identity (id, version, approval) was never confirmed from the catalog, so "saved" could not be told apart from "stored". |
| U7 | Load failure | A plain red paragraph, with no Retry. |
| U8 | Mobile | The builder overflowed a 390 px screen by 32 px. Fieldsets use `min-content` width, and contract names are long unbroken identifiers. |

## Changes

### Test Data page (`Pages/TestDataPage.razor`)

- **Four sections**, kept in the URL as `?section=`:
  - **Overview**: the readiness from the shared rule, catalog counts, **Next step** and session identities.
  - **Test Data assets**: the catalog table with filters and details, unchanged.
  - **XML fixtures**: the builder.
  - **Requests & configuration** (advanced, right-aligned): requested Test Data, candidates, sources & configuration, reset and documentation. These stay collapsed as before.
- A deep link opens its own section: a consumer request opens Requests; `?asset=` opens Assets.
- **Next step**: one primary action, derived from the shared readiness (`TestDataTesterReadiness`, which reports its status to the page) and the counts.
  - In order: choose a missing approved identity → **Generate missing XML fixture** (or review it) → **Open XML fixture** when it is ready → review assets awaiting approval → resolve open requests.
  - Each step comes with its reason ("XML fixture: Missing.").
  - "Generate missing XML fixture" opens the XML fixtures section with the builder already open (`create=xml`).
- A load failure shows `LoadError` with **Retry**, never an empty catalog.

### XML fixture builder (`Components/XmlFixturePanel.razor`)

The generation, validation and persistence logic is unchanged. The additions are presentation and safety:

- **Steps**: Configure (identities, snapshot, XSD, mappings) → Review & validate → Save, each with a state (current / done / todo, `aria-current="step"`).
- **Status strip**:
  - XSD: file and resolution state, or "Not chosen yet".
  - Mappings: for example "1 suggested · 2 mapped · 1 needs a value".
  - Validation: whether the XML has been generated and validated.
  - Saved: "Unsaved — the XML exists only in this editor" until it is saved.
- **Four validation outcomes**, never one "valid":
  - Well-formed XML (parsed here; reports the line on failure).
  - XSD validation (passed / failed with the issue count / not run), against the exact XSD file and snapshot.
  - Business rules: **Not performed**.
  - Runtime: **Not verified**.
- **Review tools**:
  - A formatted preview with line numbers; lines named by diagnostics are highlighted. The preview shows the first 400 lines; the editor and download hold everything.
  - **Format XML**. It is disabled for malformed XML; formatting means the XML must be validated again.
  - **Copy** (clipboard) and **Download** (`<alias>.xml`).
- **Persisted identity**: after saving, the panel shows what the **reloaded catalog** lists: alias, Test Data id, version (a draft and the approved version in use are shown apart), approval and schema validation.
  - Until the catalog lists the fixture, it says "Saved, but not listed by Test Data yet", never "stored".
- `AutoOpen` opens the builder on arrival, the same way its primary action does.
- Mobile: fieldsets may shrink, and long identifiers wrap.

## Readiness rules (unchanged, now visible per step)

- Preview is not saved; saved is not validated; XSD-valid is not end-to-end ready (business and runtime outcomes are stated separately).
- The Reporter is a submission identity, not part of the payload. A missing optional need never blocks; a missing required one never reads Ready.
- An approved fixture is never mutated. A regeneration is a new draft version; approval stays explicit.

## Tests

- New tests (6):
  - Next step follows the shared readiness ×2: Generate missing XML fixture → `section=fixtures&create=xml`; Open XML fixture when ready.
  - A missing approved identity comes before the fixture.
  - A load failure shows the error with Retry, never an empty catalog.
  - Builder steps, status, the four validation outcomes, preview lines, Format disabled for malformed XML, Download.
  - **Save → the identity is confirmed only from the reloaded catalog → a new panel (a browser reload) still finds the fixture.**
  - Arriving from the next step opens the builder.
- Updated: page tests now open the section they assert (Assets, Requests or Overview). The tester-workflow test now asserts the four-section IA, replacing the old one-page order. No assertion about values, sensitivity, approval or lifecycle was relaxed.
- Test Data, XML fixture, Security Classification, Skolenærvær and scenario-builder subset: **254 passed**, then 46/46 for the new tests. Full frontend suite: **8250 passed, 1 failed**. The failure was a timing test unrelated to this work (`ExtractionPipelineCompilerIntegrationTests`, 243 ms against a 200 ms limit under parallel load); run alone it passed 4/4.

## Browser verification (M2LB copy, Edge, localhost only) — 13/13

Script `next54-e2e.cjs`; screenshots `next54-overview.png`, `next54-builder.png` and `next54-mobile.png`.

1. Four sections, with Overview current: Overview · Test Data assets (2) · XML fixtures · Requests & configuration (1 open).
2. The Overview holds readiness and identities only.
3. One next step: **"Generate missing XML fixture — XML fixture: Missing."**
4. It opens `test-data?section=fixtures&create=xml` with the builder open.
5. Steps: configure current, review todo, save todo.
6. The missing prerequisite is stated precisely: XSD "Not chosen yet" · "No matching XSD found for bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn in the imported sources. Open Schema Studio". Generate is disabled; nothing is guessed.
7. Assets: the catalog table with filters (2 assets).
8. Requests & configuration: plumbing collapsed.
9. No overflow at 1440 px.
10. No overflow at 390 px (after the fieldset fix).

Also: read-only, no external host, no page errors.

## Not verified live — manual checklist (needs the Tiltak 2 package)

1. Import `Tiltak 2 - Sikkert digitalt økosystem.zip` into the Skolenærvær application (Project Import).
2. After Source Analysis, Project Import lists the automatic XML fixture outcome: created, or the precise missing prerequisite.
3. Test Data → Overview → Next step reads "Review the XML fixture" or "Open XML fixture".
4. XML fixtures shows:
   - XSD: `bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn.xsd · <state>`;
   - Mappings including the Child (`Klient → Identifikator → Fodselsnummer`);
   - Well-formed **Yes**, XSD **Passed**, Business **Not performed**, Runtime **Not verified**.
5. Save, then check that "Stored in Test Data" shows alias `skolenaervaer-valid-report`, the Test Data id, `v1` and "Not approved".
6. Reload the browser. The fixture is still listed under XML fixtures and Assets with the same id and version.
7. Approve it. The Overview next step becomes "Open XML fixture".

## Not done (remaining acceptance criteria)

- §5 the full eight-dimension readiness model. Consumer binding, environment compatibility and runtime readiness are not separate dimensions. The shared readiness (identities, XML fixture, fixture validation) and the builder's XSD, mapping and validation status are shown.
- §6 the mapping table as `XML field | Required | Value source | Status` with required/unmapped filters. The XSD parameter model carries no "required" flag, so required-ness is not shown. Mapping rows keep their existing layout.
- §6 editor syntax highlighting and explicit undo/redo (the textarea's native undo applies), and an unsaved-changes guard on navigation away. The unsaved state is shown but not enforced.
- §9 scenario variants (negative assets).
- §11 multi-source contract-selection UX: unchanged. Several XSDs still need an explicit choice; nothing is chosen by date.
- AC-01 to AC-07 live: import-driven automatic discovery, mapping, generation, validation and persistence for Tiltak 2 were not verified in a browser (package unavailable).
