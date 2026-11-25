# Manual Testing — automatic translation in Planlegg og klargjør (2026-11-25)

Committed locally together with the «Kilde mangler» fix. Nothing pushed, no reset, no data changed.

## Audit

**Engine (requirement 19):**
- Your installation's System Settings → Translation uses `EmbeddedOffline`: CTranslate2 4.9 on CPU (int8) with the HPLT en→nb v2.0 model packaged under `runtime/translation` and `%LOCALAPPDATA%\BirkNext\offline-translation`. **Fully offline**: no internet, cloud or AI.
- The default for a new installation is LibreTranslate (a local container) with the embedded offline translator as fallback.
- Azure Translator and Anthropic are online, and used only when explicitly chosen.

**Saved translations:**
- They are stored per case (`TestCaseTranslation`), per user story and per requirement wording.
- Each records its origin (`Machine`, `MachineEdited`, `Manual`) and a fingerprint of the original it was made from.
- **Revisions (requirement from the Planlegg brief):** translate-all re-translated even current machine translations, and every call saved a new revision. The repeated «Translated to Norwegian» revisions in your plan came from those explicit translations.

**Concurrency:**
- Plan writes carry an optimistic concurrency token (`Revision`).
- A translation finishing after a save is refused with 409 and never overwrites it.

## Implementation

**Server** — `TranslatePlanRequest.OnlyMissing` (additive; default behaviour unchanged):
- translates only cases, user stories and requirement wording with **no** Norwegian text;
- an existing translation (machine, edited, person-written, approved, or made from an older original) is never replaced;
- **nothing missing:** no revision, no translation service called, and no error even when no service is available.

**Page:**
- «Oversett alle» is removed.
- When a test plan opens and something is missing, the page translates in the background, showing «Oversetter …». Existing translations show at once.
- Editing never waits.
- **Deduplication:** one request per plan revision per browser session (`AutoTranslationRegistry`, a singleton). A running request is shared when the page is reopened, and the outcome is remembered.
- **Stale answers:** an answer for another plan, an older request or a closed page is ignored.
- **«Lagre»** waits for a running translation and saves on the new revision. Unsaved edits stay, and the save keeps the new translations.
- **On 409** (the tester saved meanwhile): one new attempt on the new revision.
- **Failure:** «Kunne ikke oversette — originalteksten vises.» with «Prøv igjen».
- **No service:** «Automatisk oversettelse er ikke tilgjengelig her — originalteksten vises.», with the server's reason as a tooltip.
- **An outdated translation:** «Må gjennomgås — originalen er endret etter oversettelsen». It is kept, not replaced.
- **Labels:** «Maskinoversatt — ikke gjennomgått» is unchanged. Norsk / Original / Sammenlign are kept.

## Tests

**Backend** (4 new):
- first opening translates the missing text, then nothing (no revision, no service call);
- edited, outdated and approved translations are never replaced, and only new cases are translated;
- a save in between wins (409);
- no service: `translation-unavailable`, plan unchanged.

**Frontend** (8 new):
- first opening;
- existing translation, so no request;
- no service: honest note, no request;
- failure, then «Prøv igjen»;
- reopening never sends a second request;
- unsaved edits survive, and the save waits and uses the new revision with translations;
- an answer for a page left behind is ignored;
- an outdated translation is «Må gjennomgås» and kept.

**Suites:** frontend full suite 9,726 passed; backend TestPlanning 633 passed / 3 skipped.

## Browser verification

**NOT VERIFIED yet:** it is planned for the next isolated walkthrough. The isolated instance must be set to `EmbeddedOffline`, never the managed LibreTranslate container (no Podman).

## Limitations

- **Missing requirement wording alone** does not trigger a translation (cases and stories do).
- **Reviewer confirmation:** there is no separate «confirm translation» action. Editing a machine translation marks it «Maskinoversatt og redigert».
