namespace BirkNext.Api.Tests.Services.Schemas;

/// <summary>Real Bufdir message schemas checked into <c>AIAssisted/xsd</c> (public schema definitions, no data).</summary>
internal static class RealSchemaFixtures
{
    public const string Skolenaervaer200 = "bufdir_barnevern_tiltak_skolenaervaer_v2_0_0_altinn.xsd";
    public const string Skolenaervaer082 = "bufdir_barnevern_tiltak_skolenaervaer_v0_8_2_altinn.xsd";
    public const string VedleggMelding = "bufdir.melding.vedleggmelding.v2.0.0.xsd";
    public const string VedleggLib = "bufdir.melding.vedlegg.lib.v1.0.0.xsd";

    public static string Read(string name) => File.ReadAllText(Path.Combine(Folder(), name));

    private static string Folder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "AIAssisted", "xsd"))) directory = directory.Parent;
        return directory is null
            ? throw new DirectoryNotFoundException("AIAssisted/xsd was not found above the test output directory.")
            : Path.Combine(directory.FullName, "AIAssisted", "xsd");
    }
}
