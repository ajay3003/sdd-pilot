# Feature Visibility — gap register (task 13)

| Id | Gap | State |
|---|---|---|
| FV-D1 | Settings and sidebar defaults disagreed | CLOSED: `FeatureDefault` shared |
| FV-D2 | Inert toggles | CLOSED: declared "No effect", no toggle, counted separately (keys kept for configuration compatibility) |
| FV-D3 | Traceability & Coverage counted visible while hidden | CLOSED: counts are of sidebar entries; reason "Off: Legacy Traceability Navigation" |
| FV-D4 | Names-only "In sync" | CLOSED: identity-based Navigation check |
| FV-D5 | Dead route/link lists | CLOSED: removed |
| FV-D6 | Broken `/explorers` link | CLOSED |
| FV-D7 | Dashboard linked a hidden feature | CLOSED |
| FV-D8 | Orphan configuration keys | CLOSED: removed |
| FV-D9 | Frontend fallback showed optional/legacy entries | CLOSED |
| FV-D10 | Unknown keys accepted | CLOSED: 400, nothing written |
| FV-D12 | Stale "refresh the page" text | CLOSED |
| FV-G1 | No per-module authorization: hidden pages open by address and their APIs have no flag check | OPEN: by design (stated in NavigationCatalog and now in the UI and Help); needs a product decision before any change |
| FV-G2 | No role model for saving: any local session can change visibility | OPEN: pre-existing; local single-user installation |
| FV-G3 | Audit trail is the log only (changed keys, before → after); no persisted record of who | PARTIAL (FV-D11) |
| FV-G4 | Project prerequisites (project, imported source, Source Analysis, Target Environment) are not shown per feature in the panel; the sidebar's applicability badges show them | OPEN |
| FV-G5 | Runtime dependencies (for example the k6 runner) are not shown per feature in the panel; Runtime & Environment shows them | OPEN |
| FV-G6 | `FeatureVisibilityPageService` is a registered stub that nothing calls (always "Pass") | OPEN: dead code, left in place (has its own test) |
| FV-G7 | `ExtractionReviewList` component is rendered by no page | OPEN: dead code, its two switches are declared No effect |
| FV-G8 | Retired keys SpecComparison/SpecificationDeltas/TaskDeltas are still in the backend flag model (default true, gate nothing) | OPEN: harmless |
| FV-G9 | `/traceability` and `/artifact-traceability` both titled "Requirements Traceability" | OPEN |
| FV-G10 | Fixtures E (authorization-restricted) and Q (dev-only vs tester) | NOT APPLICABLE: no per-module authorization and no profile-specific visibility exist |
| FV-G2 | Configuration changes restricted | CLOSED (13b): backend policy SystemConfigurationWrite (authenticated permission or loopback-checked local grant); 403 verified |
| FV-G3 | Audit trail | CLOSED (13b): persisted JSONL records with actor (hashed principal / local-session:loopback / not attributed) and before → after |
| FV-G11 | Five no-effect switches | CLOSED (13b): retired from the configurable catalog; values ignored, never enabled; save rejected |
| FV-G12 | Backup & Transfer refused in the Tester Package (Development-only local grant) | CLOSED (13b): TesterNative local grant under LocalTrustedExecution checks |
| FV-G13 | Tester Package isolated restore BLOCKED (NOCREATEDB) | CLOSED (13b) with a separate CREATEDB-only provisioning role; verified on a disposable cluster; real packaged installation NOT RUN |
| FV-G1 | Hidden pages reachable by address | ACCEPTED by decision: visibility is not authorization; some features intentionally direct-access |
