using System.Text;

namespace BirkNext.Web.Tests;

/// <summary>
/// Builds small, deterministic project artifacts in a test-owned temporary workspace.
/// These are parser fixtures, not checked-in demo projects.
/// </summary>
internal static class TestDataHelper
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "BirkNext.Tests", "ProjectFixtures-v11");

    public static string ResolveFixturePath(params string[] pathSegments)
    {
        var normalized = pathSegments.Where(p => p != ".." && p != ".").ToArray();
        var slug = normalized.FirstOrDefault(p => !p.Contains('.') && !p.Equals("specs", StringComparison.OrdinalIgnoreCase)
            && !p.Equals("008-traceability-first", StringComparison.OrdinalIgnoreCase)) ?? "autorisasjon";
        var file = normalized.LastOrDefault(p => p.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) ?? "spec.md";
        var specialPlan = file.Equals("plan.md", StringComparison.OrdinalIgnoreCase)
            && pathSegments.Any(p => p.Contains("008-traceability-first", StringComparison.OrdinalIgnoreCase));
        var dir = Path.Combine(Root, specialPlan ? "traceability" : slug);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, file);
        if (!File.Exists(path)) File.WriteAllText(path, specialPlan ? LetteredPlan("Group", 7, 69) : Fixture(slug, file), new UTF8Encoding(false));
        return path;
    }

    private static string Fixture(string slug, string file) => file.ToLowerInvariant() switch
    {
        "tasks.md" => Tasks(slug),
        "plan.md" => slug == "person-module" ? LetteredPlan("Phase", 6, 36) : Plan(slug),
        "constitution.md" => Constitution(slug),
        "data-model.md" => DataModel(slug),
        _ => Specification(slug),
    };

    private static string Tasks(string slug)
    {
        var sb = new StringBuilder("# Tasks: ").AppendLine(slug).AppendLine();
        var ids = Enumerable.Range(1, 32).Select(i => $"T{i:000}")
            .Concat(["T033", "T033a", "T034", "T035", "T036", "T037"]).ToArray();
        var phaseRanges = new[] { (0, 2), (2, 17), (17, 23), (23, 27), (27, 31), (31, 36), (36, 38) };
        for (var phase = 1; phase <= phaseRanges.Length; phase++)
        {
            var label = phase switch { 1 => "Setup", 2 => "Foundational", 3 => "User Story 1 — User Activated", 4 => "User Story 2 — User Deactivated", 5 => "User Story 3 — Full Synchronization", 6 => "User Story 4 — Operations Monitoring", _ => "Polish" };
            sb.AppendLine($"## Phase {phase}: {label}")
              .AppendLine($"Purpose: {(phase == 1 ? "Prepare two new projects for delivery." : $"Deliver the scoped capability for phase {phase}.")}")
              .AppendLine($"Goal: Complete and verify phase {phase}.")
              .AppendLine("Independent Test: Run the phase checks.")
              .AppendLine(phase == 1 ? "" : "Checkpoint: Review the phase result.").AppendLine();
            if (phase is >= 3 and <= 6)
            {
                var story = phase - 2;
                var storyName = story switch { 1 => "User Activated", 2 => "User Deactivated", 3 => "Full Synchronization", _ => "Operations Monitoring" };
                sb.AppendLine($"### User Story {story} — {storyName}").AppendLine();
                for (var sub = 1; sub <= 2; sub++) sb.AppendLine($"#### User Story {story}.{sub}: Supporting scenario");
                sb.AppendLine();
            }
            if (phase == 7)
                for (var story = 5; story <= 11; story++) sb.AppendLine($"### User Story {story}: Supporting fixture story");
            var (start, end) = phaseRanges[phase - 1];
            for (var i = start; i < end; i++)
            {
                var id = ids[i];
                var parallel = i < 14 || id is "T018" or "T024" or "T028" or "T032";
                var us = phase is >= 3 and <= 6 ? $"[US{phase - 2}] " : "";
                var title = id == "T001" ? "Create project scaffold .csproj" : id == "T015" ? "Implement [US1] synchronization behavior" : $"Implement capability {id}";
                sb.AppendLine($"- [x] {(parallel ? "[P] " : "")}{id} {us}{title}");
            }
            sb.AppendLine();
        }
        sb.AppendLine().AppendLine("## Summary").AppendLine()
          .AppendLine("| Entity | Tasks |").AppendLine("|---|---|")
          .AppendLine("| Phase 2: Foundational | T003 |")
          .AppendLine().AppendLine("## Dependencies & Execution Order").AppendLine()
          .AppendLine("### User Story Internal Dependencies").AppendLine()
          .AppendLine("- **US1**: T018/T019 [P] -> T020 -> T021 -> T022 -> T023")
          .AppendLine("- **US2**: T024 -> T025 -> T026 -> T027")
          .AppendLine("- **US3**: T028 -> T029 -> T030 -> T031")
          .AppendLine("- **US4**: T032/T033/T033a [P] -> T034 -> T035");
        return sb.ToString();
    }

    private static string Plan(string slug) => $$"""
        # Feature Plan: {{slug}}

        **Branch**: `004-scim-user-sync` | **Date**: 2026-04-23 | **Spec**: [spec.md](spec.md)
        **Input**: Feature specification from `/specs/004-scim-user-sync/spec.md`
        **Author**: Test Fixture

        ## Summary
        Deliver the project capability using small, verifiable increments.

        ## Technical Context
        **Language/Version**: C# 13 / .NET 10
        **Primary Dependencies**: ASP.NET Core Minimal API, EF Core 10, Azure.Messaging.ServiceBus, Polly 8.x, Azure.Identity
        **Storage**: SQL Server 2022
        **Testing**: xUnit 2.9.3, Shouldly 4.3.0, NSubstitute 5.x, Testcontainers 4.x
        **Target Platform**: .NET 10
        **Project Type**: web application
        **Performance Goals**: No specific SLA; Entra provisioning engine has a 30-second timeout per request
        **Constraints**: Service Bus publish must complete synchronously before HTTP 2xx (FR-008); Key Vault unreachable at startup means fail fast (FR-022)
        **Scale/Scope**: Single replica; up to 500 users (SC-004); no optimistic concurrency needed

        ## Constitution Check

        | Rule ID | Requirement | Status | Notes |
        |-----------|-------------|--------|-------|
        | PP-01 | No hardcoded secrets | PASS | SCIM endpoint is the inbound contract |
        | PP-02 | Maintainable design | PASS | Bearer token; Key Vault |
        | GL-18 | Temporal fields justified | JUSTIFIED DEVIATION | sync-state |
        | GL-20 | Outbox justified | JUSTIFIED DEVIATION | scope |
        | PS-01 | Security review | JUSTIFIED DEVIATION | review |
        | PS-02 | Test coverage | PASS | checked |
        | PS-03 | Observability | PASS | checked |
        | PS-04 | Privacy | PASS | checked |
        | PS-05 | Reliability | PASS | checked |
        | PS-06 | Documentation | PASS | checked |
        | PS-07 | Compatibility | PASS | checked |
        | PS-08 | Recovery | PASS | checked |
        | PS-09 | Operations | PASS | checked |

        ## Complexity Tracking

        | Violation | Why Needed | Simpler Alternative Rejected Because |
        |-----------|-----------|-------------------------------------|
        | GL-18: KjentBruker without GyldigFra/GyldigTil | Record is sync-state, not entity | Adding audit fields would complicate machine-driven sync |
        | GL-20: No transactional outbox | No outbox table for synchronous flow | An outbox requires polling infrastructure |
        | PS-01: Non-EntraID auth on SCIM endpoint | Needed during provisioning | Entra-only auth blocks provisioning |

        ## Project Structure
        ### Documentation (this feature)
        specs/004-scim-user-sync/ contains the specification, plan, and tasks.

        ### Source Code Changes
        ```text
        src/
          Api/
          Core/
        tests/
          UnitTests/
        ```

        ## Implementation Phases
        ### Phase 1: Setup
        {{string.Join("\n", Enumerable.Range(1, 5).Select(i => $"- T{i:000} [P] Setup task {i}"))}}

        ### Phase 2: Foundational
        ---
        ```xml
        <Project Sdk="Microsoft.NET.Sdk"></Project>
        ```

        ### Phase 3: Custom handler
        Implement the Custom SCIM handler and validate its behavior.

        ### Phase 4: User synchronization
        {{string.Join("\n", Enumerable.Range(6, 4).Select(i => $"- T{i:000} [P] Synchronization task {i}"))}}

        ### Phase 5: SCIM compatibility
        Preserve SCIM compatibility and document the expected behavior.

        ### Phase 6: API endpoints
        Add the required endpoints and verify request handling.

        ### Phase 7: Application bootstrap
        Setup sequence for the Program.cs application bootstrap.

        ### Phase 8: Unit Test Strategy
        - T010 Implement unit test suite
        - T011 Verify unit test suite

        ### Phase 9: Integration Test Strategy
        {{string.Join("\n", Enumerable.Range(12, 12).Select(i => $"- T{i:000} Final task {i}"))}}
        """;

    private static string LetteredPlan(string prefix, int groups, int taskCount)
    {
        var sections = Enumerable.Range(0, groups).Select(i =>
        {
            var letter = (char)("A"[0] + i);
            var tasks = Enumerable.Range(1, taskCount).Where(n => (n - 1) % groups == i)
                .Select(n => $"- T{n:000} Implement group {letter}");
            return $"### {prefix} {letter} - Group {letter}\n{string.Join("\n", tasks)}";
        });
        return $"# Lettered Plan\n\n## Implementation Phases\n\n{string.Join("\n\n", sections)}";
    }
    private static string Constitution(string slug) => $$"""
        # {{slug}} Constitution
        ## Core Principles
        ### PP-01: Security First
        Secrets are never committed.
        ### PP-02: Testable Design
        Changes include deterministic tests.
        ## Governance
        ### GOV-001: Governance
        Decisions link to their governing principle. This rule references PP-01, PP-02, and PP-03.
        ### GOV-002: Traceability
        Requirements and tasks retain stable identifiers.
        ### GOV-003: Review
        Material changes receive review.
        ### GOV-004: Operations
        Operational behavior is documented.
        ### GOV-005: Privacy
        Personal data is handled deliberately.
        ### GOV-006: Reliability
        Failures are observable and recoverable.
        ### GOV-007: Compatibility
        Contracts are versioned.
        ### GOV-008: Maintainability
        Modules have clear responsibilities.
        ## Governance Rules
        ## Platform Standards
        ### PS-01: Source Code Language
        Source code uses consistent language and naming.

        | Character | Replacement |
        |---|---|
        | `æ` | `ae` |
        | `ø` | `oe` |
        | `å` | `aa` |
        """;

    private static string DataModel(string slug)
    {
        if (slug == "person-module")
        {
            var names = new[] { "IX_Person_EksternId", "IX_Person_Foedselsnummer", "IX_Person_DUFNummer", "IX_Person_Status",
                "IX_BarnIAndrelinjeBarnevern_PersonId", "IX_BarnIAndrelinjeBarnevern_BirkId", "IX_Barn_Search", "IX_Barn_Status",
                "IX_Barn_Kilde", "IX_Barn_Opprettet", "IX_BarnStatusHistorikk_PersonId", "IX_OutboxMessage_Status_CreatedAt" };
            var entities = new[] { "Person", "Person", "Person", "Person", "BarnIAndrelinjeBarnevern", "BarnIAndrelinjeBarnevern",
                "BarnIAndrelinjeBarnevern", "BarnIAndrelinjeBarnevern", "BarnIAndrelinjeBarnevern", "BarnIAndrelinjeBarnevern",
                "BarnStatusHistorikk", "OutboxMessage" };
            var sb = new StringBuilder("# Person Module Data Model\n\n");
            foreach (var entity in entities.Distinct())
            {
                sb.AppendLine($"## Table: {entity}").AppendLine("| Id | UUID | Primary key |");
                var indexes = Enumerable.Range(0, names.Length).Where(i => entities[i] == entity).ToArray();
                if (indexes.Length > 0)
                {
                    sb.AppendLine().AppendLine("**Indexes**:");
                    foreach (var i in indexes)
                        sb.AppendLine($"- `{names[i]}` on ({(i == 11 ? "Status, CreatedAt" : i == 5 ? "BirkId" : "PersonId")}){(i is 4 or 5 ? " (unique)" : "")}");
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }
        if (slug == "person-adapter") return """
            # Person Adapter Data Model

            ## Table: FaultQueueEntry
            | Id | UUID | Primary key |

            ## Persistent Entities
            ### StreamCheckpoint
            | Id | UUID | Primary key |

            ## Events
            ### CDC Event
            | PersonId | UUID | Event key |

            ## SCIM Request/Response Models
            ### PersonRecord
            | Id | UUID | Record key |

            ## Table: DeliveryAttempt
            | Id | UUID | Primary key |

            ## Entities
            ### AdapterState
            | Id | UUID | Primary key |
            """;
        if (slug == "hendelsestjenesten") return """
            # Event Service Data Model

            ## Table: Event
            | Id | UUID | Key |

            **Indexes**: `BirkHendelsesId`; `BarnId`; `BirkTiltakPK` + `BarnId IS NULL`.
            """;
        if (slug is "autorisasjon" or "hendelse-adapter" or "revisjon" or "frontend-admin-panel" or "proxy" or "tjeneste") return $$"""
            # {{slug}} Data Model

            ## Table: Item

            | Field | Type | Constraints |
            |---|---|---|
            | ItemId | UUID | Primary key |
            """;
        return $$"""
            # {{slug}} Data Model
            ## Entities
            ### Project
            | Field | Type | Constraints |
            |---|---|---|
            | Id | UUID | PK |
            | DisplayLabel | string | required |
            """;
    }

    private static string Specification(string slug) => $$"""
        # {{slug}} Specification
        ## User Stories
        ### User Story 1: Manage projects
        As a user, I want to manage projects so that work is traceable.
        #### Acceptance Criteria
        - **Given** a project exists **When** it is opened **Then** its artifacts are shown.
        ### User Story 2: Validate input
        As a user, I want clear validation.
        #### Acceptance Criteria
        - **Given** invalid input **When** submitted **Then** an actionable error is shown.
        ## Requirements
        - FR-001: The system shall retain stable requirement identifiers.
        - FR-002: The system shall validate imported artifacts.
        ## Testing
        Unit and integration tests verify the acceptance criteria.
        """;
}

