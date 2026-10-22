using System.Text;
using System.Text.RegularExpressions;

namespace BirkNext.Api.Services.SourceAnalysis.Evidence;

/// <summary>
/// Static Python reader for infrastructure scripts: a tokenizer (strings with prefixes, triple quotes and f-string placeholders, comments,
/// line continuation, bracket depth) and a logical-line walk with indentation scopes. It records imports, functions, the entry point, calls
/// (subprocess/os.system commands, file reads/writes/globs, environment variables, argparse/sys.argv, eval/exec, stderr messages, exit
/// statuses, network and delete operations) and path constants built from literals and <c>Path(__file__)</c>. Nothing is evaluated, imported
/// or executed; dynamic constructs make the analysis Partial.
/// </summary>
internal static class PythonStaticReader
{
    private const int MaxTokens = 200_000;
    private enum K { Name, Str, Num, Op, Newline, End }
    private readonly record struct Tok(K Kind, string Text, int Line, int Column);

    private static readonly HashSet<string> ProcessCalls = new(StringComparer.Ordinal) { "run", "call", "check_call", "check_output", "Popen", "getoutput", "getstatusoutput" };
    private static readonly HashSet<string> NetworkModules = new(StringComparer.Ordinal) { "urllib", "urllib.request", "urllib3", "requests", "httpx", "http.client", "socket", "aiohttp", "ftplib", "paramiko" };

    public static void Read(string text, ScriptFacts f, int lineOffset = 0)
    {
        var tokens = Tokenize(text.Replace("\r\n", "\n"), f, lineOffset);
        var lines = new List<List<Tok>>();
        var current = new List<Tok>();
        foreach (var t in tokens)
        {
            if (t.Kind is K.Newline or K.End) { if (current.Count > 0) lines.Add(current); current = []; }
            else current.Add(t);
        }
        var constants = new Dictionary<string, string>(StringComparer.Ordinal);
        var scopes = new Stack<(int Indent, string Name)>();
        var mainReturns = new List<List<Tok>>();
        var mainCalled = false;
        foreach (var line in lines)
        {
            var indent = line[0].Column;
            while (scopes.Count > 0 && indent <= scopes.Peek().Indent) scopes.Pop();
            var function = scopes.Count > 0 ? scopes.Peek().Name : null;
            var first = line[0];
            if (first.Kind == K.Name)
            {
                switch (first.Text)
                {
                    case "import":
                        foreach (var name in DottedNames(line.Skip(1).ToList(), stopAtAs: true)) Import(f, name, first.Line);
                        break;
                    case "from" when line.Count > 1:
                        var module = string.Concat(line.Skip(1).TakeWhile(t => t.Text != "import").Select(t => t.Text));
                        Import(f, module, first.Line);
                        break;
                    case "def" or "async" when line.FirstOrDefault(t => t.Kind == K.Name && t.Text != "def" && t.Text != "async") is { Kind: K.Name } def:
                        f.Functions.Add(def.Text);
                        f.Identifiers.Add(def.Text);
                        scopes.Push((indent, def.Text));
                        break;
                    case "class" when line.Count > 1:
                        scopes.Push((indent, line[1].Text));
                        f.Identifiers.Add(line[1].Text);
                        break;
                    case "if" when line.Any(t => t.Text == "__name__") && line.Any(t => t.Kind == K.Str && t.Text == "__main__"):
                        f.EntryPoint = "__main__ guard";
                        break;
                    case "return" when function == "main":
                        mainReturns.Add(line.Skip(1).ToList());
                        break;
                    case "raise" when line.Count > 1:
                        if (line[1].Text == "SystemExit") { mainCalled |= line.Any(t => t.Text == "main"); ExitStatus(f, line.Skip(2).ToList()); }
                        else
                        {
                            f.ConditionalFailure = true;
                            if (line.FirstOrDefault(t => t.Kind == K.Str) is { Kind: K.Str } message) f.Messages.Add((message.Text, message.Line, false));
                        }
                        break;
                }
            }
            if (line.Count >= 3 && line[0].Kind == K.Name && line[1].Text == "=" )
            {
                f.Identifiers.Add(line[0].Text);
                if (PathExpression(line.Skip(2).ToList(), constants, f) is { } path) constants[line[0].Text] = path;
            }
            mainCalled |= Calls(line, f, constants);
            if (line.Count >= 4 && line[0].Text == "sys" && line[2].Text == "exit") { }
        }
        if (f.EntryPoint is null && f.Functions.Contains("main") && mainCalled) f.EntryPoint = "main()";
        if (f.EntryPoint is null && lines.Any(l => l[0].Column == 0 && l[0].Kind == K.Name && l[0].Text is not ("import" or "from" or "def" or "class" or "async" or "@")))
            f.EntryPoint = "top-level statements";
        if (mainCalled || f.Functions.Contains("main"))
            foreach (var r in mainReturns) ExitStatus(f, r);
    }

    private static void Import(ScriptFacts f, string name, int line)
    {
        if (name.Length == 0) return;
        f.Imports.Add(name);
        if (NetworkModules.Contains(name) || NetworkModules.Any(n => name.StartsWith(n + ".", StringComparison.Ordinal)))
            f.Capability("May access network", $"imports {name}", line);
        if (name.StartsWith('.')) f.Includes.Add((name, line));
        else f.Includes.Add((name, line)); // resolved against the snapshot: only local modules become includes
    }

    private static IEnumerable<string> DottedNames(List<Tok> tokens, bool stopAtAs)
    {
        var sb = new StringBuilder();
        var skip = false;
        foreach (var t in tokens)
        {
            if (t.Text == ",") { if (sb.Length > 0) yield return sb.ToString(); sb.Clear(); skip = false; continue; }
            if (stopAtAs && t.Text == "as") { skip = true; continue; }
            if (skip) continue;
            if (t.Kind == K.Name || t.Text == ".") sb.Append(t.Text);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    /// <summary>A path an expression builds from literals, path constants and <c>Path(__file__)</c> parents ("wiki/reference/x.md"); null when none.</summary>
    private static string? PathExpression(List<Tok> tokens, Dictionary<string, string> constants, ScriptFacts f)
    {
        var parts = new List<string>();
        string? fileBase = null;
        var parents = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == K.Name && t.Text == "__file__") { fileBase = f.ScriptDirectory; continue; }
            if (t.Kind == K.Name && t.Text is "parent" or "dirname" && fileBase is not null) { parents++; continue; }
            if (t.Kind == K.Name && t.Text == "argv" && i > 0 && tokens[i - 1].Text == ".") Argument(f, "positional arguments (sys.argv)");
            if (t.Kind == K.Str && LooksLikePath(t.Text)) parts.Add(t.Text);
            else if (t.Kind == K.Name && constants.TryGetValue(t.Text, out var constant) && (i == 0 || tokens[i - 1].Text != ".") && (i + 1 >= tokens.Count || tokens[i + 1].Text != "("))
                parts.Add(constant);
        }
        if (fileBase is not null)
        {
            var dir = fileBase;
            for (var p = 1; p < parents; p++) dir = dir.Contains('/') ? dir[..dir.LastIndexOf('/')] : "";
            parts.Insert(0, dir);
        }
        var joined = string.Join('/', parts.Where(p => p.Length > 0)).Replace("//", "/");
        if (joined.StartsWith("./", StringComparison.Ordinal)) joined = joined[2..];
        return parts.Count == 0 ? null : joined.Length == 0 ? "." : joined;
    }

    private static bool LooksLikePath(string s) => s.Length is > 0 and < 200 && !s.Contains(' ') && !s.Contains('\n') && Regex.IsMatch(s, @"^[\w.\-*/~{}…]+$") && (s.Contains('/') || s.Contains('.') || Regex.IsMatch(s, @"^[a-z][\w\-]*$"));

    private static void Argument(ScriptFacts f, string label) { if (!f.CliArguments.Contains(label)) f.CliArguments.Add(label); }

    private static void ExitStatus(ScriptFacts f, List<Tok> expression)
    {
        var e = expression.Where(t => t.Text is not ("(" or ")")).ToList();
        if (e.Count == 0) { f.ExitCodes.Add(0); return; }
        if (e.Count == 1 && e[0].Kind == K.Num && int.TryParse(e[0].Text, out var code)) { f.ExitCodes.Add(code); return; }
        if (e.Count == 1 && e[0].Kind == K.Str) { f.ExitCodes.Add(1); f.Messages.Add((e[0].Text, e[0].Line, false)); return; }
        if (e.Count >= 1 && e[0].Text == "main") return;
        if (e.Any(t => t.Kind == K.Num && t.Text != "0") || e.Any(t => t.Kind == K.Name)) f.ConditionalFailure = true;
    }

    /// <summary>The calls of one logical line. Returns true when it calls main() as the process status (sys.exit(main())).</summary>
    private static bool Calls(List<Tok> line, ScriptFacts f, Dictionary<string, string> constants)
    {
        var mainCalled = false;
        for (var i = 0; i < line.Count; i++)
        {
            var t = line[i];
            if (t.Kind == K.Str)
            {
                f.Literal(t.Text, t.Line);
                if (Regex.Match(t.Text, @"^https?://([^/\s""'?#]+)") is { Success: true } url) f.Capability("May access network", $"URL host {url.Groups[1].Value} (not contacted)", t.Line);
                continue;
            }
            if (t.Kind != K.Name || (i > 0 && line[i - 1].Text == ".")) continue;
            // os.environ["X"]
            if (t.Text == "os" && i + 4 < line.Count && line[i + 2].Text == "environ" && line[i + 3].Text == "[" && line[i + 4].Kind == K.Str)
                f.EnvironmentVariables.Add((line[i + 4].Text, t.Line));
            if (t.Text == "sys" && i + 2 < line.Count && line[i + 2].Text == "argv") Argument(f, "positional arguments (sys.argv)");
            // dotted callee
            var j = i;
            var parts = new List<string> { t.Text };
            while (j + 2 < line.Count && line[j + 1].Text == "." && line[j + 2].Kind == K.Name) { parts.Add(line[j + 2].Text); j += 2; }
            if (j + 1 >= line.Count || line[j + 1].Text != "(") continue;
            var close = Close(line, j + 1);
            var args = Arguments(line, j + 2, close);
            var callee = string.Join('.', parts);
            var last = parts[^1];
            var receiver = parts.Count > 1 ? parts[^2] : null;
            Call(f, constants, callee, last, receiver, parts, args, t.Line, ref mainCalled);
        }
        return mainCalled;
    }

    private static void Call(ScriptFacts f, Dictionary<string, string> constants, string callee, string last, string? receiver, List<string> parts, List<List<Tok>> args, int line, ref bool mainCalled)
    {
        var positional = args.Where(a => !(a.Count >= 2 && a[0].Kind == K.Name && a[1].Text == "=")).ToList();
        List<Tok>? Keyword(string name) => args.FirstOrDefault(a => a.Count >= 2 && a[0].Text == name && a[1].Text == "=")?.Skip(2).ToList();
        string? FirstString(List<Tok>? a) => a?.FirstOrDefault(x => x.Kind == K.Str).Text;
        string? ReceiverPath() => receiver is not null && constants.TryGetValue(receiver, out var p) ? p : receiver is null ? null : null;

        if (parts[0] == "subprocess" && ProcessCalls.Contains(last) || callee is "os.system" or "os.popen" || callee.StartsWith("os.exec", StringComparison.Ordinal) || callee.StartsWith("os.spawn", StringComparison.Ordinal))
        {
            f.Capability("Runs external commands", callee, line);
            var a = positional.FirstOrDefault() ?? [];
            var words = a.Count > 0 && a[0].Text == "[" ? a.Where(x => x.Kind == K.Str).Select(x => x.Text).ToList()
                : a.FirstOrDefault(x => x.Kind == K.Str) is { Kind: K.Str } s ? Regex.Split(s.Text.Trim(), @"\s+").ToList() : [];
            if (words.Count == 0) { f.Partial("A command is built at run time and could not be statically resolved."); return; }
            ShellStaticReader.CommandWords(f, words, line);
            return;
        }
        switch (callee)
        {
            case "open" or "io.open" or "codecs.open":
            {
                var path = positional.Count > 0 ? PathExpression(positional[0], constants, f) : null;
                var mode = positional.Count > 1 ? FirstString(positional[1]) : FirstString(Keyword("mode"));
                if (path is null) return;
                if (mode is not null && mode.IndexOfAny(['w', 'a', 'x']) >= 0) { f.Outputs.Add(("File write", path, line)); f.Capability("May modify files", "writes files", line); }
                else f.Inputs.Add(("Path", path, line));
                return;
            }
            case "os.getenv" or "os.environ.get" or "environ.get" or "getenv":
                if (FirstString(positional.FirstOrDefault()) is { } env) f.EnvironmentVariables.Add((env, line));
                return;
            case "eval" or "exec" or "compile" or "__import__" or "importlib.import_module":
                f.Dynamic = true;
                f.Capability("Dynamic execution detected", $"{callee}()", line);
                f.Partial("Dynamic execution (eval/exec/dynamic import) — static analysis is partial.");
                return;
            case "print":
            {
                var target = Keyword("file");
                var text = string.Concat(positional.FirstOrDefault()?.Where(x => x.Kind == K.Str).Select(x => x.Text) ?? []);
                if (target is not null && target.Any(x => x.Text == "stderr")) { if (text.Length > 0) f.Messages.Add((text, line, text.TrimStart().StartsWith("warn", StringComparison.OrdinalIgnoreCase))); }
                else if (target is null) f.PrintsOutput = true;
                return;
            }
            case "sys.stderr.write":
                if (FirstString(positional.FirstOrDefault()) is { } message) f.Messages.Add((message, line, message.TrimStart().StartsWith("warn", StringComparison.OrdinalIgnoreCase)));
                return;
            case "sys.exit" or "exit" or "quit" or "os._exit":
                if (positional.FirstOrDefault() is { } status && status.Count > 0 && status[0].Text == "main") mainCalled = true;
                ExitStatus(f, positional.FirstOrDefault() ?? []);
                return;
            case "shutil.rmtree" or "os.remove" or "os.unlink" or "os.rmdir" or "os.removedirs":
                f.Capability("May delete files", callee, line);
                return;
            case "argparse.ArgumentParser" or "ArgumentParser":
                Argument(f, "argparse options");
                return;
        }
        if (parts[0] is "logging" or "logger" or "log" && last is "error" or "critical" or "exception" or "warning")
        {
            if (FirstString(positional.FirstOrDefault()) is { } message) f.Messages.Add((message, line, last == "warning"));
            return;
        }
        if (parts[0] is "requests" or "httpx" or "urllib3" || last is "urlopen" or "HTTPSConnection" or "HTTPConnection" or "create_connection")
        {
            var host = positional.SelectMany(a => a).Where(x => x.Kind == K.Str).Select(x => Regex.Match(x.Text, @"^https?://([^/\s""'?#]+)")).FirstOrDefault(m => m.Success)?.Groups[1].Value;
            f.Capability("May access network", host is null ? callee : $"URL host {host} (not contacted)", line);
            return;
        }
        if (last is "add_argument" && positional.FirstOrDefault()?.FirstOrDefault(x => x.Kind == K.Str) is { Kind: K.Str } option) { Argument(f, option.Text); return; }
        if (last is "write_text" or "write_bytes" or "touch" && ReceiverPath() is { } written) { f.Outputs.Add(("File write", written, line)); f.Capability("May modify files", "writes files", line); return; }
        if (last is "read_text" or "read_bytes" && ReceiverPath() is { } read) { f.Inputs.Add(("Path", read, line)); return; }
        if (last is "unlink" or "rmdir") { f.Capability("May delete files", callee, line); return; }
        if (last is "mkdir" or "makedirs") { f.Capability("May modify files", "creates directories", line); return; }
        if (last is "glob" or "rglob" or "iglob" && FirstString(positional.FirstOrDefault()) is { } pattern)
        {
            var root = parts[0] == "glob" ? null : ReceiverPath();
            f.Inputs.Add(("File pattern", root is null || root == "." ? (last == "rglob" ? $"**/{pattern}" : pattern) : $"{root}/{(last == "rglob" ? "**/" : "")}{pattern}", line));
            return;
        }
        if (last is "iterdir" or "walk" or "listdir" or "scandir" && (ReceiverPath() ?? (positional.Count > 0 ? PathExpression(positional[0], constants, f) : null)) is { } directory)
            f.Inputs.Add(("Path", directory, line));
    }

    private static int Close(List<Tok> line, int open)
    {
        var depth = 0;
        for (var k = open; k < line.Count; k++)
        {
            if (line[k].Text is "(" or "[" or "{") depth++;
            else if (line[k].Text is ")" or "]" or "}" && --depth == 0) return k;
        }
        return line.Count;
    }

    private static List<List<Tok>> Arguments(List<Tok> line, int start, int close)
    {
        var result = new List<List<Tok>>();
        var current = new List<Tok>();
        var depth = 0;
        for (var k = start; k < close && k < line.Count; k++)
        {
            var t = line[k];
            if (t.Text is "(" or "[" or "{") depth++;
            else if (t.Text is ")" or "]" or "}") depth--;
            if (t.Text == "," && depth == 0) { result.Add(current); current = []; continue; }
            current.Add(t);
        }
        if (current.Count > 0) result.Add(current);
        return result;
    }

    // ── tokenizer ─────────────────────────────────────────────────────────────────────────────────────────────────────

    private static List<Tok> Tokenize(string s, ScriptFacts f, int lineOffset)
    {
        var tokens = new List<Tok>();
        int i = 0, line = 1 + lineOffset, depth = 0, lineBegin = 0;
        var sawCode = false;
        while (i < s.Length)
        {
            if (tokens.Count > MaxTokens) { f.Partial("The script exceeds the static analysis size guard; only its first part was analyzed."); break; }
            var c = s[i];
            if (c == '\n')
            {
                if (depth == 0 && tokens.Count > 0 && tokens[^1].Kind != K.Newline) tokens.Add(new(K.Newline, "", line, 0));
                line++; i++; lineBegin = i; continue;
            }
            if (c is ' ' or '\t' or '\r' or '\f') { i++; continue; }
            if (c == '\\' && i + 1 < s.Length && s[i + 1] == '\n') { i += 2; line++; lineBegin = i; continue; }
            if (c == '#')
            {
                var end = s.IndexOf('\n', i);
                if (end < 0) end = s.Length;
                if (!sawCode && f.HeaderComments.Count < 40) f.HeaderComments.Add(s[(i + 1)..end].Trim());
                i = end; continue;
            }
            var column = i - lineBegin;
            if (char.IsLetter(c) || c == '_')
            {
                var j = i;
                while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
                var word = s[i..j];
                if (j < s.Length && s[j] is '"' or '\'' && word.Length <= 2 && word.All(ch => "rRbBuUfF".Contains(ch)))
                {
                    i = ReadString(s, j, word, ref line, tokens, column, f);
                    sawCode = true;
                    continue;
                }
                tokens.Add(new(K.Name, word, line, column));
                i = j; sawCode = true; continue;
            }
            if (c is '"' or '\'') { i = ReadString(s, i, "", ref line, tokens, column, f); sawCode = true; continue; }
            if (char.IsDigit(c))
            {
                var j = i;
                while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] is '.' or '_')) j++;
                tokens.Add(new(K.Num, s[i..j], line, column));
                i = j; sawCode = true; continue;
            }
            var two = i + 1 < s.Length ? s.Substring(i, 2) : "";
            var op = two is "==" or "!=" or "<=" or ">=" or "->" or ":=" or "**" or "//" ? two : c.ToString();
            if (op is "(" or "[" or "{") depth++;
            else if (op is ")" or "]" or "}") depth = Math.Max(0, depth - 1);
            tokens.Add(new(K.Op, op, line, column));
            i += op.Length; sawCode = true;
        }
        f.Lines = Math.Max(f.Lines, line - lineOffset);
        tokens.Add(new(K.Newline, "", line, 0));
        tokens.Add(new(K.End, "", line, 0));
        return tokens;
    }

    private static int ReadString(string s, int start, string prefix, ref int line, List<Tok> tokens, int column, ScriptFacts f)
    {
        var quote = s[start];
        var triple = start + 2 < s.Length && s[start + 1] == quote && s[start + 2] == quote;
        var raw = prefix.Contains('r', StringComparison.OrdinalIgnoreCase);
        var formatted = prefix.Contains('f', StringComparison.OrdinalIgnoreCase);
        var startLine = line;
        var i = start + (triple ? 3 : 1);
        var sb = new StringBuilder();
        while (i < s.Length)
        {
            var c = s[i];
            if (triple && c == quote && i + 2 < s.Length && s[i + 1] == quote && s[i + 2] == quote) { i += 3; return Done(); }
            if (!triple && c == quote) { i++; return Done(); }
            if (!triple && c == '\n') break;
            if (c == '\n') line++;
            if (c == '\\' && i + 1 < s.Length)
            {
                if (raw) { sb.Append(c).Append(s[i + 1]); }
                else sb.Append(s[i + 1] switch { 'n' or 't' => ' ', var x => x });
                if (s[i + 1] == '\n') line++;
                i += 2; continue;
            }
            if (formatted && c == '{')
            {
                if (i + 1 < s.Length && s[i + 1] == '{') { sb.Append('{'); i += 2; continue; }
                var depth = 0;
                while (i < s.Length) { if (s[i] == '{') depth++; else if (s[i] == '}' && --depth == 0) break; if (s[i] == '\n') line++; i++; }
                sb.Append('…'); i++; continue;
            }
            if (formatted && c == '}' && i + 1 < s.Length && s[i + 1] == '}') { sb.Append('}'); i += 2; continue; }
            sb.Append(c); i++;
        }
        f.ParseError = true;
        f.Partial("An unterminated string literal stopped the static analysis; the rest of the script is not analyzed.");
        return s.Length;

        int Done()
        {
            tokens.Add(new(K.Str, sb.ToString(), startLine, column));
            return i;
        }
    }
}
