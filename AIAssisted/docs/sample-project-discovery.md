# Sample Project document discovery

Sample Projects no longer assumes a fixed set of root-level files (`constitution.md`, `spec.md`, `data-model.md`,
`plan.md`, `tasks.md`). Any safe folder structure and any filenames work, and every artifact role is optional.

## Ownership

| Concern | Owner |
|---|---|
| Recursive, bounded file inventory; safe document reads | Backend `SampleProjectDocumentInventory` (`Services/SampleProjects/`), served by `SampleProjectsController` |
| Artifact-role classification | Frontend `SampleArtifactClassifier` (pure, deterministic) |
| Per-project discovery, cache, explicit document choice | Frontend `SampleProjectArtifactDiscoveryService` (`ISampleProjectArtifactDiscovery`, singleton) |
| Role → document for Explorers, Quality Review, traceability | `SampleProjectDocumentResolver` (by role, never by filename) |
| Selected project identity | `WorkspaceArtifactRepository.CurrentProject` (unchanged: identity only, no document copies) |

Discovery is separate from Source Analysis. It looks at documents only and never at source technology.

## API

- `GET api/sample-projects` returns each project with `Files`, the recursive inventory. Each entry has `relativePath`,
  `sizeBytes`, `lastModifiedUtc` and `skipReason`, and `isSupported` marks a readable Markdown candidate. It also returns
  `Discovery` stats: files scanned, ignored folders, links not followed and truncation.
- `GET api/sample-projects/{slug}/documents` returns the text of every readable candidate document, in path order.
- `GET api/sample-projects/{slug}/file?filename=<relative path>` returns one document. It serves only readable
  candidate documents that the project's own inventory lists. Traversal, absolute paths, links and unlisted files are
  refused.

## Inventory limits

- Depth: 16 levels.
- Files: 10,000 scanned and 2,000 listed. Documents are always listed.
- Documents: 500, each up to 1 MiB.
- A document that contains NUL bytes is treated as binary and is not read.
- Reparse points (symlinks and junctions) are never followed.

Ignored folders: `.git`, `.vs`, `.idea`, `.vscode`, `bin`, `obj`, `node_modules`, `dist`, `build`, `out`, `coverage`,
`TestResults`, `artifacts`, `packages`, `vendor`, `.terraform`, `target`, `__pycache__`, `.venv`, `venv`, `.next`,
`.nuxt`, `.angular`.

## Classification

The roles are the repository's `WorkspaceArtifactType` values: Constitution, Specification, DataModel, Plan, Tasks
and Research. The classifier applies these rules in order:

1. Front matter `type`/`artifact`/`artifactType`/`documentType`/`doctype`/`role`/`kind` with a known value gives a
   Confirmed role.
2. README, CHANGELOG and similar files are not artifact roles. A title or folder that marks a document as a checklist
   or a contract (`checklists/`, `contracts/`) also leaves it unclassified.
3. Structure is read with the shared `MarkdownTokenizer` (title and headings) and the domain extractors.
   `SpecExplorerService` counts requirement and scenario items, `TaskExplorerService` counts task IDs,
   `DataModelAnalysisService` counts entities with fields, `ConstitutionAnalysisService` counts principles, and
   `PlanAnalysisService` counts milestones, decisions and constitution checks.
4. An exact canonical filename (`constitution`, `spec`, `specification`, `data-model`, `plan`, `tasks`, `research`)
   adds a strong hint. Filename synonyms (`requirements`, `implementation-plan`, `domain-model`, `work-items` …) add a
   weak one. Folder names (`specs/`, `plans/` …) add +1 only when the content or the filename already suggests that
   role.

The result is one of four states:

- **Detected** (Confirmed or Strong). Confirmed means metadata, or a canonical filename with agreeing content. Strong
  means a canonical filename alone, or substantial content evidence.
- **Needs review** has three causes:
  - weak evidence only;
  - a canonical filename contradicted by strong content of another role;
  - two roles with substantial evidence, where the weaker has at least two thirds of the stronger's score.
- **Unclassified** means no reliable role. These documents are shown as *Other documents*.
- **Parse error** means the document could not be read. This is distinct from not found.

## Multiple documents and authority

All detected documents of a role are kept, and the same filename in different folders never collides. The role's
primary document is set as follows:

- When there is exactly one document, it is the primary.
- When there are several, the user chooses one on the project card with *Document the … Explorer opens*. Until then the
  resolver returns `RequiresSelection` and Explorers show the candidate list.
- Discovery never picks the first, shortest or root-level file, and it never marks anything Baseline or authoritative.

Duplicate content is flagged ("Same content as …") using the shared `ArtifactFingerprint`, the same SHA-256 that
repository revisions use.

## Caching

Discovery is cached per project. The cache key is a fingerprint of the inventory (path, size, modification time), so a
changed, added or removed file triggers a re-read. The Sample Projects page re-reads the catalog every time it opens.
The explicit document choice lasts for the session only.

## Known limitations

- The explicit choice among several documents is not persisted across reloads.
- Quality Review, Artifact Traceability and Task-to-Spec Alignment still label their input cards with canonical names
  (`spec.md` …). Their content comes from the role-based resolver.
