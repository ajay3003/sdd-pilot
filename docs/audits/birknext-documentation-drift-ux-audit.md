# Documentation Drift — UX audit and change (2026-11-13)

Starting HEAD: `01cb16e1`. Components: `Components/DocumentationDriftPanel.razor` (tabs), `Services/DocumentationDriftCards.cs` (card state, label, tone) and `Services/DocumentationQuality.cs` (`QualityAreaStatus`: Compared, NotAssessed, MissingGenerated, MissingAuthored, Ambiguous, NotSupported).

## Verified issues and changes (presentation only)
| Issue | Root cause | Change |
|---|---|---|
| Ready-to-compare cards look like a positive result | `DocumentationDriftCards.Tone(ReadyToCompare) = "ready"` (blue) | Neutral tone. Green stays reserved for `Consistent`, a completed comparison without differences |
| "Compared" area chip is green, which reads as "consistent" | `AreaTone(Compared) = "green"` | Info (blue). Its findings say whether anything differs |
| "Ready to compare" area chip is blue | `AreaTone(NotAssessed) = "blue"` | Neutral (grey) |
| A "Not supported" chip repeated in every module row | One chip per area, per module | Each module row lists only comparable areas. One line summarises the unsupported areas (labels, area and module counts) as a capability limitation, not missing documentation and not drift. Every area record is kept |

**Already correct (kept):**
- Findings before any comparison say *Not assessed — no area has been compared yet* with a link to Compare; never "No differences found".
- Analyzer verdict and human review are separate fields.
- Ambiguous pairs stay "review".

## Not done in this pass (gaps)
- No restructuring of the five tabs and no compare-selection changes.
- Stale-result behaviour unchanged and not re-verified in the browser.
