# Contract Evolution

Contract Evolution is a general BirkNext capability. It answers one question for any contract BirkNext can read:

> This contract changed. What changed, who is affected, and which boundary is the first one known to break?

It detects and explains change. It never maps renamed fields automatically, never decides what a business value means, and never
generates payloads.

## Concepts

| Concept | What it is | Where |
|---|---|---|
| Contract snapshot | A contract normalized into nodes (path, kind, type, requiredness, occurrence, enum values, namespace, sibling order) at one moment | `shared/ContractEvolutionContracts.cs` (`ContractSnapshot`, `ContractNode`, `ContractDescriptor`) |
| Normalization | Turns an existing parser's output into nodes. No format is parsed twice | `Services/ContractEvolution/ContractSnapshots.cs` |
| Fingerprint | SHA-256 (lowercase hex) over the canonical normalized form: type, declared version, namespace, nodes, normalizer version. Formatting, comments and line endings do not change it | `ContractSnapshots.Fingerprint` |
| Diff | Structural changes by stable path, using the shared `ContractChangeClass` taxonomy | `ContractDiff` |
| Structural compatibility | Per change and per direction (producer writes / consumer reads), conservative | `ContractCompatibilityRules` |
| Semantic overlay | A domain's business rules on top (`IContractSemanticRules`); without a rule a change is *Not evaluated* | domain profiles |
| Dependency graph | Data: boundaries where a component produces, transports, consumes or maps a contract | `ContractDependencyGraph`, `ContractBoundary` |
| Impact traversal | Walks the graph from the changed contract; follows a mapping only for the paths it maps | `ContractImpactAnalyzer` |
| First incompatible boundary | The first boundary, in traversal order, that is known (not potentially) to be Breaking | `ContractImpactResult.FirstIncompatibleBoundary` |

### Supported contract types

| Type | Normalizer | Reused parser |
|---|---|---|
| XML Schema (XSD) | `FromXmlSchemaPackage` / `FromXmlSchemaText` | The shared XSD capability (`shared/XmlSchemaPackages.cs`, also used by Schema Studio and Test Data): effective schema with inheritance, cross-file types and restrictions; DTDs prohibited, imports/includes resolve only inside the package, never fetched. Each node keeps its declaring file; the snapshot lists every schema file with namespace, version and fingerprint |
| JSON Schema-like field lists | `FromFields` | Any flattened field list (for example an Altinn form schema) |
| OpenAPI | `FromOpenApi` | The existing OpenAPI extractor's normalized contract (operations, parameters, request fields) |

Adding a contract type means adding one normalizer. Diff, compatibility, graph and traversal stay the same.

### Compatibility values

`Compatible` < `NotEvaluated` < `NeedsReview` < `Breaking`. The overall result is always the worse value, and *Not evaluated* is
never read as compatible. Structural and semantic compatibility are kept apart. For example, an optional new element is structurally
Compatible but semantically *Needs review*, so it needs review overall.

| Change | Producer (writes it) | Consumer (reads it) |
|---|---|---|
| Optional member added | Compatible | Compatible if the consumer ignores unknown members, Breaking if it rejects them, Needs review if unknown |
| Required member added | Compatible if already written, otherwise Breaking | As for an optional member |
| Member removed | Breaking if written | Breaking if read |
| Type changed | Breaking if written | Breaking if read |
| Optional → required | Breaking if not written | Compatible |
| Required → optional | Compatible | Needs review if read |
| Occurrence changed | Needs review if written | Needs review if read |
| Enum value added | Compatible | Needs review if read |
| Enum value removed | Needs review if written | Compatible |
| Namespace changed | Needs review | Needs review: same names in another namespace are not assumed equivalent |
| Member order changed (XSD sequence) | Needs review if written | Breaking for a strict validator, otherwise Needs review |
| Declared version changed | Needs review | Needs review where the consumer declares supported versions |
| Format/restriction changed (pattern, length, bounds, digits) | Needs review if written | Needs review if read |
| Schema dependency changed (an imported/included file) | Needs review | Needs review; the changed members carry the file as provenance |

Other rules:
- A party whose paths are unknown gets *Needs review*, never Compatible.
- A path that moved is reported as a removal plus an addition, never as a guessed rename.

### Boundaries and propagation

The four roles are `Produces`, `Transports`, `Consumes` and `Maps`.
- **A transport** is Compatible: it carries the message without reading its structure.
- **A mapping with unknown fields** is *Not evaluated*, and so is everything behind it, marked *potential*.
- **A known mapping** passes on only the changes to paths it maps. An unmapped optional addition does not reach downstream consumers. An unmapped required addition is *Needs review*: its data does not reach the output.
- **A declared pass-through mapping** (`PassThrough`, never inferred) carries each mapped change onto its target paths, so downstream consumers are evaluated on it.
- **Without a pass-through**, downstream impact stays *potential*, never a confirmed failure.

### Integration in readiness

A domain consumes the result through its existing readiness. No second readiness engine exists.
- **Structurally compatible ≠ ready for execution.** Authentication, target, identities, enrolment and runtime gates are separate.
- **Compatible ≠ runtime observed.**

## Skolenærvær: the first end-to-end use

`Services/IntegrationJourneys/Packs/SkoleNaervaer/SkoleNaervaerContractProfile.cs` holds only domain knowledge:

- **Contract graph (data):**
  1. BirkNext → Altinn (producer; its paths are exactly the form binding, `SkoleNaervaerAltinnForm`)
  2. Altinn → MU (form to MU message; mapping *unknown*)
  3. MU → MM (transport: encrypted Blob reference)
  4. MM input (XSD v2.0.0 flat, strict, version 2.0.0)
  5. MM → Skoletjenesten (mapping verified in a source review but *not imported*)
  6. Skoletjenesten intake (strict JSON, schema version 2.0.0)
  7. Skoletjenesten → Bufdata (no writer yet, ADR-028)
  8. Bufdata (not verified)

  Every boundary cites its evidence (`docs/skolenaervaer-cross-repo-verification.md`). MM input and MM → Skole mapping are separate boundaries. Skole and Bufdata are linked to the Altinn form only through mappings, never directly.
- **Semantic rules (`SkoleNaervaerContractSemantics`):** these are explicit path rules only.
  - An optional `Meldingshode` member is technical (Compatible). Other header changes need review (MM correlates on `Meldingshode/Id`).
  - `Klient/Identifikator` changes are Breaking; new options there need review.
  - `Periode` and the hours fields need review.
  - A new code-list value needs review and is never mapped to an existing value.
  - Any other new field needs review, because no rule says it is technical.
- **Live discovery:** the live public TT02 metadata and the confirmed baseline are converted by the Altinn-generic adapter (`AltinnTesting/AltinnContractEvolution.cs`). No Skolenærvær-specific snapshot format exists.
- **Readiness:** the Rapportmottak row *Contract change impact* (`contract-impact`), plus a dry-run check.
  - It blocks a real run only when the form changed since the confirmed contract and BirkNext's producer boundary is not Compatible. That covers a required field BirkNext does not fill and any change that needs business review.
  - BirkNext never generates values for unknown business fields.
  - The run page's decision lists it under E (payload / contract).
- **No silent acceptance:** the Altinn contract comparer used to auto-accept form changes other than field names and types (requiredness, formats, XML namespace or root). It now consults the generic form contract, and such changes need review.

What a normal future Skolenærvær form change needs: no generic engine change. Only the profile's rules and graph data change where
the business impact requires it, and the payload mapping changes when BirkNext must write a new field.

## Known limits

- **Not imported as data:** the Altinn → MU transformation, MM's read paths, the MM → Skole field mapping and Skole's JSON fields. Their boundaries are *Not evaluated*, never Compatible.
- **The two flat XSDs** (`skolenaervaer.v2.0.0.flat.xsd` and `.flat.m2lb.xsd`) are not in this repository, and nothing references them. Their roles cannot be proven from BirkNext's evidence.
  The Altinn form schemas v0.8.2 and v2.0.0 (`AIAssisted/xsd`) are test fixtures: their comparison exercises namespace, version, enumeration, requiredness and root-type changes.
- **XSD fingerprints** use normalizer version 2 (`ContractSnapshots.XmlSchemaNormalizerVersion`); an XSD fingerprint from version 1 never equals a version 2 one. JSON and OpenAPI fingerprints are unchanged.
- **Not yet built:**
  - Impact Analysis and Quality Review do not consume contract-change results.
  - Snapshots are not kept as a separate history; the Altinn form's confirmed baseline is stored with the IQR configuration as before.
  - There is no dedicated diff view; the readiness row carries the summary.
