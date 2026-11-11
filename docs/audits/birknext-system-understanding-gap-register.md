# BirkNext — System Understanding: gap register (2026-11-11)

| ID | Gap | Status | Note |
|---|---|---|---|
| SU-G1 | Real-source verification of the Skolenærvær guide against the six packages (found in `C:\Users\ajaan\Downloads`; `C:\Downloads` does not exist) | CLOSED 2026-11-11 | The seed was rewritten from the independent oracle (`birknext-skolenaervaer-evidence-matrix.md`). Both M2LB generations are kept apart; what remains unknown stays Needs clarification or Conflicting. The seed source-basis defect is fixed (see `birknext-skolenaervaer-integration-gap-register.md` BN-4/BN-5) |
| SU-G2 | No source text viewer (file/line) | NOT SUPPORTED | BirkNext never retains source text. Citations name snapshot, file, symbol and lines, and link to the owning explorer |
| SU-G3 | No separate reviewer/approver role; the same configuration-write permission edits, approves and publishes | OPEN | No independent four-eyes approval is enforced |
| SU-G4 | Guides are not scoped to a project or workspace selection; the catalog applies to the installation, and sources are recognised by role across Source Analysis snapshots | OPEN | Change awareness lists every recognised snapshot separately and never picks the newest |
| SU-G5 | Change awareness works at snapshot level (imported or removed), not per changed file, route or namespace | OPEN | Snapshots keep no file contents to diff; per-claim granularity would need evidence-location matching |
| SU-G6 | Editing covers purpose, step text ("what happens", "how to check") and "needs clarification" marks; rules, scenarios and edges cannot yet be edited in the UI | OPEN | The API accepts any valid content |
| SU-G7 | Approve and publish are not available in the Tester Package (configuration writes are Development-only); readers see the page read-only | OPEN | Same as SC-G7 |
| SU-G8 | No Project Quality signal for guide state (available / reviewed / published) | NOT RUN | Deliberately not added to quality metrics; could be a separate documentation signal |
| SU-G9 | Synthetic fixtures A–J of the brief: covered A-like (no guide: empty state), C/E/F (draft edits isolated from the published revision; source change → may need review, old revision preserved), G/H-like (scenarios never coverage; no verified claim), I (unsafe links and markup refused), generic second system | PARTIAL | B (specifications only) and D (two conflicting snapshots) are represented in the seed's version facts, not as separate imported fixtures; J (partial provider failure) is not exercised |
| SU-G10 | Scratch database `birknext_su_20261111` | OPEN (needs approval to drop) | Kept |
