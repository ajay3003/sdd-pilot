# Schema Studio — UX audit and P0 default-view fix (2026-11-13)

Branch `008-traceability-first`. The starting HEAD was 01548852.

## Ownership (verified)

- **Page:** `Pages/SchemaStudio.razor`, about 2,190 lines. It holds every view in one `switch (_tab)`.
- **Navigation:** `Services/SchemaStudio/SchemaStudioNavigation.cs` defines five areas. Each area has its own views, plus `DefaultView`,
  `?view=` slugs and legacy identifiers.
- **Library or message:** `SchemaStudioPresentation.IsLibrary` → `ContractEvidenceQueries.EntryPoint`, which is the shared entry-point rule.
- **Parser, validation, drafts, versions:** `SchemaStudioService` and the shared XSD engine (`BirkNext.Schemas`). None of these was
  touched by this change.

## Root cause: "Understand Schema" opened Overview

No control is literally labelled "Understand Schema". The paths a user takes to "understand" a schema are:

1. clicking the **Understand** area: `SelectGroup` → `group.Views[0]`, which was `"Overview"`;
2. importing or selecting a package: `Select()` → `_tab = _pendingView ?? SchemaStudioNavigation.DefaultView`, which was `"Overview"`.

Both paths ended on Overview, so every user needed one more click to see the structure.

## Fix (P0)

- `DefaultView` is now **Schema Tree**, and the Understand area's views are ordered Schema tree → Overview → Business structure →
  Effective structure.
  - Import, selection and the Understand area all open the Schema tree.
  - An explicit `?view=` deep link (overview, designer, compare, generated-tests, …) is still honoured. A deep link opened before any
    package exists still applies to the first import only.
- The Schema tree begins with one line from the shared entry-point rule:
  - a library gets "This schema defines reusable types but no complete message element", and is not called invalid;
  - a message schema gets its entry point.

  The tree already lists every global element and reusable definition by file, with constraints and technical details on demand. No
  message is invented.

Tests:

- `SchemaStudioHierarchyTests`, 7 new:
  - a library opens its definitions;
  - a message opens with its root;
  - Understand from another area opens the Schema tree;
  - four explicit deep links are honoured.
- Nine existing tests that assumed an Overview default now open Overview explicitly.
- The focused Schema and Feature Visibility tests passed 345/345.

## Not done yet (remaining scope of the redesign brief)

- Compact package header.
- The four-workspace navigation (Explore / Compare / Design / Validate & Test).
- Merging Schema tree and Types.
- A type focus view.
- Grouped constraint and enumeration explorer.
- Designer editor improvements.
- Compare and validation hardening.
- Type-level library tests.
- Real M2LB XSD acceptance.
- Browser and responsive acceptance.

None of these was implemented in this pass.
