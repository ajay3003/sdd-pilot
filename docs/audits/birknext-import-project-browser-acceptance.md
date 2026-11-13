# Import Project — browser acceptance (2026-11-13)

**Setup:**
- isolated Release web published fresh (`mb/web4`, Blazor integrity 86/86) and the isolated API (`birknext_mb`, current import M2LB, 7 evidence packages);
- Edge; non-localhost aborted; harness `scratchpad/mb/import-accept.cjs`.

**13/13 PASS:**

| Check | Result |
|---|---|
| IP-01 | Import ZIP visible in the header without scrolling (top 62 px) with a current import present |
| IP-02 | Opens the existing form **above** the current import; focus on the file chooser |
| IP-03 | Intent "What is this ZIP?" shown; *Add evidence to current application* checked by default, naming M2LB |
| IP-04 | *Import another application* switches the target (`NewApplication`) and the heading |
| IP-05 | A real synthetic ZIP (`nav/demo-import.zip`: one spec, one .cs file) is uploaded, validated and **previewed** |
| IP-06 | Choose another file, then Cancel: **no commit request**; the form closes; focus returns to Import ZIP; M2LB is still the current import |
| IP-07 | After a refresh, Import ZIP is still the header action |
| IP-R1440/1280/1024/768/390 | Import ZIP visible without scrolling at every width (top 266 px at 390 px); no horizontal overflow; text 14.4 px |
| IP-Z | 0 console errors, 0 external requests |

Screenshots: `scratchpad/mb/out/import-<width>.png`, `import-preview.png`.

**Not run in the browser (by choice, to keep the shared acceptance workspace unchanged):** committing an import, a failed commit and retry, and real MU/Altinn role imports. Commit, duplicate and history behaviour are covered by the existing 133 Import Project tests, which are unchanged and passing.
