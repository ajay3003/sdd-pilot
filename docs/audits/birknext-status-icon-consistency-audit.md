# Status icon consistency (2026-11-13)

Starting HEAD: `383a0943`.

## Root cause (verified in source, not a rendering fault)
The `?` on Release Readiness is a **literal text glyph**. Release Readiness and `GovernanceGatePanel` each had a private icon switch:
- `"?"` for Not assessed and for any other value;
- `!` for Ready with limitations;
- `✕` for Blocked;
- plus a hard-coded heading `<span class="rr-icon">?</span>` and a hard-coded legend `? Not assessed`.

It was not a missing icon, a font fallback or CSS. The shared status vocabulary (`StatusVocabulary`, used by `SemanticStatusBadge`) already defines Not assessed as the neutral **○**. These pages simply did not use it.

## Change (presentation only)
- `ReleaseReadinessPresentation.Icon(GateDecision)` and `.LevelIcon(FindingLevel)` are now the one mapping, taken from `StatusVocabulary`:

| Decision | Icon | Source |
|---|---|---|
| Ready | ✓ | `SemanticStatus.Ready` |
| Ready with limitations | ⚠ | `SemanticStatus.Warning`, so it is never the same mark as Ready |
| Passed with reviewed exception | ✓! | unchanged |
| Blocked | ⛔ | `SemanticStatus.Blocked` |
| Not assessed, and any undefined value | ○ | `SemanticStatus.NotAssessed`, the neutral outlined circle |
| Finding level Not assessed | ○ | same |

- Release Readiness heading, legend and dimension chips and the Governance gate panel all use it. The legend is rendered from the same mapping, so legend and chips cannot disagree.
- Icons stay `aria-hidden`, and the label is always visible next to them: meaning never depends on the icon or its colour.
- **Not changed:** gate decisions, readiness calculation, dimensions, required evidence, persistence, history and authorization.

**Cross-module audit:**
- The other `?` icons are `Unknown`/`Unresolved` (where `?` is the vocabulary's own icon) or unreachable fallbacks: Analysis, Library and Review readiness panels map every enum member.
- None is a confirmed instance of this defect, so they were not changed. No global replacement of `?` was made.

## Verification
| Check | Result |
|---|---|
| Unit: every `GateDecision` and `FindingLevel` has a non-`?` icon; the icons are distinct; an undefined value falls back to ○; labels non-empty | PASS |
| Page tests: Not assessed heading icon ○ and `aria-hidden`; the legend chips are exactly ✓ Ready, ⚠ Ready with limitations, ⛔ Blocked, ○ Not assessed; no `?` in Evidence dimensions; Ready-with-limitations decision shows ⚠; Governance Blocked shows ⛔ | PASS |
| Full frontend suite | **8 738/8 738** |
| Browser (isolated Release web, fresh publish, integrity 86/86; isolated API) | Not assessed heading `○ Release Readiness — Not assessed` (icon `aria-hidden`); legend and dimension chip as above; **0 bare `?` icons**; 0 console errors; 0 external requests |
| Responsive 1440/1280/1024/768/390 | No horizontal overflow; no wrapped chip; chip text 12.48 px. Screenshots `scratchpad/mb/out/rr-icons-<width>.png` |

**Not visually verified in the browser:** Ready, Ready with limitations and Blocked as the page's *decision*. The isolated workspace has no relationship analysis, so only Not assessed can be rendered for real. Those three appear in the browser only as legend chips; as decisions they are covered by the component tests.
