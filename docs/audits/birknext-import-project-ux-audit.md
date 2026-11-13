# Import Project — UX audit (2026-11-13)

Starting HEAD: `ec0ed607`. Page: `Pages/ProjectImport.razor`, with the import state in `ProjectImportSession` and the intent enum `ImportTarget`.

## Root cause
The upload form (`pi-choose`) is shown automatically **only when nothing is current**. After the first import it collapses, and the only way back was two small secondary buttons at the end of the long Current import card:
- *Import more evidence*: `ImportTarget.CurrentApplication`;
- *Import another application*: `ImportTarget.NewApplication`.

When opened, the form rendered **below** all the current-import details.

## Existing import actions (all kept)
| Action | What it does | Same mechanism? |
|---|---|---|
| Import more evidence | Opens `pi-choose` with target CurrentApplication | Yes, the one upload flow |
| Import another application | Opens `pi-choose` with target NewApplication | Yes |
| `?import=…` contextual "Import source" links | Open `pi-choose` (CurrentApplication) | Yes |
| Result page "Import more evidence" | A direct file chooser | Yes |
| Manage sources | Link to Source Management | No: inspect, not import |
| Clear current import | Only stops showing this package's documents; the application, packages, snapshots and IQR bindings are unchanged (stated on the page) | No; not changed |

**"Import another source version":** there is no separate operation in the import architecture. Another ZIP of the same source is an *Add evidence* import that creates a new snapshot beside the earlier one, and the analysis baseline is chosen in Source Management. No new operation was invented.
