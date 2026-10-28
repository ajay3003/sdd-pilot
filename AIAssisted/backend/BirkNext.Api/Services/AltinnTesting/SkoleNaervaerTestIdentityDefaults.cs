namespace BirkNext.Api.Services.AltinnTesting;

/// <summary>
/// The built-in Skolenærvær default test identities, owned by Test Data (the Altinn test data source): one reporter (a synthetic TestID person
/// with a confirmed TT02 Altinn profile) and one child. Added once when the options are bound — fill-if-absent by alias and by identifier, so a
/// restart, a configured identity or a session entry never creates a duplicate, and a configured default keeps precedence. The identifiers are
/// synthetic test identifiers; they never travel in setup packages (only roles/aliases do), and the UI outside Test Data shows the alias.
/// </summary>
public static class SkoleNaervaerTestIdentityDefaults
{
    public const string ReporterAlias = "skolenaervaer-reporter-default", ChildAlias = "skolenaervaer-child-default";
    public const string ReporterLabel = "Skolenærvær default reporter", ChildLabel = "Skolenærvær default child";
    private const string ReporterIdentifier = "05859497324", ChildIdentifier = "04061076692";

    public static AltinnTestingOptions Apply(AltinnTestingOptions options)
    {
        if (!options.BuiltInSkolenaervaerIdentities) return options;
        var reporters = options.UnattendedIdPortenTest.Reporters;
        if (!reporters.Any(r => string.Equals(r.Alias, ReporterAlias, StringComparison.Ordinal) || string.Equals(r.TestIdPersonIdentifier?.Trim(), ReporterIdentifier, StringComparison.Ordinal)))
            reporters.Add(new SyntheticReporterOptions
            {
                Alias = ReporterAlias, Label = ReporterLabel, TestIdPersonIdentifier = ReporterIdentifier, AllowedEnvironment = "TT02", AltinnRole = "PRIV",
                // The standard Skolenærvær TT02 reporter: its Altinn profile is a stated, built-in confirmation (no TT02 call is made here).
                AltinnProfileConfirmed = true, Default = !reporters.Any(r => r.Default), BuiltIn = true,
            });
        var children = options.SyntheticChildren;
        if (!children.Any(c => string.Equals(c.Alias, ChildAlias, StringComparison.Ordinal) || string.Equals(c.TestPersonIdentifier?.Trim(), ChildIdentifier, StringComparison.Ordinal)))
            children.Add(new SyntheticChildOptions
            {
                Alias = ChildAlias, Label = ChildLabel, TestPersonIdentifier = ChildIdentifier, KnownToPerson = true, Default = !children.Any(c => c.Default), BuiltIn = true,
            });
        return options;
    }
}
