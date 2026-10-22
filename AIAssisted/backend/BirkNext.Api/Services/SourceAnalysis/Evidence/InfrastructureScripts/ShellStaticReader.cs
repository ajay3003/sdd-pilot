using System.Text;
using System.Text.RegularExpressions;

namespace BirkNext.Api.Services.SourceAnalysis.Evidence;

/// <summary>
/// Static shell (sh/bash) reader for infrastructure scripts: logical lines (comments and quotes respected, "\" continuations joined),
/// heredocs (a <c>python3 - &lt;&lt;'EOF'</c> body is read as embedded Python), simple commands split at ; &amp;&amp; || | $( ) and backticks,
/// redirections, stderr messages, exit statuses, set -e, positional arguments with defaults, source/. includes and environment variables.
/// Nothing is expanded, sourced or executed; a command whose name is built at run time makes the analysis Partial.
/// </summary>
internal static class ShellStaticReader
{
    private static readonly HashSet<string> Builtins = new(StringComparer.Ordinal)
    {
        "echo", "printf", "read", "local", "declare", "typeset", "export", "readonly", "set", "unset", "shift", "return", "exit", "cd", "pushd", "popd", "source", ".",
        "trap", "eval", "exec", "test", "true", "false", ":", "wait", "let", "type", "getopts", "shopt", "mapfile", "readarray", "continue", "break", "command", "builtin", "hash", "ulimit", "umask", "[", "[[",
    };
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
        { "if", "then", "elif", "else", "fi", "for", "in", "do", "done", "while", "until", "case", "esac", "function", "select", "time", "!", "{", "}", "]]", "]", ";;", "coproc" };
    private static readonly HashSet<string> SubcommandTools = new(StringComparer.Ordinal)
        { "terraform", "terraform-docs", "tflint", "tfsec", "checkov", "trivy", "az", "git", "kubectl", "helm", "docker", "podman", "npm", "uv", "pre-commit", "gh", "pip", "pip3", "apt", "apt-get", "brew", "pulumi", "dotnet", "func", "azcopy", "bicep" };
    private static readonly HashSet<string> CoreUtilities = new(StringComparer.Ordinal)
        { "cat", "grep", "egrep", "sed", "awk", "sort", "uniq", "head", "tail", "cut", "tr", "wc", "xargs", "find", "basename", "dirname", "mkdir", "rm", "mv", "cp", "ln", "touch", "chmod", "chown",
          "tee", "date", "sleep", "env", "printenv", "realpath", "readlink", "ls", "stat", "diff", "comm", "paste", "column", "fold", "seq", "yes", "uname", "id", "whoami", "install", "mktemp", "nproc", "sha256sum", "md5sum", "tar", "unzip", "gzip", "zip" };
    private static readonly HashSet<string> ShellVariables = new(StringComparer.Ordinal)
        { "HOME", "PATH", "PWD", "OLDPWD", "IFS", "LINENO", "BASH_SOURCE", "RANDOM", "SECONDS", "UID", "EUID", "USER", "SHELL", "TMPDIR", "BASH_REMATCH", "PIPESTATUS", "FUNCNAME", "HOSTNAME", "OSTYPE", "TERM", "LANG", "LC_ALL", "REPLY", "OPTARG", "OPTIND", "BASH_VERSION" };

    public static void Read(string text, ScriptFacts f)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        f.Lines = Math.Max(f.Lines, lines.Length);
        var sawCode = false;
        var variables = new List<(string Name, int Line)>();
        var locals = new HashSet<string>(StringComparer.Ordinal);
        for (var n = 0; n < lines.Length; n++)
        {
            var trimmed = lines[n].TrimStart();
            var lineNo = n + 1;
            if (trimmed.StartsWith('#'))
            {
                if (!sawCode && !trimmed.StartsWith("#!", StringComparison.Ordinal) && f.HeaderComments.Count < 40) f.HeaderComments.Add(trimmed.TrimStart('#').Trim());
                continue;
            }
            if (trimmed.Length == 0) continue;
            sawCode = true;
            // A logical line continues over "\" line ends, open quotes (multi-line awk/python programs) and multi-line array assignments.
            var raw = lines[n];
            while (n + 1 < lines.Length && (StripComment(raw).TrimEnd().EndsWith('\\') || Open(raw))) raw += "\n" + lines[++n];
            var logical = Regex.Replace(StripComment(raw), @"\\[ \t]*\n", " ");
            string? body = null;
            var bodyLine = 0;
            if (Regex.Match(logical, @"<<(-?)\s*(['""]?)([A-Za-z_][A-Za-z0-9_]*)\2") is { Success: true } heredoc)
            {
                var marker = heredoc.Groups[3].Value;
                var dash = heredoc.Groups[1].Value == "-";
                var collected = new List<string>();
                bodyLine = n + 2;
                var closed = false;
                while (n + 1 < lines.Length) { var l = lines[++n]; if ((dash ? l.Trim() : l.TrimEnd()) == marker) { closed = true; break; } collected.Add(l); }
                if (!closed) f.Partial("A heredoc is not terminated; static analysis is partial.");
                body = string.Join('\n', collected);
                logical = logical[..heredoc.Index] + logical[(heredoc.Index + heredoc.Length)..];
            }
            Statement(logical, lineNo, f, body, bodyLine, variables, locals);
        }
        foreach (var (name, line) in variables)
            if (!locals.Contains(name) && !ShellVariables.Contains(name) && !f.EnvironmentVariables.Any(e => e.Name == name)) f.EnvironmentVariables.Add((name, line));
    }

    /// <summary>Comments removed outside quotes: from an unquoted '#' at a word start to the end of that physical line.</summary>
    private static string StripComment(string text)
    {
        var sb = new StringBuilder(text.Length);
        char? quote = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quote is null && c == '\\' && i + 1 < text.Length) { sb.Append(c).Append(text[++i]); continue; }
            if (quote is not null) { sb.Append(c); if (c == quote) quote = null; else if (c == '\\' && quote == '"' && i + 1 < text.Length) sb.Append(text[++i]); continue; }
            if (c is '\'' or '"') { quote = c; sb.Append(c); continue; }
            if (c == '#' && (i == 0 || char.IsWhiteSpace(text[i - 1])))
            {
                var end = text.IndexOf('\n', i);
                if (end < 0) break;
                i = end - 1;
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>True while a quote or an array assignment "NAME=(" opened on this logical line is not closed yet.</summary>
    private static bool Open(string text)
    {
        var stripped = StripComment(text);
        char? quote = null;
        var array = 0;
        for (var i = 0; i < stripped.Length; i++)
        {
            var c = stripped[i];
            if (quote is null && c == '\\') { i++; continue; }
            if (quote is not null) { if (c == quote) quote = null; else if (c == '\\' && quote == '"') i++; continue; }
            if (c is '\'' or '"') { quote = c; continue; }
            if (c == '(' && (array > 0 || i > 0 && stripped[i - 1] == '=')) array++;
            else if (c == ')' && array > 0) array--;
        }
        return quote is not null || array > 0;
    }

    private static void Statement(string logical, int line, ScriptFacts f, string? heredocBody, int heredocLine, List<(string, int)> variables, HashSet<string> locals)
    {
        f.Literal(logical, line);
        foreach (Match url in Regex.Matches(logical, @"https?://([^/\s""'$]+)")) f.Capability("May access network", $"URL host {url.Groups[1].Value} (not contacted)", line);
        foreach (Match v in Regex.Matches(logical, @"(?<!\\)\$\{?([A-Za-z_][A-Za-z0-9_]*)")) variables.Add((v.Groups[1].Value, line));
        if (Regex.IsMatch(logical, @"\$\{?[1-9@*#]")) Argument(f, "positional arguments ($1, $@)");
        foreach (Match d in Regex.Matches(logical, @"\$\{[1-9]:-([^}]+)\}"))
            if (Regex.IsMatch(d.Groups[1].Value, @"^[\w./\-*]+$")) f.Inputs.Add(("Path", d.Groups[1].Value, line));
        if (Regex.Match(logical, @"^\s*(?:function\s+)?([A-Za-z_][\w\-]*)\s*\(\s*\)") is { Success: true } fn)
        { f.Functions.Add(fn.Groups[1].Value); f.Identifiers.Add(fn.Groups[1].Value); locals.Add(fn.Groups[1].Value); }
        foreach (Match a in Regex.Matches(logical, @"(?:^|[\s;(&|])(?:local\s+|declare\s+(?:-\w+\s+)*|export\s+|readonly\s+)?([A-Za-z_][A-Za-z0-9_]*)(?:\[[^\]]*\])?\+?="))
        { locals.Add(a.Groups[1].Value); f.Identifiers.Add(a.Groups[1].Value); }
        foreach (Match a in Regex.Matches(logical, @"\b(?:for|select)\s+([A-Za-z_][A-Za-z0-9_]*)\s+in\b")) locals.Add(a.Groups[1].Value);
        foreach (Match r in Regex.Matches(logical, @"\bread\s+(?:-\w+\s+)*([A-Za-z_][A-Za-z0-9_ ]*)")) foreach (var name in r.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)) locals.Add(name);

        foreach (var segment in Segments(logical))
        {
            var words = Words(segment);
            var stderr = words.Any(w => w is ">&2" or "1>&2") || Regex.IsMatch(segment, @"(^|\s)1?>&2(\s|$)");
            var k = 0;
            while (k < words.Count && (Keywords.Contains(words[k]) || Regex.IsMatch(words[k], @"^[A-Za-z_][A-Za-z0-9_]*(\[[^\]]*\])?\+?="))) k++;
            if (k >= words.Count) continue;
            var cmd = words[k];
            var args = words.Skip(k + 1).ToList();
            Redirections(f, words, line);
            if (cmd.StartsWith('$')) { f.Partial("A command name is built at run time and could not be statically resolved."); continue; }
            var name = cmd.Split('/').Last();
            if (Builtins.Contains(name) || locals.Contains(name))
            {
                switch (name)
                {
                    case "exit":
                        var status = args.FirstOrDefault(a => !a.StartsWith('>') && !a.StartsWith('1'+">"));
                        if (status is null) break;
                        if (int.TryParse(status, out var code)) f.ExitCodes.Add(code); else f.ConditionalFailure = true;
                        break;
                    case "set":
                        if (args.Any(a => Regex.IsMatch(a, @"^-[a-z]*e[a-z]*$")) || (args.Contains("-o") && args.Contains("errexit"))) f.ErrExit = true;
                        break;
                    case "echo" or "printf":
                        var message = Message(args.Where(a => !a.StartsWith('>') && a is not ("1>&2" or "-e" or "-n")).ToList(), name == "printf");
                        if (stderr) { if (message.Length > 0) f.Messages.Add((message, line, Regex.IsMatch(message, @"^\s*warn", RegexOptions.IgnoreCase))); }
                        else f.PrintsOutput = true;
                        break;
                    case "source" or "." when args.Count > 0:
                        f.Includes.Add((args[0], line));
                        break;
                    case "eval":
                        f.Dynamic = true;
                        f.Capability("Dynamic execution detected", "eval", line);
                        f.Partial("eval builds commands at run time — static analysis is partial.");
                        break;
                    case "exec" or "command" when args.Count > 0:
                        CommandWords(f, args, line);
                        break;
                }
                continue;
            }
            // Embedded programs: python3 - <<'EOF' … EOF (read as Python), python -c "…".
            if (name is "python" or "python3" or "python2" or "py")
            {
                if (heredocBody is not null && args.Contains("-"))
                {
                    f.Embedded.Add($"Python (heredoc, line {heredocLine})");
                    PythonStaticReader.Read(heredocBody, f, heredocLine - 1);
                }
                else if (args.IndexOf("-c") is var c and >= 0 && c + 1 < args.Count)
                {
                    f.Embedded.Add($"Python (-c, line {line})");
                    PythonStaticReader.Read(args[c + 1], f, line - 1);
                }
            }
            CommandWords(f, words.Skip(k).ToList(), line);
            if (name == "tee") foreach (var target in args.Where(a => !a.StartsWith('-') && !a.StartsWith("/dev/"))) f.Outputs.Add(("Redirect", Path(target), line));
            if (name == "find" && args.FirstOrDefault(a => !a.StartsWith('-')) is { } root) f.Inputs.Add(("Path", Path(root), line));
            foreach (var arg in args.Where(a => Regex.IsMatch(a, @"\.(tf|tfvars|hcl)(\b|$)|\*\.tf")))
                f.Inputs.Add(("File pattern", Path(arg), line));
        }
    }

    private static void Argument(ScriptFacts f, string label) { if (!f.CliArguments.Contains(label)) f.CliArguments.Add(label); }

    /// <summary>A path word with variable parts written as '*' ("$layer_dir"*.tf → */*.tf is too lossy; "$dir/README.md" → */README.md).</summary>
    private static string Path(string word)
    {
        var p = Regex.Replace(word.Trim('"', '\''), @"\$\{[^}]*\}|\$[A-Za-z_@*#?0-9][A-Za-z0-9_]*", "*");
        p = Regex.Replace(p, @"\*+", "*");
        return p.StartsWith("./", StringComparison.Ordinal) ? p[2..] : p;
    }

    private static string Message(List<string> args, bool printf)
    {
        if (args.Count == 0) return "";
        var text = printf ? args[0] : string.Join(' ', args);
        if (printf) text = Regex.Replace(text, @"%[-+ 0#]*\d*(?:\.\d+)?[sdqifxXc]", "…");
        text = Regex.Replace(text, @"\\[nt]", " ");
        text = Regex.Replace(text, @"\$\{[^}]*\}|\$\(([^()]|\([^()]*\))*\)|\$[A-Za-z_@*#?0-9][A-Za-z0-9_]*", "…");
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static void Redirections(ScriptFacts f, List<string> words, int line)
    {
        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i];
            var m = Regex.Match(w, @"^(?:[1&]?>>?|>\|)(.*)$");
            if (!m.Success || w.StartsWith("2>", StringComparison.Ordinal)) continue;
            var target = m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : i + 1 < words.Count ? words[i + 1] : "";
            if (target.Length == 0 || target.StartsWith('&') || target.StartsWith("/dev/", StringComparison.Ordinal)) continue;
            f.Outputs.Add(("Redirect", Path(target), line));
            f.Capability("May modify files", "writes files", line);
        }
    }

    /// <summary>A command as words (shared with the Python reader for subprocess calls): records the command, a subcommand for multi-command
    /// tools ("terraform fmt -check", "az role assignment list") and the neutral capabilities the command implies. Never runs anything.</summary>
    internal static void CommandWords(ScriptFacts f, IReadOnlyList<string> raw, int line)
    {
        var w = raw.Where(x => x.Length > 0).ToList();
        var i = 0;
        while (i < w.Count && (w[i] is "sudo" or "env" or "command" or "exec" or "time" or "nohup" or "xargs" || Regex.IsMatch(w[i], @"^[A-Za-z_]\w*=") || (w[i].StartsWith('-') && i > 0)))
        {
            if (w[i] == "sudo") f.Capability("May require elevated privileges", "sudo", line);
            i++;
        }
        if (i >= w.Count) return;
        var cmd = w[i].Split('/').Last();
        if (cmd.Length == 0 || cmd.StartsWith('$') || cmd.StartsWith('>') || !Regex.IsMatch(cmd, @"^[A-Za-z0-9_.+\-]+$")) return;
        var rest = w.Skip(i + 1).ToList();
        string? sub = null;
        if (SubcommandTools.Contains(cmd))
        {
            var words = rest.TakeWhile(x => !x.StartsWith('>') && x != "|").Where(x => Regex.IsMatch(x, @"^[a-z][a-z0-9\-]*$")).ToList();
            var take = cmd is "az" or "gh" ? 3 : cmd is "terraform-docs" or "uv" or "pre-commit" or "git" or "kubectl" or "helm" or "docker" or "dotnet" ? 2 : 1;
            sub = words.Count == 0 ? null : string.Join(' ', words.Take(take));
            if (cmd == "terraform" && sub is not null)
            {
                var flags = rest.Where(x => x is "-check" or "-diff" or "-recursive" or "-auto-approve" or "-destroy").Distinct().ToList();
                if (flags.Count > 0) sub = $"{sub} {string.Join(' ', flags)}";
            }
        }
        if (cmd.EndsWith(".sh", StringComparison.Ordinal) || cmd.EndsWith(".py", StringComparison.Ordinal) || cmd.EndsWith(".ps1", StringComparison.Ordinal))
        {
            f.Includes.Add((w[i], line));
            f.Command("local script", null, line);
            return;
        }
        if (cmd is "python" or "python3" or "bash" or "sh" or "pwsh" or "powershell" && rest.FirstOrDefault(a => Regex.IsMatch(a, @"\.(py|sh|bash|ps1)$")) is { } script)
            f.Includes.Add((script, line));
        f.Command(cmd, sub, line);
        var s = sub ?? "";
        switch (cmd)
        {
            case "terraform" when s.StartsWith("apply") || s.StartsWith("destroy"):
                f.Capability("May perform deployment", $"terraform {s}", line); break;
            case "terraform" when s.StartsWith("import") || s.StartsWith("state") || s.StartsWith("taint"):
                f.Capability("May change Terraform state", $"terraform {s}", line); break;
            case "terraform" when s.StartsWith("init") || s.StartsWith("plan") || s.StartsWith("refresh"):
                f.Capability("May access network", $"terraform {s} (providers, modules, backend)", line); break;
            case "az" when s.StartsWith("login") || s.Contains("get-access-token"):
                f.Capability("May access credentials", $"az {s}", line); break;
            case "az" when Regex.IsMatch(s, @"\b(create|delete|update|set|assign|purge|deploy|start|stop|restart)\b"):
                f.Capability("May change cloud resources", $"az {s}", line); break;
            case "az" or "curl" or "wget" or "Invoke-WebRequest" or "Invoke-RestMethod":
                f.Capability("May access network", sub is null ? cmd : $"{cmd} {s}", line); break;
            case "git" when s.StartsWith("ls-remote") || s.StartsWith("clone") || s.StartsWith("fetch") || s.StartsWith("pull") || s.StartsWith("push"):
                f.Capability("May access network", $"git {s}", line); break;
            case "rm" or "rmdir" or "shred":
                f.Capability("May delete files", cmd, line); break;
            case "mv" or "cp" or "install" or "mkdir" or "touch" or "chmod" or "ln":
                f.Capability("May modify files", cmd, line); break;
            case "sed" when rest.Any(a => a.StartsWith("-i", StringComparison.Ordinal)):
                f.Capability("May modify files", "sed -i", line); break;
            case "kubectl" when Regex.IsMatch(s, @"^(apply|delete|create|replace|patch|rollout)"):
            case "helm" when Regex.IsMatch(s, @"^(install|upgrade|uninstall|rollback)"):
                f.Capability("May perform deployment", $"{cmd} {s}", line); break;
        }
        if (cmd is "npm" or "pip" or "pip3" or "apt" or "apt-get" or "brew" or "uv" && Regex.IsMatch(s, @"\binstall\b") || cmd == "curl" && rest.Contains("|"))
            f.Capability("Installs tools", sub is null ? cmd : $"{cmd} {s}", line);
        if (cmd == "terraform-docs" || cmd == "terraform")
        {
            var output = rest.IndexOf("--output-file");
            if (output >= 0 && output + 1 < rest.Count) f.Outputs.Add(("Tool output", Path(rest[output + 1]), line));
        }
    }

    internal static bool IsCoreUtility(string command) => CoreUtilities.Contains(command);

    /// <summary>Simple-command segments of a logical line, split at ; &amp;&amp; || | ( ) $( and backticks outside quotes.</summary>
    private static List<string> Segments(string logical)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        char? quote = null;
        for (var i = 0; i < logical.Length; i++)
        {
            var c = logical[i];
            if (quote is not null)
            {
                if (c == quote) quote = null;
                else if (quote == '"' && c == '\\' && i + 1 < logical.Length) { sb.Append(c).Append(logical[++i]); continue; }
                else if (quote == '"' && c == '$' && i + 1 < logical.Length && logical[i + 1] == '(') { Flush(); i++; continue; }
                sb.Append(c);
                continue;
            }
            if (c is '\'' or '"') { quote = c; sb.Append(c); continue; }
            if (c == '\\' && i + 1 < logical.Length) { sb.Append(c).Append(logical[++i]); continue; }
            // An array assignment's elements are words, never commands: NAME=( a b c ) stays in its segment.
            if (c == '(' && sb.Length > 0 && sb[^1] == '=')
            {
                var depth = 0;
                char? q = null;
                sb.Append("()");
                for (; i < logical.Length; i++)
                {
                    var x = logical[i];
                    if (q is not null) { if (x == q) q = null; else if (x == '\\' && q == '"') i++; continue; }
                    if (x is '\'' or '"') { q = x; continue; }
                    if (x == '(') depth++;
                    else if (x == ')' && --depth == 0) break;
                }
                continue;
            }
            if (c is ';' or '`' or '(' or ')' or '\n' || (c == '|' && !(i > 0 && logical[i - 1] == '>')) || (c == '&' && i + 1 < logical.Length && logical[i + 1] == '&'))
            {
                if (c == '(' && i > 0 && logical[i - 1] is '$' or '<' or '>') { if (sb.Length > 0) sb.Length--; }
                if (c == '(' && i > 0 && sb.ToString().TrimEnd().Length > 0 && Regex.IsMatch(sb.ToString(), @"[A-Za-z_][\w\-]*\s*$") && i + 1 < logical.Length && logical[i + 1] == ')') { sb.Append("()"); i++; continue; }
                Flush();
                if (c == '&' || (c == '|' && i + 1 < logical.Length && logical[i + 1] == '|')) i++;
                continue;
            }
            sb.Append(c);
        }
        Flush();
        return result;

        void Flush()
        {
            var s = sb.ToString().Trim();
            if (s.Length > 0) result.Add(s);
            sb.Clear();
        }
    }

    /// <summary>Words of a simple command with quotes removed (variables kept verbatim).</summary>
    private static List<string> Words(string segment)
    {
        var words = new List<string>();
        var sb = new StringBuilder();
        char? quote = null;
        var any = false;
        for (var i = 0; i < segment.Length; i++)
        {
            var c = segment[i];
            if (quote is not null)
            {
                if (c == quote) quote = null;
                else if (quote == '"' && c == '\\' && i + 1 < segment.Length && segment[i + 1] is '"' or '\\' or '$' or '`') sb.Append(segment[++i]);
                else sb.Append(c);
                continue;
            }
            if (c is '\'' or '"') { quote = c; any = true; continue; }
            if (char.IsWhiteSpace(c)) { if (sb.Length > 0 || any) words.Add(sb.ToString()); sb.Clear(); any = false; continue; }
            if (c == '\\' && i + 1 < segment.Length) { sb.Append(segment[++i]); continue; }
            sb.Append(c);
        }
        if (sb.Length > 0 || any) words.Add(sb.ToString());
        return words;
    }
}
