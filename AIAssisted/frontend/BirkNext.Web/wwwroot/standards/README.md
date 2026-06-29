# Standards Compliance Rule Packs

This folder contains the rule packs used by the **Standards Compliance** feature in BirkNext QA Review Studio.

Rules are defined in JSON files, not in C#. Adding, updating, or versioning a rule pack requires no C# changes — only JSON file edits.

---

## Folder structure

```
standards/
  index.json                    ← manifest: tells the engine which packs to load
  README.md                     ← this file
  wcag/
    2.2/
      rule-pack.json            ← WCAG 2.2 rule pack
  owasp/
    asvs-top10/
      rule-pack.json            ← OWASP ASVS / Top 10 rule pack
  gdpr/
    documentation/
      rule-pack.json            ← GDPR documentation coverage rule pack
  iso/
    25010/
      rule-pack.json            ← ISO 25010 rule pack
```

The folder path reflects the standard + version. When a new version of a standard is released, a new subfolder is added (e.g. `wcag/2.3/`).

---

## index.json

`index.json` is the discovery manifest. The engine reads it to find all available rule packs. Each entry has:

| Field         | Required | Description |
|---------------|----------|-------------|
| `standardId`  | yes      | Free-form identifier string (e.g. `"WCAG22"`, `"NIST800-53"`). Must be unique across all entries. Case-insensitive. |
| `label`       | yes      | Display name shown in the UI (e.g. `"WCAG 2.2"`). |
| `description` | yes      | Short description shown in the UI (e.g. `"Web Content Accessibility Guidelines"`). |
| `path`        | yes      | Relative URL to the rule-pack JSON file (e.g. `"standards/wcag/2.2/rule-pack.json"`). |

**No C# code needs to change when adding a new entry.** The UI discovers available standards at runtime by reading this file.

Example:

```json
[
  {
    "standardId": "WCAG22",
    "label": "WCAG 2.2",
    "description": "Web Content Accessibility Guidelines",
    "path": "standards/wcag/2.2/rule-pack.json"
  }
]
```

---

## Rule-pack JSON schema

Each `rule-pack.json` has the following shape:

```json
{
  "standardId":      "WCAG22",
  "standardName":    "WCAG 2.2",
  "standardVersion": "2.2",
  "rulePackVersion": "1.0",
  "lastUpdated":     "2026-06-29",
  "description":     "...",
  "rules": [
    {
      "ruleId":           "wcag-keyboard",
      "category":         "Operable",
      "title":            "Keyboard Navigation",
      "description":      "All interactive functionality must be accessible via keyboard.",
      "severity":         "High",
      "requiredSections": ["accessibility", "non-functional requirements"],
      "requiredKeywords": ["keyboard navigation", "tab order", "focus trap"],
      "optionalKeywords": ["keyboard", "tab key"],
      "evidenceHint":     "Look for keyboard navigation mentions.",
      "recommendation":   "Document keyboard navigation requirements."
    }
  ]
}
```

### Top-level fields

| Field             | Type   | Required | Description |
|-------------------|--------|----------|-------------|
| `standardId`      | string | yes      | Must match the `standardId` in `index.json` for this pack (case-insensitive) |
| `standardName`    | string | yes      | Display name shown in findings table and summaries |
| `standardVersion` | string | yes      | Version of the official standard (e.g. `"2.2"`) |
| `rulePackVersion` | string | yes      | Version of this rule pack file (e.g. `"1.0"`) |
| `lastUpdated`     | string | yes      | ISO date of last update (e.g. `"2026-06-29"`) |
| `description`     | string | no       | Summary of what the pack checks |
| `rules`           | array  | yes      | Array of rule objects (must be non-empty) |

### Rule fields

| Field              | Type     | Required | Description |
|--------------------|----------|----------|-------------|
| `ruleId`           | string   | yes      | Unique ID within the pack (e.g. `"wcag-keyboard"`) |
| `category`         | string   | yes      | Grouping label shown in the findings table |
| `title`            | string   | yes      | Short rule name shown in the findings table |
| `description`      | string   | yes      | What the rule checks |
| `severity`         | string   | yes      | `"Critical"`, `"High"`, `"Medium"`, `"Low"`, or `"Info"` (case-insensitive) |
| `requiredSections` | string[] | no       | Informational — document sections this rule targets |
| `requiredKeywords` | string[] | yes*     | Strong signal: if ANY term is found → **Passed** |
| `optionalKeywords` | string[] | yes*     | Weak signal: if any strong term misses but a weak one hits → **Warning** |
| `evidenceHint`     | string   | no       | Internal hint describing what good evidence looks like |
| `recommendation`   | string   | yes      | Shown in the findings table when status is Warning or Manual review recommended |

\* At least one of `requiredKeywords` or `optionalKeywords` must be non-empty.

### How keyword matching works

The engine concatenates all loaded artifacts (constitution, spec, plan, tasks) into a single string and searches case-insensitively:

1. If **any** `requiredKeywords` term is found → **Passed** (shows the matching line as evidence)
2. Else if **any** `optionalKeywords` term is found → **Potential gap** (shows the matching line as evidence)
3. Otherwise → **Manual review recommended** (no evidence)

The score per standard is: `(passed × 1.0 + warnings × 0.5) / applicable_checks × 100`.

---

## Versioning rules

### Two version numbers

Each rule pack has two separate version numbers with different meanings:

| Version           | What it tracks | When to increment |
|-------------------|----------------|-------------------|
| `standardVersion` | The **official standard** (e.g. WCAG 2.2) | When the governing body releases a new version of the standard |
| `rulePackVersion` | This **JSON file and its checks** | When you add, remove, or change rules without the official standard changing |

### Adding a new official standard version

When the official standard releases a new version (e.g. WCAG 2.3):

1. Create a new folder: `standards/wcag/2.3/`
2. Add `rule-pack.json` in the new folder with `"standardVersion": "2.3"` and `"rulePackVersion": "1.0"`
3. Add a new entry to `index.json` pointing to the new path
4. (Optional) Remove the old entry from `index.json` if it should no longer be checked

No C# changes are needed.

### Updating an existing rule pack (same standard version)

When only the rule pack's checks change (e.g. improving keywords, adding rules, fixing mistakes) while the official standard stays the same:

1. Edit the existing `rule-pack.json`
2. Increment `rulePackVersion` (e.g. `"1.0"` → `"1.1"`)
3. Update `lastUpdated` to today's date
4. No changes needed to `index.json`

### Difference between standardVersion and rulePackVersion

- **`standardVersion`**: Immutable once set for a folder. Tracks the external standard.
  - `"2.2"` for WCAG 2.2 — does not change when rules are improved.
- **`rulePackVersion`**: Changes with every maintenance update.
  - `"1.0"` → `"1.1"` after adding two new rules to the WCAG 2.2 pack, without WCAG itself changing.

---

## How to add a new standard

1. Decide on a `standardId` string. It can be anything unique — no C# enum exists.
2. Create a folder: `standards/<name>/<version>/`
3. Create `rule-pack.json` in that folder following the schema above.
4. Add an entry to `standards/index.json` with `standardId`, `label`, `description`, and `path`.

That's it. The engine discovers and loads the new standard automatically on the next page load.

---

## Error handling

If a rule pack fails to load (file missing, malformed JSON, validation error), the Standards Compliance page shows a warning banner. The engine continues running checks for all other successfully loaded packs. It does not crash.

A rule pack is considered invalid if:
- The file cannot be fetched (404, network error)
- The JSON cannot be parsed
- `standardId` is missing or blank
- `standardName` is missing or blank
- `rules` is empty or absent
- Any rule has a missing `ruleId`
- Any rule has an unrecognised `severity` value
- Any rule has empty `requiredKeywords` and empty `optionalKeywords`
