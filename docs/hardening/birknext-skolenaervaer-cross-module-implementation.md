# BirkNext — Skolenærvær cross-module implementation (2026-11-11)

- **Baseline:** `02bac857`.
- **Scope:** BirkNext only. No M2LB or MU source was changed.

| Gap | Change | Files |
|---|---|---|
| BN-1 Period-closed channel | `PeriodClosedChannel = "skole.perioder"`; integration template and setup-profile template follow it | `SkoleNaervaerJourneyPack.cs`, `SetupProfileService.cs` |
| BN-2 Event identity | `PeriodClosedEventNames` = `SkolenaervaersperiodeLukket`, `SkolenaervaerPeriodClosed`; used for the requirement identity's contract identities and for the "event contract in source" check; the prerequisite text names both | `SkoleNaervaerJourneyPack.cs` |
| BN-3 Stale curated facts | `SkoleDeliveryState` and the scenario texts are generation-dependent. The contract boundaries `mm-input` and `skole-intake` have `SupportedVersions = null` (not evaluated) with the generation evidence; `skole-to-bufdata` states the Parquet writer of the English generation. The Message Verification Bufdata provider text no longer claims "rows only" | `SkoleNaervaerJourneyPack.cs`, `SkoleNaervaerContractProfile.cs`, `SkoleNaervaerMessageVerification.cs` |
| BN-4 Seed source basis | The seed records its basis as "curated outside this installation (no imported snapshot)", so every recognised snapshot is a "Not among the sources this revision was written from — may need review" change | `SystemGuideService.cs` |
| BN-5 Guide content | The seed was rewritten from the oracle: generations kept apart, conflicts marked Conflicting, nothing runtime-verified, ids unchanged, two gaps added (`g-mu-mm-versjon`, `g-generasjon`). An installed seed that nobody has edited (only revision, Draft, author = seed author) is replaced once by the shipped seed. Anything edited, submitted or published is never touched | `Guides/skolenaervaer.guide.json`, `SystemGuideService.EnsureSeededAsync` |
| BN-6 Person identity | Alias `M2LB.PersonService.Api`; description covers events and the HTTP lookup. The Bufdata description is per generation | `SkoleNaervaerTopologyProfile.cs` |

## Tests

**Corrected to the source:**
- `SkoleNaervaerJourneyPackTests`
- `IntegrationIdentityTests`
- `SetupProfileServiceTests`
- `MessageVerificationTests`
- `ScenarioCapabilityTests`
- `SystemGuideTests` (the seed assertions now require both generations and no runtime verification)

**New tests:**
- `A_curated_seed_never_claims_the_snapshots_present_when_it_was_seeded_as_its_basis`. It runs on real PostgreSQL with the real pack wiring, and the source is imported before the guide is first opened.
- `An_unedited_seed_draft_is_refreshed_from_the_shipped_seed_and_an_edited_or_published_one_never_is`.
- In `IntegrationIdentityTests`, the report topics are shown not to be the Period-closed integration, and both event names are shown to identify it.

## Compatibility note

An existing integration configured with topic `skole.reports` for Period closed is **no longer matched** as the Period-closed integration. Per the source, that topic does not carry period closure; the readiness check now says `Channel skole.reports is not skole.perioder`. No stored configuration is changed automatically.
