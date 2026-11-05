# Test Data & XML fixture lifecycle — persistence root cause (NEXT50, 2026-11-06)

Branch `008-traceability-first`. **Status: partially completed.** The critical persistence defect and the automatic-draft defect it caused are fixed and covered by tests. The page redesign (brief §6) and the scenario-variant foundation (§9) are not done in this pass.

## Audit — the save failure ("Fixtures cannot be saved in this host.")

The chain: Import → Source Analysis (XSD evidence) → integration template context → `XmlFixturePanel` / `XmlFixtureAutoDraft` → Schema Studio generation and validation (one XSD engine) → `SchemaGeneratedXmlStore` + the Test Data API (register, approve) → readiness.

| Question | Finding (repo evidence) |
|---|---|
| Why "cannot be saved in this host"? | `ITestDataApiService` was **never registered** in `Program.cs`. `XmlFixturePanel.SaveAsync` asked the container for it on the canonical-alias path (`GetService(typeof(ITestDataApiService))` → null → that message). |
| Intentional / environment / defect? | A **defect**. Every other Test Data surface uses `TestDataApiService.Resolve(...)`, which falls back to a client built from the backend URL. Three callers used `GetService` directly: the fixture save, `XmlFixtureAutoDraft.RunAsync` and Security Classification. |
| Does automatic generation after import exist? | Yes: `XmlFixtureAutoDraft` is called by Project Import after Source Analysis. In the running app it **returned no drafts**, because of the same missing registration (`RunAsync` exits early with `[]` when the service is null). Generation after import never ran outside tests. |
| Why did tests not catch it? | Every bUnit test registers a mock `ITestDataApiService`. The host composition was never asserted. |
| Is reusable persistence available elsewhere? | Yes. The backend Test Data API (register, version, approve) and `SchemaGeneratedXmlStore` are the existing stores. Nothing new was added; there is no browser-only persistence. |
| Is readiness from persisted or UI state? | Persisted: the catalog is read from the API. Nothing changed. |

## Fix

- `Program.cs` registers the typed client `AddHttpClient<ITestDataApiService, TestDataApiService>` on the backend base address, with the same optional bearer-token handler as the other protected clients.
- `XmlFixturePanel` uses the shared `TestDataApiService.Resolve` on the canonical save path. The remaining guard (workspace store missing) now says precisely what is missing and that the XML stays in the editor.
- `XmlFixtureAutoDraft` resolves the client through the registered service, else the shared resolver (`ResolveTestData`). Automatic drafting after import runs in the real host.
- Security Classification uses the same resolution for approved Test Data context.

## Tests

- New `TestDataHostCompositionTests` (2): the host registers the Test Data client; no surface refuses to save because of the host, and the auto-draft uses the shared resolution.
- Existing `XmlFixtureAutoDraftTests`, XML fixture panel, Test Data and Security Classification tests: 166/166.
- Frontend full suite **8231 passed, 0 failed**.

## Browser check (M2LB copy)

- The Test Data page loads: catalog, readiness, session identities (Child and Reporter approved defaults), requests.
- The XML fixtures section reads "No test pack declares an XML message fixture for this environment". The M2LB copy's environment has no Skolenærvær test pack bound, so an end-to-end save could not be exercised here. **Not verified live.**

## Manual verification checklist (to run where the Skolenærvær pack is bound)

1. Import `Tiltak 2 - Sikkert digitalt økosystem.zip` into the Skolenærvær application (Project Import).
2. After Source Analysis, Project Import lists the automatic XML fixture outcome: created, or the precise missing prerequisite (no XSD, several XSD candidates, no approved Child, not bound to a project).
3. Test Data → XML fixtures lists the generated draft, validated against the exact XSD version.
4. Save the fixture under the canonical alias. It saves; "Fixtures cannot be saved in this host." no longer appears.
5. Reload the page. The fixture is still in the catalog with its version, and readiness reflects it.
6. Re-import the same snapshot: "already has a version generated from this snapshot" (idempotent; approved versions are never overwritten).

## Not done (remaining brief scope)

- §6 UI redesign into Overview / Assets / XML Fixtures / Requests & Configuration, and the Configure → Review & Validate → Save builder with an editor.
- §5 per-dimension readiness model (identities / contract / mapping / fixture / XSD / consumer / environment / runtime) beyond the existing readiness summary.
- §9 scenario variants (negative assets).
- §11 multi-source contract-selection UX (existing behaviour: several XSD candidates → no draft, with the reason).
