using System.Text.RegularExpressions;

namespace BirkNext.Api.Services.SourceAnalysis.Evidence;

/// <summary>
/// Static PowerShell reader for infrastructure scripts (line based): cmdlets and native tools (terraform, az …), file reads and writes,
/// <c>$env:</c> variables, <c>param()</c> arguments, Invoke-Expression, web cmdlets, Remove-Item, throw/Write-Error/Write-Warning messages,
/// exit statuses, $ErrorActionPreference and dot-sourcing. Never executed; Invoke-Expression makes the analysis Partial.
/// </summary>
internal static class PowerShellStaticReader
{
    private static readonly Regex Cmdlet = new(@"(?<![\w$-])([A-Z][a-z]+-[A-Z][A-Za-z]+)\b", RegexOptions.Compiled);
    private static readonly Regex Native = new(@"^\s*&?\s*['""]?(terraform|terraform-docs|tflint|az|git|kubectl|helm|docker|python3?|pwsh|bicep|azcopy|func|dotnet|curl|jq)['""]?(\s|$)", RegexOptions.Compiled);
    private static readonly HashSet<string> Writes = new(StringComparer.OrdinalIgnoreCase) { "Set-Content", "Add-Content", "Out-File", "Export-Csv", "Export-Clixml", "New-Item", "Copy-Item", "Move-Item" };
    private static readonly HashSet<string> Reads = new(StringComparer.OrdinalIgnoreCase) { "Get-Content", "Import-Csv", "Import-Clixml", "Get-ChildItem", "Test-Path", "Resolve-Path" };

    public static void Read(string text, ScriptFacts f)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        f.Lines = lines.Length;
        var inBlockComment = false;
        var sawCode = false;
        var paramBlock = false;
        for (var n = 0; n < lines.Length; n++)
        {
            var line = lines[n];
            var lineNo = n + 1;
            if (inBlockComment) { if (line.Contains("#>")) { inBlockComment = false; line = line[(line.IndexOf("#>", StringComparison.Ordinal) + 2)..]; } else { if (!sawCode && f.HeaderComments.Count < 40) f.HeaderComments.Add(line.Trim()); continue; } }
            if (line.Contains("<#")) { inBlockComment = !line.Contains("#>"); line = line[..line.IndexOf("<#", StringComparison.Ordinal)]; }
            var hash = Regex.Match(line, @"(^|\s)#");
            if (hash.Success && !Regex.IsMatch(line[..hash.Index], @"['""][^'""]*$"))
            {
                if (!sawCode && line.TrimStart().StartsWith('#') && f.HeaderComments.Count < 40) f.HeaderComments.Add(line.Trim().TrimStart('#').Trim());
                line = line[..hash.Index];
            }
            if (line.Trim().Length == 0) continue;
            sawCode = true;
            f.Literal(line, lineNo);
            foreach (Match url in Regex.Matches(line, @"https?://([^/\s""'$]+)")) f.Capability("May access network", $"URL host {url.Groups[1].Value} (not contacted)", lineNo);
            foreach (Match env in Regex.Matches(line, @"\$env:([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.IgnoreCase)) f.EnvironmentVariables.Add((env.Groups[1].Value, lineNo));
            if (Regex.IsMatch(line, @"^\s*param\s*\(", RegexOptions.IgnoreCase)) { paramBlock = true; f.EntryPoint ??= "param() block"; }
            if (paramBlock)
            {
                foreach (Match p in Regex.Matches(line, @"\$([A-Za-z_][A-Za-z0-9_]*)")) if (!f.CliArguments.Contains($"-{p.Groups[1].Value}")) f.CliArguments.Add($"-{p.Groups[1].Value}");
                if (line.Contains(')') && !Regex.IsMatch(line, @"\[[^\]]*\(")) paramBlock = !Regex.IsMatch(line.Trim(), @"^\)\s*$") && !line.TrimEnd().EndsWith(')');
            }
            if (Regex.Match(line, @"^\s*function\s+([A-Za-z_][\w-]*)", RegexOptions.IgnoreCase) is { Success: true } fn) { f.Functions.Add(fn.Groups[1].Value); f.Identifiers.Add(fn.Groups[1].Value); }
            foreach (Match v in Regex.Matches(line, @"^\s*\$([A-Za-z_][A-Za-z0-9_]*)\s*=")) f.Identifiers.Add(v.Groups[1].Value);
            if (Regex.Match(line, @"^\s*\.\s+['""]?([^'""\s]+\.ps1)") is { Success: true } dot) f.Includes.Add((dot.Groups[1].Value, lineNo));
            if (Regex.IsMatch(line, @"\$ErrorActionPreference\s*=\s*['""]Stop['""]", RegexOptions.IgnoreCase)) f.ErrExit = true;
            if (Regex.Match(line, @"\bexit\s+(\d+)\b", RegexOptions.IgnoreCase) is { Success: true } code) f.ExitCodes.Add(int.Parse(code.Groups[1].Value));
            else if (Regex.IsMatch(line, @"\bexit\s+\$", RegexOptions.IgnoreCase)) f.ConditionalFailure = true;
            if (Regex.Match(line, @"\b(throw|Write-Error)\s+(?:-Message\s+)?(['""])(.*?)\2", RegexOptions.IgnoreCase) is { Success: true } error)
            { f.Messages.Add((Placeholders(error.Groups[3].Value), lineNo, false)); f.ConditionalFailure = true; }
            if (Regex.Match(line, @"\bWrite-Warning\s+(?:-Message\s+)?(['""])(.*?)\1", RegexOptions.IgnoreCase) is { Success: true } warning) f.Messages.Add((Placeholders(warning.Groups[2].Value), lineNo, true));
            if (Regex.IsMatch(line, @"\bWrite-(Output|Host)\b", RegexOptions.IgnoreCase)) f.PrintsOutput = true;
            if (Regex.IsMatch(line, @"\b(Invoke-Expression|iex)\b", RegexOptions.IgnoreCase))
            {
                f.Dynamic = true;
                f.Capability("Dynamic execution detected", "Invoke-Expression", lineNo);
                f.Partial("Invoke-Expression builds commands at run time — static analysis is partial.");
            }
            if (Native.Match(line) is { Success: true } native)
                ShellStaticReader.CommandWords(f, Regex.Split(line.Trim().TrimStart('&').Trim(), @"\s+").Select(w => w.Trim('\'', '"')).ToList(), lineNo);
            foreach (Match c in Cmdlet.Matches(line))
            {
                var cmdlet = c.Groups[1].Value;
                f.Command(cmdlet, null, lineNo);
                var target = Regex.Match(line[(c.Index + c.Length)..], @"^\s+(?:-(?:Path|FilePath|LiteralPath)\s+)?['""]?([\w./\\\-*${}:]+)['""]?");
                var path = target.Success ? target.Groups[1].Value.Replace('\\', '/') : null;
                if (path is not null && path.StartsWith('-')) path = null;
                if (Writes.Contains(cmdlet)) { f.Capability("May modify files", cmdlet, lineNo); if (path is not null) f.Outputs.Add(("File write", Normalize(path), lineNo)); }
                else if (Reads.Contains(cmdlet) && path is not null) f.Inputs.Add((path.Contains('*') ? "File pattern" : "Path", Normalize(path), lineNo));
                if (cmdlet.Equals("Remove-Item", StringComparison.OrdinalIgnoreCase)) f.Capability("May delete files", cmdlet, lineNo);
                if (cmdlet is "Invoke-WebRequest" or "Invoke-RestMethod" or "Start-BitsTransfer") f.Capability("May access network", cmdlet, lineNo);
                if (cmdlet.StartsWith("New-Az", StringComparison.Ordinal) || cmdlet.StartsWith("Remove-Az", StringComparison.Ordinal) || cmdlet.StartsWith("Set-Az", StringComparison.Ordinal) || cmdlet.StartsWith("Update-Az", StringComparison.Ordinal))
                    f.Capability("May change cloud resources", cmdlet, lineNo);
                if (cmdlet is "Connect-AzAccount" or "Get-AzAccessToken" or "Get-Credential") f.Capability("May access credentials", cmdlet, lineNo);
            }
        }
    }

    private static string Normalize(string path) => Regex.Replace(path, @"\$\{?[A-Za-z_:][A-Za-z0-9_:]*\}?", "*").TrimStart('.', '/');

    private static string Placeholders(string message) => Regex.Replace(message, @"\$\([^)]*\)|\$\{?[A-Za-z_:][A-Za-z0-9_:]*\}?|\{\d+\}", "…");
}
