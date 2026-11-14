namespace BirkNext.Web.Tests;

/// <summary>
/// Tests that change process-wide state — <see cref="System.Globalization.CultureInfo.DefaultThreadCurrentCulture"/> — run in this
/// collection, alone. While such a test runs, every new thread in the test process starts with its culture; in parallel, unrelated tests
/// rendered "−1" (U+2212, the Norwegian minus) instead of "-1" and failed intermittently in the full suite.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessCultureCollection
{
    public const string Name = "Process-wide culture";
}
