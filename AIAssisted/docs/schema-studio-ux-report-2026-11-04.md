# Schema Studio UX — report (2026-11-04)

Branch `008-traceability-first`. Nothing was pushed. No functionality was removed.

## Done

1. **Business structure shows hierarchy before interaction** (the main usability bug). Every group row now carries:
   - a visible chevron (▸ collapsed / ▾ expanded, driven by the native `<details>` state, so it stays keyboard-operable and announces expanded/collapsed);
   - a **Group** kind marker;
   - its name and occurrence chips;
   - **"N children · M fields"**: direct children and every field below, visible without opening the group.

   Leaf rows show "• **Field**". Groups and fields are distinct before any click.
2. **Effective structure is a tree-table.**
   - Columns: **Structure** (expandable tree, indented by depth, with chevron and child count) | **Type** (readable type + raw XSD type, "Group" for groups) | **Occurrence** (exact cardinality + compositor) | **Rules** (plain-language rules or "—") | **Declared in** (file, inherited from, type file).
   - At ≤ 760 px each row stacks.
3. **Overview as a guided landing.**
   - **Layer 1:** package identity, unchanged.
   - **Layer 2:** confidence is now two grouped panels, **✓ Verified here** and **○ Not verified here**, each a list rather than one dense line.
   - **Layer 3:** the **Start here** cards (Explore structure / Review fields & rules / Run generated tests or Review schema rules) come first on the Overview, then "What this means", then the package figures ("Package figures").
4. **Dependency graph:**
   - larger canvas (max(36rem, 65vh));
   - wider nodes (236 px) with less label truncation; the full name stays as tooltip and in the details panel;
   - zoom up to 250 %;
   - a **Full screen** mode with an "Exit full screen" toggle.
5. **Tests:**
   - Business structure: every group has a chevron, the Group kind and a child count; leaves are Fields.
   - Effective structure tree-table: header columns, group rows with "Group" type and child count, field rows.
   - All 279 Schema Studio tests pass.

## Not done in this pass

- **Fields & rules:** column toggles. Documentation is already truncated, and source/provenance is already behind a disclosure.
- **Types:** compact summary rows and filters (category / has rules / not used / sort by usage).
- **Constraints:** "Top rule hotspots".
- **Validate & Test:** split into four sections.
- **Badge hierarchy:** distinct styles for type / rule / usage / status in the Schema tree.
- **Detail drawer:** a side drawer for type details.
- **Navigation:** regrouping into Read / Technical / Inspect / Validate. The existing five areas are kept, because many deep links and tests use them.
- **Graph interaction:** node-type colours and fit-to-selection.
- **Browser acceptance:** not run for Schema Studio in this pass. It is verified by bUnit against the real Skolenærvær XSD package.
