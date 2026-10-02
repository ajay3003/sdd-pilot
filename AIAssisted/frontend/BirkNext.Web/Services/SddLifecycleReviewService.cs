using System.Security.Cryptography;
using System.Text;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>Deterministic transformations for the persisted SDD evidence graph.</summary>
public static class SddLifecycleReviewService
{
    public static void ResolveQuestion(SddLifecycleState lifecycle, string id, string text, string resolution, string authorityReference, IEnumerable<string>? affectedRequirements = null)
    {
        if (string.IsNullOrWhiteSpace(resolution) || string.IsNullOrWhiteSpace(authorityReference))
            throw new ArgumentException("An explicit resolution and its authoritative reference are required.");
        var question = lifecycle.Questions.FirstOrDefault(x => x.Id == id);
        if (question is null)
        {
            question = new SddClarification { Id = id, Text = text };
            lifecycle.Questions.Add(question);
        }
        question.Status = "Resolved";
        question.Resolution = resolution.Trim();
        question.ResolutionReference = authorityReference.Trim();
        question.ResolvedAt = DateTimeOffset.UtcNow;
        question.RequirementIds = affectedRequirements?.ToList() ?? [];
        if (!lifecycle.Decisions.Any(x => x.Id == authorityReference.Trim()))
        {
            lifecycle.Decisions.Add(new SddDecision
            {
                Id = authorityReference.Trim(),
                Title = $"Resolution of {id}",
                Description = question.Resolution,
                Status = "Accepted",
                SourceReference = authorityReference.Trim(),
                ResolvesQuestionIds = [id],
                AffectedRequirementIds = question.RequirementIds.ToList()
            });
        }
    }

    public static void ReconcileRequirements(SddLifecycleState lifecycle, IEnumerable<SemanticRequirement> requirements, Guid? artifactRevisionId = null)
    {
        var current = lifecycle.RequirementSnapshots.Where(x => x.IsCurrent).ToList();
        foreach (var snapshot in current) snapshot.IsCurrent = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var requirement in requirements)
        {
            seen.Add(requirement.Id);
            var acFingerprint = Hash(string.Join("\n", requirement.LinkedAcceptanceScenarios
                .Select(x => $"{x.Id}|{x.Title}|{x.Given}|{x.When}|{x.Then}").OrderBy(x => x, StringComparer.Ordinal)));
            var combined = Hash(Hash(requirement.Text) + acFingerprint);
            var prior = current.Where(x => x.RequirementId.Equals(requirement.Id, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.CapturedAt).FirstOrDefault();
            if (prior is null)
            {
                lifecycle.RequirementChanges.Add(new SddRequirementChange { RequirementId = requirement.Id, Change = "Added", CurrentFingerprint = combined });
            }
            else if (prior.Fingerprint == combined)
            {
                prior.IsCurrent = true;
                continue;
            }
            else
            {
                var criteriaChanged = prior.AcceptanceCriteriaFingerprint != acFingerprint;
                var criterionIds = requirement.LinkedAcceptanceScenarios.Select(x => x.Id ?? x.Title).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var previousCriterionIds = prior.AcceptanceCriterionIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
                lifecycle.RequirementChanges.Add(new SddRequirementChange
                {
                    RequirementId = requirement.Id,
                    Change = "Modified",
                    ChangeDetail = criteriaChanged ? "Requirement or acceptance criteria changed" : "Requirement text changed",
                    PreviousFingerprint = prior.Fingerprint,
                    CurrentFingerprint = combined
                });
                foreach (var link in lifecycle.Links.Where(x => x.Currentness == "Current" &&
                    (x.FromId.Equals(requirement.Id, StringComparison.OrdinalIgnoreCase) || x.ToId.Equals(requirement.Id, StringComparison.OrdinalIgnoreCase))))
                {
                    link.Currentness = "PotentiallyStale";
                    link.Reason = criteriaChanged ? "Requirement or acceptance criteria revision changed; reconfirm this link." : "Requirement revision changed; reconfirm this link.";
                }
                foreach (var evidence in lifecycle.ImplementationEvidence.Where(x => x.RequirementId.Equals(requirement.Id, StringComparison.OrdinalIgnoreCase) && x.Currentness == "Current"))
                { evidence.Currentness = "PotentiallyStale"; evidence.CurrentnessReason = "Requirement revision changed; source evidence requires review."; }
                foreach (var evidence in lifecycle.TestEvidence.Where(x => x.RequirementId.Equals(requirement.Id, StringComparison.OrdinalIgnoreCase) && x.Currentness == "Current"))
                { evidence.Currentness = "PotentiallyStale"; evidence.CurrentnessReason = "Requirement revision changed; test design requires review."; }
                foreach (var evidence in lifecycle.TestExecutions.Where(x => x.RequirementReferences.Contains(requirement.Id, StringComparer.OrdinalIgnoreCase) && x.Currentness == "Current"))
                { evidence.Currentness = "PotentiallyStale"; evidence.CurrentnessReason = "Requirement revision changed after this execution was recorded; the historical result is retained."; }
                foreach (var evidence in lifecycle.TestExecutions.Where(x => x.Currentness == "Current" &&
                    x.AcceptanceCriterionReferences.Any(id => criterionIds.Contains(id) || previousCriterionIds.Contains(id))))
                { evidence.Currentness = "PotentiallyStale"; evidence.CurrentnessReason = "Acceptance criterion changed after this execution was recorded; the historical result is retained."; }
            }
            lifecycle.RequirementSnapshots.Add(new SddRequirementSnapshot
            {
                RequirementId = requirement.Id,
                Fingerprint = combined,
                AcceptanceCriteriaFingerprint = acFingerprint,
                AcceptanceCriterionIds = requirement.LinkedAcceptanceScenarios.Select(x => x.Id ?? x.Title).ToList(),
                ArtifactRevisionId = artifactRevisionId,
                IsCurrent = true
            });
        }
        foreach (var removed in current.Where(x => !seen.Contains(x.RequirementId)))
        {
            lifecycle.RequirementChanges.Add(new SddRequirementChange { RequirementId = removed.RequirementId, Change = "Removed", PreviousFingerprint = removed.Fingerprint });
            foreach (var link in lifecycle.Links.Where(x => x.Currentness == "Current" &&
                (x.FromId.Equals(removed.RequirementId, StringComparison.OrdinalIgnoreCase) || x.ToId.Equals(removed.RequirementId, StringComparison.OrdinalIgnoreCase))))
            {
                link.Currentness = "Historical";
                link.Reason = "Requirement was removed from the current specification revision.";
            }
            foreach (var evidence in lifecycle.ImplementationEvidence.Where(x => x.RequirementId.Equals(removed.RequirementId, StringComparison.OrdinalIgnoreCase) && x.Currentness == "Current")) { evidence.Currentness = "Historical"; evidence.CurrentnessReason = "Requirement is absent from the current specification revision."; }
            foreach (var evidence in lifecycle.TestEvidence.Where(x => x.RequirementId.Equals(removed.RequirementId, StringComparison.OrdinalIgnoreCase) && x.Currentness == "Current")) { evidence.Currentness = "Historical"; evidence.CurrentnessReason = "Requirement is absent from the current specification revision."; }
            foreach (var evidence in lifecycle.TestExecutions.Where(x => x.RequirementReferences.Contains(removed.RequirementId, StringComparer.OrdinalIgnoreCase) && x.Currentness == "Current")) { evidence.Currentness = "Historical"; evidence.CurrentnessReason = "Requirement is absent from the current specification revision; execution result retained as history."; }
            foreach (var evidence in lifecycle.TestExecutions.Where(x => x.Currentness == "Current" &&
                x.AcceptanceCriterionReferences.Any(id => removed.AcceptanceCriterionIds.Contains(id, StringComparer.OrdinalIgnoreCase)))) { evidence.Currentness = "Historical"; evidence.CurrentnessReason = "Acceptance criterion was removed with its requirement; execution result retained as history."; }
        }
    }

    public static void AddOrRefreshLink(SddLifecycleState lifecycle, string from, string to, string relationship, string confidence)
    {
        var existing = lifecycle.Links.FirstOrDefault(x => x.FromId == from && x.ToId == to && x.Relationship == relationship);
        if (existing is not null)
        {
            // Rebuilding a view is not evidence revalidation. Preserve targeted stale/history decisions
            // until an explicit reconfirmation action validates the link against changed inputs.
            if (existing.Currentness != "Current") return;
            if (existing.Confidence == confidence && existing.Provenance == "SharedReviewContext") return;
            existing.Confidence = confidence;
            existing.Provenance = "SharedReviewContext";
            return;
        }
        lifecycle.Links.Add(new SddTraceabilityLink
        {
            FromId = from, ToId = to, Relationship = relationship, Confidence = confidence,
            Provenance = "SharedReviewContext", Currentness = "Current", LastValidatedAt = DateTimeOffset.UtcNow
        });
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? "")));
}
