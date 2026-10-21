using BirkNext.Integrations;

namespace BirkNext.Web.Services;

/// <summary>
/// The active-run readiness of a message flow as three separate counts: Known (resolvable now from the integration, canonical source,
/// Test Pack reference or a pack default — or saved), Saved for the active run (explicitly stored, what a run uses), and Runtime verified
/// (observed; this pre-run model observes nothing, so it stays 0). Never collapsed into one "configured" count.
/// </summary>
public sealed record MessageFlowReadinessCounts(int Known, int Saved, int Verified, int Total)
{
    /// <summary>Saved and total are the readiness model's own counts; Known adds the unsaved items whose values are resolvable now.</summary>
    public static MessageFlowReadinessCounts From(IntegrationTestReadiness readiness, IReadOnlySet<string> knownKeys) =>
        new(readiness.Ready + readiness.Items.Count(item => !item.Satisfied && knownKeys.Contains(item.Key)), readiness.Ready, 0, readiness.Total);
}
