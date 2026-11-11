# M2LB source packages — independent inventory (tasks 14/15)

This is the acceptance oracle for the multi-branch work. It was built directly from the archives with `scratchpad/mb/tools/inventory.py` and `oracle.py`, which read the entries in memory: nothing was extracted, executed or modified. BirkNext's own analysis is not used here.

## Input location

The brief names `C:\Downloads\M2LB (3..7).zip`. **`C:\Downloads` does not exist on this machine.** Files with exactly these names exist in `C:\Users\ajaan\Downloads\`, and those were used read-only. This is a documented path resolution, not a silent substitution; no other package was swapped in.

The same folder also holds `M2LB.zip`, `M2LB Skole.zip`, `M2LB -main.zip`, `M2LB-language.zip`, `M2LB (1).zip`, `M2LB (2).zip` and `M2LB.Common.zip`. Two of them were inventoried only to test content identity (see below).

## Archive identity and safety

| Package | Bytes | Archive SHA-256 | Canonical content fingerprint | Files | Uncompressed | Max ratio | .csproj | XSD | Test projects |
|---|---|---|---|---|---|---|---|---|---|
| M2LB (3).zip | 8,248,351 | `BC780B6B6B5DCBF8043C686B23395E4394F9D68D2DD9E8F47469AD13C6CF1FAD` | `B3D2547BA123FE921AAF5AD8355632FEDD051F7AB0205C82A3BE3502338A8963` | 2,361 | 19,473,560 | 32.0 | 68 | 6 | 29 |
| M2LB (4).zip | 4,446,106 | `049031372EACDE844F2B80A173C6768AFD8B25EC101FCD417B85F47ADA230506` | `E2A0D4C05EB578F7197CD082AE435FF211745D8255F17C1E08E379D40CE77909` | 1,839 | 12,371,134 | 11.9 | 51 | 0 | 23 |
| M2LB (5).zip | 4,823,294 | `0AF7DCF7F1EB40B2EE30ED338799F57D417D561987C3171D86BB1B1645EDC2E9` | `660DD268F19F21DE55D5917D2A6C43380AC6CDD9958FED44D05E838204E0C152` | 2,098 | 13,318,659 | 28.1 | 63 | 5 | 27 |
| M2LB (6).zip | 4,626,491 | `A306EE4733EC3BC9BC344F5AAAD119D703D3173293651D90C22F66C01C0494CF` | `737A4F22810064BD48881C1A35A22B17E9C930CE4FD10EEECE3A6B550B37050C` | 1,975 | 12,838,607 | 11.9 | 56 | 0 | 25 |
| M2LB (7).zip | 10,731,823 | `C7BB9F125826846355AA0DA5459B7BCF262C043D598712D2CB1E6BE476F654D5` | `CA5992E5DEC390934E14D2DE51024933555D5B1364D0395B2B4951C215D70746` | 3,003 | 25,321,332 | 32.0 | 70 | 6 | 31 |
| *M2LB.zip* (reference) | 8,248,351 | `22339207CD2EF2F75EA418F66037C2CCACD8DF7AAD3CD89DDF73BD2A439C28F0` | `B3D2547B…338A8963` (**same content as (3)**) | 2,361 | 19,473,560 | 32.0 | 68 | 6 | 29 |
| *M2LB Skole.zip* (reference) | 4,446,106 | `7516B48D6BF77426FEEF67BCA89D68070486896A0430B30C41085F103149DCD8` | `E2A0D4C0…D40CE77909` (**same content as (4)**) | 1,839 | 12,371,134 | 11.9 | 51 | 0 | 23 |
| *M2LB.Common.zip* (shared library) | 94,609 | `6015C521…149DCD8`¹ | `93958DAD…190150B8C` | 67 | 234,174 | 5.6 | 2 | 0 | 1 |

¹ The full hash is in `scratchpad/mb/out/inventory.json`.

**Canonical content fingerprint (rule used by this oracle):**
- Regular files only; paths use `/`.
- A single top-level directory is removed when every entry shares it. These archives have none: their files sit at the root.
- Each file is hashed with SHA-256 of its raw bytes, and the sorted `path\0sha256\n` lines are hashed again.
- Entry order, timestamps, compression and the archive name are ignored. Line endings are **not** normalised.

**Real duplicate-content cases:**
- `M2LB (3).zip` and `M2LB.zip` have different archive hashes but identical content.
- `M2LB (4).zip` and `M2LB Skole.zip` have different archive hashes but identical content.

**Safety, for all eight archives:**
- The central directory reads cleanly and every CRC check passes.
- 0 entries with path traversal (`..`), 0 absolute paths, 0 duplicate Unicode-normalised paths and 0 case-insensitive collisions.
- 0 symlinks and 0 nested archives.
- The maximum compression ratio is 32:1 (text files), well under bomb thresholds.

**Git metadata:** none. No `.git/HEAD` or `.git` directory is in any archive.
- **Verified branch is UNKNOWN for all five; verified commit is UNKNOWN for all five.**
- The numbers (3)…(7) are browser download suffixes, not branch names and not an ordering.

## Components (C# files per top-level directory)

| Directory | (3) | (4) | (5) | (6) | (7) |
|---|---|---|---|---|---|
| Autorisasjon / AutorisasjonService | 239 | 238 | 238 | 238 | 310 (renamed `AutorisasjonService`) |
| CdcReplay | 11 | 9 | 9 | 9 | 15 |
| Frontend | 146 | 146 | 146 | 146 | 178 |
| Hendelse / HendelseService | 70 | 70 | 70 | 70 | 112 (renamed) |
| HendelseAdapter | 75 | 75 | 75 | 75 | 128 |
| **MeldingsutvekslerReceiver** (MM, English) | 113 | — | — | — | 114 |
| **Meldingsutvekslermottak** (MM, Norwegian) | — | — | 96 | — | — |
| Person / PersonService | 137 | 137 | 137 | 137 | 145 (renamed) |
| PersonAdapter | 84 | 84 | 84 | 84 | 90 |
| Proxy | 28 | 27 | 27 | 27 | 26 |
| Revisjon / RevisjonService | 17 | 17 | 17 | 17 | 22 (renamed) |
| **Skole** (Norwegian school service) | — | — | 121 | 121 | — |
| **SkoleAdapter** | 83 | — | — | — | 85 |
| **SkoleService** (English) | 198 | — | — | — | 212 |
| Tjeneste / TjenesteService | 94 | 94 | 94 | 94 | 127 (renamed) |

In (4) and (6), the `Meldingsutvekslermottak` and `SkoleAdapter` directories hold only non-C# files (13 and 11 paths respectively). There is **no implementation** of them there.

## Contracts and integration evidence

| Evidence | (3) | (4) | (5) | (6) | (7) |
|---|---|---|---|---|---|
| Skolenærvær XSD | v2.0.1.flat (`https://bufdir.no/barnevern.tiltak.skolenaervaer.v2.0.1.flat`) + m2lb variant | none | **v2.0.0.flat** + m2lb variant | none | v2.0.1.flat + m2lb variant |
| Receipt XSDs (lib v1.1.0, applikasjonskvittering v2, melding.lib v2) | yes (`…/Receipt/Skjema`) | — | yes (`…/Kvittering/Skjema`) | — | yes |
| Report route terms in C# | `/reports` (6 files), `outcome` (76) | `outcome` (4), `utfall` (3) | **`/rapporter` (9), `utfall` (35)** | `/rapporter` (7), `utfall` (16) | `/reports` (6), `outcome` (101) |
| Education routes | `/utdanningsforhold/{recordId}`, `/manglende-skoletilbud/{recordId}`, `/code-lists`, `/record-ids` | — | `/utdanningsforhold/{postId}`, `/manglende-skoletilbud/{postId}`, `/kodelister`, `/postidentiteter` | same as (5) | same as (3); reference data `reference-data/…` (English) |
| Parquet / Bufdata | SkoleService (6 paths, 20 code refs) | — | 3 code refs, no implementation directory | 3 code refs | SkoleService (6 paths, 22 refs) |
| Period lock | SkoleService (6 paths, 28 refs) | — | — | — | SkoleService (6 paths, 28 refs) |
| Service Bus / Event Hub code refs | 94 / 52 | 74 / 25 | 88 / 25 | 77 / 25 | 134 / 57 |
| Altinn references (code) | 23 | 0 | 15 | 2 | 23 |
| Specs (`specs/`, `.specify/`) | 14 md | 14 | 14 | 14 | 75 (adds `specs/`) |
| Pipelines | `.pipeline/*.yml` (identical set in all five) | | | | |
| Terraform | 0 in all five (no infrastructure-as-code in these packages) | | | | |

## Pairwise differences (content hashes)

| From → to | Added | Removed | Changed | Same |
|---|---|---|---|---|
| (3) → (4) | 33 | 555 | 109 | 1,697 |
| (3) → (5) | 292 | 555 | 109 | 1,697 |
| (3) → (6) | 169 | 555 | 109 | 1,697 |
| (3) → (7) | 1,787 | 1,145 | 364 | 852 |
| (4) → (5) | 259 | 0 | 9 | 1,830 |
| (4) → (6) | 136 | 0 | 11 | 1,828 |
| (4) → (7) | 2,341 | 1,177 | 271 | 391 |
| (5) → (6) | 0 | 123 | 10 | 1,965 |
| (5) → (7) | 2,341 | 1,436 | 271 | 391 |
| (6) → (7) | 2,341 | 1,313 | 271 | 391 |

**Reading:**
- (4) is a common base: (5) and (6) only add to it.
- (5) and (6) are a Norwegian-language generation: Meldingsutvekslermottak, `Skole`, v2.0.0, `/rapporter`, `utfall`.
- (3) and (7) are an English-language generation: MeldingsutvekslerReceiver, SkoleService, v2.0.1, `/reports`, `outcome`. (7) also renames the services to `*Service`.

This describes content only. **No chronology, branch name or deployment is inferred.**
