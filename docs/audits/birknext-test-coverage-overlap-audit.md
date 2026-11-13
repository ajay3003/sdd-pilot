# Test Coverage & Overlap Review — audit, part 1 (2026-11-13)

## "Possible duplicates: 0" vs a list of overlapping pairs (confirmed defect)

The Overview card counted only `OverlapKind.HighConfidenceOverlap` and called it "Possible duplicates". The "Possible Duplicate Tests" tab
listed **every** overlap kind: partial, complementary, different boundary, needs tester review, insufficient evidence. So a result with
several partial overlaps showed "0 possible duplicates" above a list of pairs. That is two populations under one word.

**Fix:**

- The card now counts the same population the tab lists, and is labelled **"Overlap observations"**.
- Its subtitle breaks the count down by kind, e.g. "65 QA tests compared · 2 exact duplicate candidates · 5 partial behavioral overlaps".
- The tab is renamed **"Overlap Review"**.
- The kinds are now named Exact duplicate candidate, Partial behavioral overlap, Distinct verification purpose, Similar behavior at a
  different test level, Needs tester review, and Insufficient evidence.
- A new test asserts that the card value equals the number of listed overlaps.

## Wording

The blanket "Probably do not need to repeat" / "Probably does not need to be repeated" is gone. It now reads **"Potential overlap —
test-lead review required"**, both in the backend recommendation and in the page, Markdown and HTML exports.

## Given/When/Then spacing (root cause)

The scenario container is `white-space: pre-wrap`. A line break in the Razor markup between two rendered parts becomes a whitespace text
node, which pre-wrap renders as a blank line.

- The When→Then gap was fixed earlier.
- A remaining Given→When gap had the same cause. Given and the When/Then block are now emitted with no text node between them.
- A test asserts that no whitespace node remains at either point.
- Test Plan Suggestions does not render G/W/T labels (it uses Preconditions/Steps/Expected), so it has no such gap.

## Not done (rest of the brief)

- The seven-view information architecture.
- Journey explorer, coverage matrix, requirements and risk.
- Side-by-side test comparison with decisions.
- The Remaining QA Scope backlog and Test Plan Suggestions drafts.
- QA report restructure.
- Performance measurements, browser and real-M2LB acceptance.
