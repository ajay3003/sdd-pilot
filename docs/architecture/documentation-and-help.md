# Documentation and help architecture

## Sources of truth

- `NavigationCatalog` defines current sidebar rows, canonical route strings, labels, visibility rules, provider hints and review IDs.
- `DocumentationCatalog.Features` is the explicit documentation-feature registry. `FeatureId` is stable identity; labels are presentation and may be renamed. Entries carry route, User Guide anchor, category, required status and optional provider/extension scope.
- Razor `@page` route attributes are the source for routability. Documentation Health discovers them from the application assembly, so valid routes such as `/source-analysis` are not rejected by a parallel static route list.
- The User Guide renders its Feature Matrix from the documentation registry. Longer guidance remains in the guide component.

## Documentation Health

`DocumentationCatalog.Check` is a pure deterministic checker. It receives current features, documentation entries, routable routes, anchors and explicit settings/tab/domain inventories. It reports duplicate IDs, undocumented current features, invalid or stale routes, missing anchors, orphan documentation, missing Settings/Target Environment/Source Analysis entries, missing provider/extension qualification and known legacy wording supplied by the caller. It does not use NLP, AI, network access or project source analysis.

Findings contain severity, category, feature ID, route, document section, message and recommended fix. The System Settings page groups them by category. A failed diagnostic means documentation metadata is out of sync; it is not a project-quality result.

## How to add a feature

1. Add the route and navigation row to `NavigationCatalog` with its visibility/applicability/provider metadata.
2. Add an explicit stable `FeatureId` entry to `DocumentationCatalog.Features`; do not derive a documentation entry automatically from a new menu row.
3. Add the referenced explicit anchor to the User Guide and render a useful matrix row.
4. Describe purpose, evidence inputs, outputs, applicability and limitations; qualify provider/extension behavior.
5. Add or update focused checker fixtures for any new metadata shape. Documentation Health must show no unexplained feature or route failure.

## Renaming a feature

Keep the stable `FeatureId`; update `NavigationCatalog` and documentation display text and route as needed. A display-label-only change should not create an identity mismatch. A route change must update the documentation entry and links; tests must prove old routes do not persist unless they are explicit aliases.

## Settings, target tabs and source domains

Update the live System Settings section inventory, `DocumentationCatalog.SettingsSections`, and the guide’s settings coverage together. Update the target-environment tab inventory and guide ownership description when a tab is added or renamed. Update Source Analysis domain inventory and its documented domain grouping whenever `AreaGroups` changes. Coverage checks should fail when a fixture removes one of these documentation entries.

## Provider and extension documentation

Set `ProviderScope` or `ExtensionScope` on both current feature metadata and its documentation entry. Describe the capability as optional/scoped, explain that availability/detection does not mean a project uses it, and link to Technology Coverage for implementation support. Never imply that a provider or project extension is a universal BirkNext requirement.

## Limits

Structured checks catch metadata and route drift, but cannot prove prose accuracy, evidence claims, accessibility or responsive behavior. Review content against current implementation and run UI/accessibility checks for visual changes.
