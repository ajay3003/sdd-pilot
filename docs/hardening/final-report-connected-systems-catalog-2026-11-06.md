# Connected Systems + Integration Catalog — guided workflow (2026-11-06)

Target Environments → Connected Systems and → Integration Catalog now each start with numbered guided steps. A step shows ✓ when done, and the next step is marked. Each step has its state, one sentence, its evidence, and at most one secondary action or link. The steps are derived from each page's own state. There is no new readiness model, and Authentication's workflow is unchanged.

## 1. Architecture and UX findings (source verified)

**Connected Systems** (`ExternalIntegrationsPane`, backend `ExternalIntegrationService`)

- **Why it shows "0 · 0 · 0 · 0" next to 3 catalog proposals.** This is a real gap, but not a bug in either count. Detection (`POST …/external-integrations/detect`) loops over the registered `IExternalProviderHandler`s.
  - Only `AltinnProviderHandler` is registered, and it proposes only when the Altinn app id resolves (from the Source Analysis AppId, public metadata or saved settings).
  - Detection never reads the Integration Catalog, Test Pack templates or Source Analysis relationships.
  - Catalog proposals are *logical integrations*. They name their systems in free text: there is no foreign key to a Connected System, and accepting a proposal does not require a registered system.
  - So nothing should be invented here from catalog proposals. The page now says this explicitly when Detect finds nothing.
- **Candidates are per browser session.** They come from this session's last Detect (`ConnectedSystemsDetectionSession`). The count is 0 on a fresh load until Detect runs.
- **Ignore is session-only.** There is no persisted reject or defer decision.
- **Duplicate Detect action.** The header's "Detect systems" and the empty state's "Detect from imported evidence" ran the same action. The empty-state set of Detect/Add/Import buttons is removed; the header keeps one of each.
- **Runtime.** Generic providers report Runtime *Not assessed*. Altinn reports *Not verified*, or *Observed* after a recorded observation. Nothing checks connectivity automatically.

**Integration Catalog** (`IntegrationsPane`)

- Proposals come from `IntegrationPackProposals.For`: Test Pack templates that are not configured and whose evidence-source role is recognized in a Source Analysis snapshot. Source Analysis "technical relationships" are evidence, not proposals.
- Duplicates are refused by `IntegrationIdentity.Key`.
- There is no stored lifecycle of discovered → accepted → configured → validated → runtime verified. These states are separate facts that live in different places: proposal, catalog record (completeness), IQR evidence review, and runtime evidence. The steps now say which is which.
- The Utdanning ↔ SkoleAdapter identity conflict lives in the canonical model (review item `IdentityConflict`, governance `gov.identity.conflict`). Neither page read it. The catalog now points to Relationship Review for identity decisions and never decides them itself.

## 2. Authentication patterns reused

- **Reused principles:** sequential steps with ✓/number, a state per step, one sentence, a reason when an action is unavailable, one next step (`aria-current="step"`), completed steps kept visible, and status badges through `SemanticStatusBadge`.
- **Not reused:** `AuthWorkflowStep` and `AuthenticationWorkflowPanel` stay authentication-specific.
- **New domain-neutral primitive:** `GuidedStep` / `GuidedStepState` (`Services/GuidedWorkflow.cs`) and `GuidedStepList.razor` (+ css). Step actions are secondary: the host page keeps its one primary action.

## 3. Connected Systems before / after

| | Before | After |
|---|---|---|
| Orientation | Summary counts only | Steps: Environment → Detect systems → Review candidates → Configure systems → Validate |
| Detect | Two buttons (header and empty state) | One Detect, primary except while candidates are pending (then Accept is primary) or when Add is the next step after nothing was found |
| Nothing found | "Nothing new detected" | Detect step explains that catalog proposals are logical integrations that do not register systems; Add becomes the next step |
| Candidates | List | Current step with "N awaiting a decision"; ambiguous ones "need a person's decision" |
| Needs attention | Count | Step names the systems; "Review first system" opens it (opening never saves) |
| Validate | — | "Runtime verified only by explicit checks or recorded observations — never by registration"; verified / observed / not-verified counts |

## 4. Integration Catalog before / after

The steps are Discover integrations → Review proposals → Resolve requirements → Save configuration → Validate:

- **Discover.** Shows the proposals and the Source Analysis technical relationships, and says that relationships are "evidence, not proposals". If neither is available, it links to Source Analysis.
- **Review.** Shows "N open: R ready to review · D need a decision first … Nothing is added for you". The action focuses the existing proposals section.
- **Resolve.** Shows needs-confirmation and needs-configuration counts for the configured integrations. Suggested mappings stay unconfirmed until a person confirms them. Identity conflicts are decided in Relationship Review, which is linked.
- **Save.** "A proposal stays a proposal"; changes save immediately; duplicates are refused.
- **Validate.** "Configured is not validated", with the runtime evidence label and a link to Integration Quality Review.

## 5. Workflow state transitions

- **Connected Systems.**
  - Detect: Current when no system exists and Detect has not run in this session; Done after Detect.
  - Candidates: Current while any are pending; otherwise Done, or Waiting before Detect.
  - Configure: Current when any system has a partial or blocked dimension, or when no system exists after an empty Detect; Done otherwise.
  - Validate: Done only when every system's Runtime dimension is Verified.
- **Integration Catalog.**
  - Discover: Current only without any evidence.
  - Review: Current while proposals are open.
  - Resolve: Current with open confirmations or configuration.
  - Save: Current when nothing is configured and nothing is open.
  - Validate: Current for configured integrations, since validation lives in IQR. It is never Done on this page.
- **On both pages only the first current step is current.** Later ones wait.

## 6. Evidence and decision safety

- Nothing is accepted, confirmed, rejected or saved by the steps.
- Step actions only focus or open existing sections, or link to the owning page.
- No Connected System is invented from catalog proposals.
- The identity conflict and every review decision are untouched.

## 7. Files changed

- `frontend/BirkNext.Web/Services/GuidedWorkflow.cs` (new)
- `frontend/BirkNext.Web/Components/GuidedStepList.razor` and `.css` (new)
- `frontend/BirkNext.Web/Services/ConnectedSystemsWorkflow.cs` (new)
- `frontend/BirkNext.Web/Services/IntegrationCatalogWorkflow.cs` (new)
- `frontend/BirkNext.Web/Components/ExternalIntegrationsPane.razor` (+ css)
- `frontend/BirkNext.Web/Components/IntegrationsPane.razor`
- Tests:
  - `ExternalIntegrationsUiTests`: +3, one test redefined (empty state without duplicate actions), one assertion changed (Detect is secondary while candidates are pending);
  - `IntegrationTemplateUiTests`: +1;
  - `Services/IntegrationCatalogWorkflowTests`: new, 4 tests.

## 8. Tests and results (actual runs, 2026-11-06)

**Automated.** The frontend filter covered Integration*, ExternalIntegrations*, Authentication*, ConnectedSystem* and GenericProvider*: **773/773 passed**. This includes:
- every Authentication test, run unchanged;
- the new step tests;
- `IntegrationCatalogWorkflowTests` (4).

The first run had three failures. They were fixed as follows:
- An earlier, deliberate rule says "Detect stays primary with systems" (`ExternalIntegrationsUiTests`). It is kept: Detect is secondary only while candidates are pending, or when Add is the next step after an empty Detect.
- The Discover step had a "Show technical relationships" action that duplicated the existing call to action. The action was removed.
- Test Pack proposals without a Source Analysis report now count as evidence that was read.

**Browser.** `cs-catalog-e2e.cjs` ran against the published Release web build on the local stack (`birknext_gaps`, localhost only): **15/15 passed**.
- **Connected Systems:**
  - five steps, with Detect as the one current step;
  - at most one primary button in the header;
  - no duplicate empty-state Detect;
  - Validate says "applies to registered systems";
  - no horizontal scroll at 390px.
- **Integration Catalog:**
  - five steps;
  - the review step matches the summary (2 open, both "need a decision first");
  - Validate is "Not assessed", never validated;
  - the resolve step points to Relationship Review;
  - "Review proposals" focuses the proposals heading.
- **Authentication:** still renders its own `awf-steps`.
- **Safety:** no state-changing request, no external host and no page errors.
- Screenshots are in the scratchpad folder `shots-cs/`.

## 9. Remaining limitations

- **Detection is still Altinn-only on the backend.** Adding providers that propose systems from Source Analysis (MU, SkoleAdapter, Event Hub, Service Bus, Bufdata) is a backend change that needs its own evidence rules. It was not done here, so as not to invent systems.
- **"Ignore" for a candidate is still session-only.** A persisted defer or reject decision needs a storage design.
- **The catalog does not query the canonical review queue.** The identity-conflict pointer is a standing link, not a live count.
