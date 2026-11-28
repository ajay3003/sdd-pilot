using System.Text.RegularExpressions;

namespace BirkNext.TestPlanning;

/// <summary>
/// Technical test cases (Technical Testing V2, 2026-11-28). A technical case is an ordinary saved case of the shared plan (same lifecycle, review,
/// revisions and history) whose type-specific details are stored in its existing <see cref="TestCaseEdits.TemplateValues"/> under keys «tech.*».
/// No parallel case system and no schema change. One template per test type says which fields matter; none is required (a field is filled when
/// known). Manual cases never show these fields. Values are descriptions and references — a value that looks like a credential is refused.
/// </summary>
public static partial class TechnicalCaseTemplates
{
    public const string Version = "technical-case/1";
    public const string Prefix = "tech.";

    /// <param name="Key">Stored key (without <see cref="Prefix"/>).</param>
    /// <param name="Help">Plain-language explanation shown under the field.</param>
    /// <param name="Advanced">Shown under «Avanserte tekniske detaljer».</param>
    public sealed record Field(string Key, string Label, string Help, bool Advanced = false);

    public static readonly IReadOnlyList<Field> Fields =
    [
        new("objective", "Testmål", "Hva testen skal vise, i én setning."),
        new("system", "System eller komponent", "Hvilken tjeneste eller komponent som testes."),
        new("entry", "Startpunkt eller utløser", "Kallet, meldingen eller hendelsen som starter testen."),
        new("operation", "REST-rute eller GraphQL-operasjon", "For eksempel «POST /api/skole/v1/reports» eller «query GetBarn»."),
        new("auth", "Autentisering og tilgang", "Hvilken rolle eller tilgang som trengs. Aldri passord eller token."),
        new("environment", "Miljø", "Hvilket testmiljø testen kjøres i."),
        new("testdata", "Testdata", "Hvilke testdata som brukes (navn eller referanse)."),
        new("input", "Inndata eller forespørsel", "Hva som sendes inn."),
        new("dependencies", "Nedstrøms avhengigheter", "Tjenester, køer eller databaser testen er avhengig av."),
        new("response", "Forventet respons", "Statuskode og svar som forventes."),
        new("downstream", "Forventet tilstand etterpå", "Hva som skal være endret i andre systemer."),
        new("events", "Forventede hendelser eller meldinger", "Topic, kø eller hendelse som skal publiseres."),
        new("persistence", "Lagring", "Hva som skal være lagret, og hvor.", Advanced: true),
        new("observability", "Observerbarhet", "Logg, måling eller sporing som bekrefter resultatet.", Advanced: true),
        new("correlation", "Korrelasjons- eller sporings-ID", "Hvordan kjøringen spores gjennom systemene.", Advanced: true),
        new("cleanup", "Opprydding og gjenoppretting", "Hva som må ryddes etter testen.", Advanced: true),
        new("failure", "Feilsituasjon og retry", "Forventet oppførsel ved feil, tidsavbrudd eller ny forsøk.", Advanced: true),
        new("evidence", "Kildebevis", "Kontrakt, dokument eller test i kilden som testen bygger på.", Advanced: true),
    ];

    /// <summary>The test types and the fields each one shows first (the others stay available under advanced details).</summary>
    public static readonly IReadOnlyDictionary<TestWorkCategory, string[]> Templates = new Dictionary<TestWorkCategory, string[]>
    {
        [TestWorkCategory.TechnicalApi] = ["objective", "system", "operation", "auth", "environment", "input", "response", "failure"],
        [TestWorkCategory.TechnicalGraphQl] = ["objective", "system", "operation", "auth", "environment", "input", "response", "failure"],
        [TestWorkCategory.TechnicalIntegration] = ["objective", "system", "entry", "events", "dependencies", "downstream", "observability", "failure"],
        [TestWorkCategory.TechnicalE2E] = ["objective", "entry", "dependencies", "environment", "testdata", "response", "downstream", "events", "correlation"],
        [TestWorkCategory.Hybrid] = ["objective", "entry", "environment", "testdata", "downstream", "events"],
        [TestWorkCategory.TechnicalOther] = ["objective", "system", "environment", "response"],
    };

    public static IReadOnlyList<Field> For(TestWorkCategory type)
    {
        var keys = Templates.TryGetValue(type, out var k) ? k : Templates[TestWorkCategory.TechnicalOther];
        return [.. keys.Select(key => Fields.First(f => f.Key == key))];
    }

    public static string? Value(TestCaseEdits edits, string key) => edits.TemplateValues?.GetValueOrDefault(Prefix + key);

    /// <summary>The stored type of a technical case («tech.type»), when chosen.</summary>
    public static TestWorkCategory? TypeOf(TestCaseEdits edits) =>
        Value(edits, "type") is { } t && Enum.TryParse<TestWorkCategory>(t, out var c) ? c : null;

    /// <summary>Filled technical fields of a case (for readiness: a technical case is described when its template's objective, entry/operation and
    /// expected response/state are known).</summary>
    public static int Filled(TestCaseEdits edits) => Fields.Count(f => !string.IsNullOrWhiteSpace(Value(edits, f.Key)));

    public static bool Described(TestCaseEdits edits) =>
        !string.IsNullOrWhiteSpace(Value(edits, "objective"))
        && (!string.IsNullOrWhiteSpace(Value(edits, "entry")) || !string.IsNullOrWhiteSpace(Value(edits, "operation")))
        && (!string.IsNullOrWhiteSpace(Value(edits, "response")) || !string.IsNullOrWhiteSpace(Value(edits, "downstream")) || !string.IsNullOrWhiteSpace(Value(edits, "events")));

    /// <summary>A value that looks like a credential: a bearer/basic header, a JWT, a «password=…»/«secret=…»/«token=…» assignment, an Azure SAS or
    /// connection-string key, or a long random-looking string. Such a value is never stored in a test case or a result.</summary>
    public static bool LooksLikeSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return SecretRe().IsMatch(value) || JwtRe().IsMatch(value) || value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Any(w => w.Length >= 40 && RandomRe().IsMatch(w));
    }

    public static bool LooksLikeSecretAny(IEnumerable<string?> values) => values.Any(LooksLikeSecret);

    [GeneratedRegex(@"(?i)\b(bearer|basic)\s+[a-z0-9._~+/=-]{12,}|\b(password|passord|passwd|pwd|secret|client_secret|api[_-]?key|token|accountkey|sharedaccesskey|sig)\s*[:=]\s*\S{6,}")]
    private static partial Regex SecretRe();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}")]
    private static partial Regex JwtRe();

    [GeneratedRegex(@"^(?=.*[A-Z])(?=.*[a-z])(?=.*\d)[A-Za-z0-9+/_=-]{40,}$")]
    private static partial Regex RandomRe();
}
