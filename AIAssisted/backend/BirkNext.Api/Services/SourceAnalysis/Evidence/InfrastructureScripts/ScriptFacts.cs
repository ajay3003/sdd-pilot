namespace BirkNext.Api.Services.SourceAnalysis.Evidence;

/// <summary>
/// What one static script reader found in ONE script, raw and in memory only. Nothing here is stored: the infrastructure script provider
/// classifies these facts and redacts every value before it becomes <see cref="BirkNext.SourceDomains.InfrastructureScript"/> evidence.
/// </summary>
internal sealed class ScriptFacts
{
    public const int MaxLiterals = 3_000;

    public string ScriptPath { get; init; } = "";
    public string ScriptDirectory => ScriptPath.Contains('/') ? ScriptPath[..ScriptPath.LastIndexOf('/')] : "";
    public List<string> Imports { get; } = [];
    public List<string> Functions { get; } = [];
    /// <summary>Raw include targets (Python local imports, shell source/., PowerShell dot-sourcing) — resolved against the snapshot later.</summary>
    public List<(string Raw, int Line)> Includes { get; } = [];
    public List<(string Command, string? Subcommand, int Line)> Commands { get; } = [];
    public List<(string Kind, string Value, int Line)> Inputs { get; } = [];
    public List<(string Kind, string Path, int Line)> Outputs { get; } = [];
    public List<(string Name, int Line)> EnvironmentVariables { get; } = [];
    public List<string> CliArguments { get; } = [];
    /// <summary>String literals and patterns (capped), used only to recognise Terraform concepts the code refers to.</summary>
    public List<(string Text, int Line)> Literals { get; } = [];
    /// <summary>Messages the script reports problems with (stderr, raise, Write-Error), placeholders already "…".</summary>
    public List<(string Text, int Line, bool Warning)> Messages { get; } = [];
    public List<int> ExitCodes { get; } = [];
    /// <summary>An exit/return status computed at run time (exit $violations, sys.exit(1 if total else 0), raise).</summary>
    public bool ConditionalFailure { get; set; }
    /// <summary>set -e / $ErrorActionPreference = 'Stop': fails on command errors, which is not a validation rule.</summary>
    public bool ErrExit { get; set; }
    public bool PrintsOutput { get; set; }
    public List<(string Capability, string Detail, int Line)> Capabilities { get; } = [];
    public bool Dynamic { get; set; }
    public string? EntryPoint { get; set; }
    public List<string> Embedded { get; } = [];
    /// <summary>Function, variable and constant names (lower-cased words feed the purpose classification).</summary>
    public List<string> Identifiers { get; } = [];
    /// <summary>The leading comment block (documented intent — supporting evidence only, never the sole basis).</summary>
    public List<string> HeaderComments { get; } = [];
    public string? PartialReason { get; set; }
    public bool ParseError { get; set; }
    public int Lines { get; set; }

    public void Literal(string text, int line)
    {
        if (Literals.Count < MaxLiterals && text.Length > 0) Literals.Add((text.Length > 400 ? text[..400] : text, line));
    }

    public void Capability(string capability, string detail, int line)
    {
        if (!Capabilities.Any(c => c.Capability == capability && c.Detail == detail)) Capabilities.Add((capability, detail, line));
    }

    public void Command(string command, string? subcommand, int line)
    {
        if (command.Length == 0) return;
        if (!Commands.Any(c => c.Command == command && c.Subcommand == subcommand)) Commands.Add((command, subcommand, line));
    }

    public void Partial(string reason) => PartialReason ??= reason;
}
