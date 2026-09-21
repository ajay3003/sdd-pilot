# Critical E2E Regression

Answers the delivery requirement:

> *Ende-til-ende-test for minst én kritisk brukerflyt per modul er på plass ved leveranse (regresjonstest).*

…in the two halves it actually has. **Definition coverage** — does every module have a required critical flow configured
— and **release validation** — did those flows pass against the build being shipped. The page states them separately,
because a module whose flow is configured but has never run is not covered in any sense that matters.

---

## Two execution modes

| | Companion browser | Automated integration |
|---|---|---|
| Reaches the system through | Browser Companion content script in your normal Edge | REST / GraphQL via the authenticated review gateway |
| Authentication | You sign in and complete MFA by hand, once | An API context BirkNext already holds |
| Unattended? | No — it needs a signed-in browser | Yes, a pipeline can run it |
| Called | **Attended browser automation.** Not "manual testing": the login is manual, the flow is not. | |

There is no Playwright and no CDP anywhere in this. Defender for Cloud Apps refuses `Target.attachToTarget` for the M2LB
origin, pre-authentication, and that refusal is not something to work around. The companion drives the page through a
content script the browser itself runs, in a session a person authenticated — which is why it needed no new permission:
`manifest.json` is byte-identical to before this capability existed.

---

## What a result means

| Status | Meaning |
|---|---|
| **Passed** | The flow's **final business assertion** held. Every step executing is *not* enough — a flow where all the clicks succeeded and nothing was asserted is Blocked, because it proved nothing. |
| **Failed** | The flow ran and the application's result was wrong or missing. |
| **Blocked** | A prerequisite was unavailable — pairing, page, session, configuration, transport, environment. We never got to find out whether the application was right. |
| **Cancelled** | You stopped it. |

Release disposition keeps the same distinction: **Incomplete** means a required flow has not run against this build yet
(nothing is known to be wrong), **Blocked** means one failed or could not run.

A pass recorded against build 12344 does not satisfy build 12345. It reads as *Pending* and names the build it came from.

---

## Writing a flow

Open **Quality → Critical E2E Regression → Add critical flow**. The editor refuses to save a flow that could never pass,
using the same rule the runner applies.

**Every flow needs a final assertion.** Tick *Final assertion* on the step that checks the business outcome —
the status that should be visible, the record that should exist, the route the user should have reached.

**Browser actions:** `Navigate`, `Click`, `Fill`, `Select`, `WaitForVisible`, `WaitForText`, `WaitForRoute`,
`AssertVisible`, `AssertHidden`, `AssertText`, `AssertValue`, `AssertRoute`, `ReadValue`.

**Selectors, in the order you should prefer them:**

1. `TestId` — `data-testid`. Survives copy changes and the Norwegian/English switch.
2. `Role` + accessible name — e.g. role `button`, name `Lagre`.
3. `Label` — for form controls.
4. `Text` — the visible name of a link or button.
5. `Css` — last resort.

A selector that matches more than one element **fails**. It does not pick the first: acting on "whichever matched first"
is how a suite silently exercises the wrong control and still reports green.

**Variables:** `${RunId}`, `${CorrelationId}`, `${Timestamp}`, `${RandomGuid}`, and `${step:<stepId>}` for an earlier
step's observed value. Nothing else — an unknown token stays literal rather than becoming an empty search.

**Integration expectations** are evaluated over what the authenticated gateway exposes: `StatusCode`, `GraphQlHasData`,
`GraphQlErrorCount`. It deliberately does not hand back response bodies, and this runner does not ask it to. To assert
that something propagated, write a query filtered by `${CorrelationId}` that returns data only when it has, and expect
`GraphQlHasData = True` — the assertion then happens where the data already lives.

**Test data is synthetic only**, prefixed `M2LB-E2E-`. No real personal data, no real national identity numbers.

---

## Ask for the M2LB team

Critical journeys need stable hooks on a *small* set of controls — not test ids everywhere.

```html
data-testid="case-search"
data-testid="case-result"
data-testid="placement-status"
data-testid="save-button"
```

Text and role selectors work today, but they break on copy changes and on the Norwegian/English switch. Stable test ids
on the handful of elements the release-critical journeys touch are the difference between a suite that lasts and one that
gets muted. Record the ids a flow depends on in its description — they are part of the flow's contract.

---

## Running a companion browser flow

1. Open the application in your normal Edge and **sign in, completing MFA as usual**.
2. Pair the Browser Companion for that Target Environment, and leave an application page open.
3. Open **Critical E2E Regression**. Opening the page tells the companion a run is expected, so it starts polling at step
   speed rather than at the 30-second liveness cadence — by the time you press Run it is already listening.
4. Check the *Companion browser* card says **Ready**. If not, it says what is missing and what to do.
5. **Run browser regression**, or Run on a single flow.

Login and MFA are never automated. Nothing else in the flow is manual.

---

## Safety

Enforced, with tests, at every layer rather than by hiding a button:

- **Production is never driven.** Refused in the backend, again in the extension worker, and again in the page. An
  unknown environment type counts as production — guessing in the permissive direction is how a probe ends up clicking
  somewhere real.
- **No arbitrary JavaScript.** A command carries an action *name* and a described element. `build.mjs` fails if `eval(`
  or `new Function` appears in any shipped extension script.
- **One command, one action.** A command leaves Pending exactly once, so a retried heartbeat or a worker that restarts
  mid-flight cannot turn one click into two. A command that outlives its deadline expires rather than firing after you
  have navigated away.
- **User-like actions only.** A hidden control is not revealed, a disabled control is not forced, an overlay is not
  removed, a read-only field is not written. `Fill` verifies the control kept the value rather than trusting that the
  assignment returned.
- **No credentials anywhere.** No token, cookie, header or payload reaches a command, a result or the stored history.
  Summaries, routes and observed values pass through the same redaction the evidence path uses.
- **Same origin only.** The origin a browser step acts on comes from the paired session, not from the flow definition.

---

## Not in this version

- **Event Hub publication.** It reports Blocked with the reason rather than guessing a namespace, an entity or an
  identity. Drive the flow from an API step, or publish by hand and record the result.
- **Headless or unattended companion browser runs.** It needs a real authenticated browser session by construction.
- **Production browser automation, MFA automation, a recorder, visual regression, branching, parallel flows.**
