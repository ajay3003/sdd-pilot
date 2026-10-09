using BirkNext.Web.Services;
using BirkNext.Web.Services.SampleProjects;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>Deterministic artifact-role classification: metadata, canonical names, structure, ambiguity.</summary>
public sealed class SampleArtifactClassifierTests
{
    internal const string Spec = """
        # Feature Specification: School attendance

        ## User Scenarios & Testing

        ### User Story 1 - Report attendance (Priority: P1)

        **Acceptance Scenarios**:

        1. **Given** a valid report, **When** it is received, **Then** it is stored.

        ## Requirements

        ### Functional Requirements

        - **FR-001**: System MUST accept attendance reports.
        - **FR-002**: System MUST reject invalid reports.
        - **FR-003**: System MUST log every decision.

        ## Success Criteria

        - **SC-001**: 99% of valid reports are stored within 1 s.
        """;

    internal const string Plan = """
        # Implementation Plan: School attendance

        ## Summary

        Deliver the attendance intake.

        ## Technical Context

        .NET 8, PostgreSQL.

        ## Project Structure

        src/ and tests/.

        ## Milestones

        - M1: intake endpoint
        - M2: storage
        """;

    internal const string Tasks = """
        # Tasks: School attendance

        ## Phase 1: Setup

        - [ ] T001 Create project structure
        - [ ] T002 [P] Configure linting
        - [ ] T003 Add CI pipeline

        ## Phase 2: Core

        - [ ] T004 Implement intake endpoint
        - [x] T005 Persist reports
        """;

    internal const string DataModel = """
        # Data Model: School attendance

        ## Entities

        ### AttendanceReport

        | Field | Type | Description |
        |-------|------|-------------|
        | Id | uuid | Primary key |
        | StudentId | uuid | Student reference |
        | Date | date | Day of attendance |

        ### Student

        | Field | Type | Description |
        |-------|------|-------------|
        | Id | uuid | Primary key |
        | Name | text | Full name |

        ## Relationships

        - AttendanceReport → Student (many-to-one)
        """;

    internal const string Constitution = """
        # Attendance Service Constitution

        ## Core Principles

        ### I. Privacy by default (NON-NEGOTIABLE)
        Personal data is minimised.

        ### II. Test first
        Every change starts with a failing test.

        ### III. Observability
        Every request is traceable.

        ## Governance

        Amendments require review.
        """;

    internal const string Research = """
        # Research: School attendance

        ## Decision 1: Storage

        **Decision**: PostgreSQL.
        **Rationale**: Existing platform standard.
        **Alternatives considered**: Cosmos DB — rejected.
        """;

    [Theory]
    [InlineData("constitution.md", Constitution, WorkspaceArtifactType.Constitution)]
    [InlineData("spec.md", Spec, WorkspaceArtifactType.Specification)]
    [InlineData("data-model.md", DataModel, WorkspaceArtifactType.DataModel)]
    [InlineData("plan.md", Plan, WorkspaceArtifactType.Plan)]
    [InlineData("tasks.md", Tasks, WorkspaceArtifactType.Tasks)]
    [InlineData("research.md", Research, WorkspaceArtifactType.Research)]
    public void CanonicalFilenameWithAgreeingContent_IsConfirmed(string path, string content, WorkspaceArtifactType role)
    {
        var result = SampleArtifactClassifier.Classify(path, content);

        result.Status.Should().Be(ArtifactDiscoveryStatus.Detected);
        result.Role.Should().Be(role);
        result.Confidence.Should().Be(ArtifactConfidence.Confirmed);
        result.Reasons.Should().Contain(r => r.StartsWith("Exact canonical filename"));
    }

    [Theory]
    [InlineData("plan", Plan, WorkspaceArtifactType.Plan)]
    [InlineData("constitution", Constitution, WorkspaceArtifactType.Constitution)]
    [InlineData("spec", Spec, WorkspaceArtifactType.Specification)]
    [InlineData("data-model", DataModel, WorkspaceArtifactType.DataModel)]
    [InlineData("tasks", Tasks, WorkspaceArtifactType.Tasks)]
    [InlineData("research", Research, WorkspaceArtifactType.Research)]
    public void StrongContentClassificationSurvivesCanonicalFilenameRemoval(string canonicalStem, string content, WorkspaceArtifactType role)
    {
        var canonical = SampleArtifactClassifier.Classify($"{canonicalStem}.md", content);
        var relocated = SampleArtifactClassifier.Classify("arbitrary/nested/renamed-document.md", content);

        canonical.Status.Should().Be(ArtifactDiscoveryStatus.Detected);
        relocated.Status.Should().Be(canonical.Status, string.Join("; ", relocated.Reasons));
        canonical.Role.Should().Be(role);
        relocated.Role.Should().Be(role);
        relocated.Confidence.Should().Be(ArtifactConfidence.Strong);
        relocated.Reasons.Should().NotContain(r => r.StartsWith("Exact canonical filename"));
        relocated.Candidates.Select(c => c.Role).Should().Equal(canonical.Candidates.Select(c => c.Role));
    }

    [Fact]
    public void CanonicalFilenameRemainsASupportedHintForContentWithoutStrongRoleEvidence()
    {
        const string weakDataModel = "# VisningstilstandType (enum)\n\nA list of values used by the application.\n";

        var canonical = SampleArtifactClassifier.Classify("data-model.md", weakDataModel);
        var renamed = SampleArtifactClassifier.Classify("arbitrary/renamed-document.md", weakDataModel);

        canonical.Status.Should().Be(ArtifactDiscoveryStatus.Detected);
        canonical.Role.Should().Be(WorkspaceArtifactType.DataModel);
        renamed.Status.Should().NotBe(ArtifactDiscoveryStatus.Detected,
            "a weakly structured document should not inherit a role after its only strong signal is removed");
    }

    [Theory]
    [InlineData("requirements.md", Spec, WorkspaceArtifactType.Specification)]
    [InlineData("implementation-plan.md", Plan, WorkspaceArtifactType.Plan)]
    [InlineData("domain-model.md", DataModel, WorkspaceArtifactType.DataModel)]
    [InlineData("work-items.md", Tasks, WorkspaceArtifactType.Tasks)]
    [InlineData("docs/requirements/school-attendance.md", Spec, WorkspaceArtifactType.Specification)]
    [InlineData("docs/architecture/domain.md", DataModel, WorkspaceArtifactType.DataModel)]
    [InlineData("docs/plans/delivery.md", Plan, WorkspaceArtifactType.Plan)]
    [InlineData("notes/anything.md", Tasks, WorkspaceArtifactType.Tasks)]
    public void CustomFilenamesAndFolders_AreClassifiedFromStructure(string path, string content, WorkspaceArtifactType role)
    {
        var result = SampleArtifactClassifier.Classify(path, content);

        result.Status.Should().Be(ArtifactDiscoveryStatus.Detected, string.Join("; ", result.Reasons));
        result.Role.Should().Be(role);
        result.Confidence.Should().BeOneOf(ArtifactConfidence.Strong, ArtifactConfidence.Confirmed);
        result.Reasons.Should().NotContain(r => r.StartsWith("Exact canonical filename"));
    }

    [Fact]
    public void FrontMatterMetadata_OutranksFilenameAndContent()
    {
        var content = "---\ntype: plan\ntitle: Delivery\n---\n" + Tasks;

        var result = SampleArtifactClassifier.Classify("tasks.md", content);

        result.Role.Should().Be(WorkspaceArtifactType.Plan);
        result.Confidence.Should().Be(ArtifactConfidence.Confirmed);
        result.Reasons.Single().Should().Contain("type: plan");
    }

    [Theory]
    [InlineData("artifact: data-model", WorkspaceArtifactType.DataModel)]
    [InlineData("documentType: specification", WorkspaceArtifactType.Specification)]
    [InlineData("role: tasks", WorkspaceArtifactType.Tasks)]
    public void SupportedMetadataKeys_DeclareTheRole(string line, WorkspaceArtifactType role)
    {
        var result = SampleArtifactClassifier.Classify("notes.md", $"---\n{line}\n---\n# Notes\n\nText.");

        result.Status.Should().Be(ArtifactDiscoveryStatus.Detected);
        result.Role.Should().Be(role);
    }

    [Fact]
    public void DocumentThatLooksLikePlanAndTasks_NeedsReview_NotArbitraryChoice()
    {
        const string mixed = """
            # Delivery plan and task list

            ## Implementation approach

            Phased delivery.

            ## Milestones

            - M1 intake
            - M2 storage

            ## Technical Context

            .NET 8.

            ## Tasks for intake

            - [ ] T001 Create endpoint
            - [ ] T002 Validate payload
            - [ ] T003 Store report
            - [ ] T004 Add logging
            - [ ] T005 Add metrics
            """;

        var result = SampleArtifactClassifier.Classify("delivery.md", mixed);

        result.Status.Should().Be(ArtifactDiscoveryStatus.NeedsReview);
        result.Role.Should().BeNull();
        result.Reasons.Single().Should().Contain("Multiple candidate roles");
        result.Candidates.Select(c => c.Role).Should().Contain([WorkspaceArtifactType.Plan, WorkspaceArtifactType.Tasks]);
    }

    [Fact]
    public void CanonicalFilenameContradictedByContent_NeedsReview()
    {
        var result = SampleArtifactClassifier.Classify("plan.md", Tasks);

        result.Status.Should().Be(ArtifactDiscoveryStatus.NeedsReview);
        result.Reasons.Single().Should().Contain("Filename suggests Plan").And.Contain("Tasks");
    }

    [Fact]
    public void ReadmeMentioningRequirements_IsNotASpecification()
    {
        const string readme = """
            # School service

            This repository contains the school service. See the requirements in the spec folder.

            ## Getting started

            Run `dotnet run`.
            """;

        SampleArtifactClassifier.Classify("README.md", readme).Status.Should().Be(ArtifactDiscoveryStatus.Unclassified);
        SampleArtifactClassifier.Classify("docs/overview.md", readme).Status.Should().Be(ArtifactDiscoveryStatus.Unclassified);
    }

    [Fact]
    public void SpecKitChecklistNamedRequirements_IsNotASpecification()
    {
        const string checklist = """
            # Specification Quality Checklist: School

            ## Requirement Completeness

            - [x] No [NEEDS CLARIFICATION] markers remain
            - [x] Requirements are testable
            - [x] Success criteria are measurable
            - [x] Edge cases are identified
            - [x] Scope is bounded
            """;

        var result = SampleArtifactClassifier.Classify("specs/001-school/checklists/requirements.md", checklist);

        result.Status.Should().Be(ArtifactDiscoveryStatus.Unclassified);
        result.Reasons.Single().Should().Contain("Checklist");
    }

    [Fact]
    public void WeakFilenameSynonymAlone_IsNotEnough()
    {
        var result = SampleArtifactClassifier.Classify("backlog.md", "# Backlog\n\nIdeas we may do later.");

        result.Status.Should().NotBe(ArtifactDiscoveryStatus.Detected);
    }

    [Fact]
    public void ExtensionAndFolderAlone_DoNotAssignARole()
    {
        var result = SampleArtifactClassifier.Classify("specs/notes.md", "# Meeting notes\n\nWe met on Monday.");

        result.Status.Should().Be(ArtifactDiscoveryStatus.Unclassified);
    }

    [Fact]
    public void FilenameHintIsCaseInsensitive()
    {
        SampleArtifactClassifier.Classify("SPEC.md", Spec).Role.Should().Be(WorkspaceArtifactType.Specification);
        SampleArtifactClassifier.Classify("Spec.MD", Spec).Role.Should().Be(WorkspaceArtifactType.Specification);
        SampleArtifactClassifier.Classify("Docs\\Plans\\Plan.md", Plan).Role.Should().Be(WorkspaceArtifactType.Plan);
    }

    [Fact]
    public void UnreadableDocument_IsParseError_NotMissing()
    {
        var result = SampleArtifactClassifier.Classify("spec.md", null);

        result.Status.Should().Be(ArtifactDiscoveryStatus.ParseError);
        result.Role.Should().BeNull();
    }

    [Fact]
    public void ClassificationIsDeterministic()
    {
        var first = SampleArtifactClassifier.Classify("docs/requirements/x.md", Spec);
        for (var i = 0; i < 5; i++)
        {
            var again = SampleArtifactClassifier.Classify("docs/requirements/x.md", Spec);
            again.Role.Should().Be(first.Role);
            again.Confidence.Should().Be(first.Confidence);
            again.Reasons.Should().Equal(first.Reasons);
        }
    }

    [Fact]
    public void CrLfAndBom_AreNormalised()
    {
        var text = "﻿" + Spec.Replace("\n", "\r\n");

        SampleArtifactClassifier.Classify("requirements.md", text).Role.Should().Be(WorkspaceArtifactType.Specification);
    }
}
