# Target Environments → Target Application: report (2026-11-05)

Branch `008-traceability-first`, not pushed.

## A. Audit findings

**P0: Search Applications could not find M2LB.**

- The picker's options came only from `ProjectEvidencePackages.All(lifecycle, …)`, which reads `lifecycle.ProjectImports` (Import Project records in the workspace), plus names other Target Environments already use.
- Sources imported through **Source Management / Source Analysis** (the eight M2LB ZIPs) and the **Delivery Reports** project "M2LB" were never offered.
- So a user who imported M2LB that way searched "M2LB", found nothing, and created the application by hand.
- Matching was also a plain substring on the display name: no aliases, and no tolerance for separators.

**Other findings:**

- **"Change application" dominated the page.** It showed even when an application was selected, with a permanent "Keep M2LB" button.
- **Two overlapping detection cards.** "Target check" and "Detected from target" showed overlapping information, and there was no single next action.

## B. Search Applications fix

`Services/ProjectEvidence/ApplicationSearch.cs` is a pure service; its rules are unit-tested. It searches everything BirkNext already knows, in this order:

1. **Registered applications.** Target Application identities used by Target Environments (with how many environments use each). They are chosen as they are, never duplicated.
2. **Available BirkNext projects.** Import Project packages that hold application source or a documentation project.
3. **Imported application sources.** Source Management / Source Analysis snapshots whose application-code area found something. Infrastructure-only and library-only packages (for example, Terraform) are never applications.
4. **Delivery projects.** Delivery Reports projects, loaded once per picker session. If loading fails, the user sees an error with Retry, never "no applications found".

**Matching and de-duplication:**

- Case-insensitive, partial, ignores separators ("m2lb language" finds "M2LB-language"); archive file names count as aliases.
- Ranked: registered first, then exact, prefix and contains matches.
- A linkable source whose name already belongs to a registered application is not listed twice. The registered entry names what it links.
- Similar names (M2LB, M2LB-language, M2LB.Common) remain distinct identities.

## C. Identity and association

- **Model kept.** Target Application identity stays per Target Environment, as before (`TargetApplicationIdentity`; Project evidence binds packages to it).
- **Registered selection.** Choosing a registered application reuses its name, so no second record is created.
- **Create and link application.** Choosing an imported project, source or Delivery project opens this step:
  - the name is prefilled and editable, and must be confirmed;
  - an Import Project package is linked by its import ID;
  - the Frontend URL and authentication are never set from it;
  - if the confirmed name matches a registered application, that application is selected instead.
- **Changing to a different application** still asks for confirmation and archives the previous application's evidence. Nothing is deleted.
- **Manual creation** remains available under **Advanced — add an application manually**. A name that already exists is reused.

## D. UI

- **Selected application:** a compact summary ("Selected application **M2LB**") with **Change application** and **View project evidence**. The picker opens only when requested. **Cancel** keeps the current application; the permanent "Keep M2LB" button is gone.
- **Target Detection summary** — one section:
  - state, Frontend URL;
  - badges: reachability ("Reachable · sign-in required"), framework, identity provider, detection confidence, and "Runtime not verified" (a detected provider is not verification);
  - **Check target again**, plus one derived next action (`TargetApplicationNextActions`):

    | State | Next action |
    |---|---|
    | No application | Select application |
    | No Frontend URL | Configure the Frontend URL |
    | Not checked, stale, failed or unreachable | Check target |
    | Sign-in required, no session | Continue to Authentication |
    | Otherwise | Open Endpoint Discovery |

  - Source-only Endpoint Discovery is always offered as an alternative.
- **View detection evidence** (collapsed) holds the former Target check and Detected-from-target cards with all their details, evidence and Frontend URL settings, unchanged.

## E. Detection and readiness

- **Detection.** Unchanged: the same `DetectConfigurationAsync` and detection snapshot.
- **Cross-tab.** The detected Entra provider reaches the Authentication tab. Its configuration is adopted only through the trust policy in the Authentication remake (f89e3a91).
- **Environment isolation.** Unchanged: application, URL and authentication are per profile, and nothing is copied between environments.

## F. Tests

**Automated:**

| Suite | Result |
|---|---|
| New `ApplicationSearchTests` | Sources and Delivery projects found; registered listed once first; aliases; infrastructure excluded; similar names distinct |
| New `TargetApplicationNextActionTests` | Every next-action rule; reachability vocabulary |
| New page test | Summary, collapsed evidence, derived next action, URL unchanged by a check |
| Updated `TargetEnvironmentProjectPaneTests` | Create-and-link step; case-insensitive partial search |
| Affected groups (Target Environment / Project Evidence / Authentication / Detection / proxy / Edge) | 1125/1125 passed |
| Full frontend suite (`dotnet test frontend/BirkNext.Web.Tests`) | **8095 passed, 0 failed** |

**Real browser** (`scratchpad/ta-e2e.cjs`):

- **Data:** the M2LB copy (DB `birknext_gaps`), where environment `ci-live` holds the real M2LB source snapshots and Delivery project "M2LB".
- **Isolation:** the target URL is a `.invalid` placeholder and detection is answered by the script. No external host was contacted.
- **Result: 19/19 checks passed.**
  - M2LB was already selected: detected from the imported primary source M2LB.zip, with the target host naming M2LB.
  - Change application is hidden until requested; there is no Keep button.
  - Searching "m2lb" lists the registered M2LB once, first, naming what it links, then M2LB.Common and M2LB-language.
  - Terraform is never offered; a no-results search shows a clear empty state, not an error.
  - Choosing an imported source requires "Create and link".
  - Re-selecting M2LB creates nothing new, and the Frontend URL is unchanged.
  - Detection: Reachable · sign-in required, Blazor WebAssembly, Microsoft Entra ID, Runtime not verified.
  - The next action becomes Continue to Authentication, which shows the provider; source-only Endpoint Discovery is offered as the alternative.
  - The association persists across reload; no horizontal scroll at 390 px; no page errors.

## G. Remaining limitations

1. **No live target check.** A real check against m2lbdev.bufetat.no was not performed: tests must never call the real target. Detection was answered with a controlled response.
2. **No per-project application registry.** "Registered applications" are the Target Application identities of Target Environments in this browser's settings. There is still no server-side application registry or multiple applications per project. That is the existing model, kept on purpose, and a candidate for the queued Source Management / Project Evidence work.
3. **Advanced Target Settings not reorganised.** Request timeout and retry count stay in the existing General/edit form; they were not moved into a new section.
4. **Search is not debounced.** It filters locally, so there is no network request per keystroke; only Delivery projects are fetched, once per picker session.
