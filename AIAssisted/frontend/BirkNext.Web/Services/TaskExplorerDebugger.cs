using System.Text.RegularExpressions;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

/// <summary>
/// Debug helper for tracing parser behavior without modifying TaskExplorerService
/// </summary>
public static class TaskExplorerDebugger
{
    public static void DebugPhaseMetadata(string markdown)
    {
        var tokens = MarkdownTokenizer.Tokenize(markdown);
        Console.WriteLine("=== PHASE METADATA TOKEN TRACE ===\n");

        var phaseCount = 0;
        var tokensAfterPhase = 0;
        bool justFoundPhase = false;

        for (var i = 0; i < tokens.Count; i++)
        {
            var tok = tokens[i];

            // Check for Phase heading
            if (tok.Kind == MarkdownTokenKind.Heading && tok.HeadingLevel == 2 &&
                tok.Content.Contains("Phase", StringComparison.OrdinalIgnoreCase))
            {
                phaseCount++;
                justFoundPhase = true;
                tokensAfterPhase = 0;
                Console.WriteLine($"\n[Phase {phaseCount}] {tok.Content}");
                Console.WriteLine("Next 15 tokens:");
                continue;
            }

            // Print tokens after phase heading
            if (justFoundPhase)
            {
                tokensAfterPhase++;
                if (tokensAfterPhase > 15)
                {
                    justFoundPhase = false;
                    continue;
                }

                var isPurpose = tok.Kind == MarkdownTokenKind.Text && tok.Content.Contains("**Purpose**", StringComparison.OrdinalIgnoreCase);
                var isGoal = tok.Kind == MarkdownTokenKind.Text && tok.Content.Contains("**Goal**", StringComparison.OrdinalIgnoreCase);
                var isTest = tok.Kind == MarkdownTokenKind.Text && tok.Content.Contains("**Independent Test**", StringComparison.OrdinalIgnoreCase);
                var isCheckpoint = tok.Kind == MarkdownTokenKind.Text && tok.Content.Contains("**Checkpoint**", StringComparison.OrdinalIgnoreCase);

                var mark = isPurpose ? "PURPOSE" : isGoal ? "GOAL" : isTest ? "ITEST" : isCheckpoint ? "CKPT" : "    ";
                var content = tok.RawLine.Length > 80 ? tok.RawLine[..80] + "..." : tok.RawLine;
                Console.WriteLine($"  {mark} [{tok.Kind:G}] {content}");
            }
        }

        Console.WriteLine($"\n=== Found {phaseCount} phases ===\n");
    }

    public static void DebugParallelTasks(string markdown)
    {
        var tokens = MarkdownTokenizer.Tokenize(markdown);
        var parallelRe = new Regex(@"\[P\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var taskIdRe = new Regex(@"\b(T\d{2,4}[a-zA-Z]*)\b");

        Console.WriteLine("=== PARALLEL TASKS ([P] MARKER) ===\n");

        var count = 0;
        foreach (var tok in tokens)
        {
            if (tok.Kind != MarkdownTokenKind.BulletItem) continue;
            if (!tok.RawLine.Contains("[X]") && !tok.RawLine.Contains("[x]")) continue;
            if (!parallelRe.IsMatch(tok.RawLine)) continue;

            count++;
            var taskMatch = taskIdRe.Match(tok.RawLine);
            var taskId = taskMatch.Success ? taskMatch.Groups[1].Value : "NO_ID";

            Console.WriteLine($"{count:D2}. {taskId}: {tok.RawLine[..Math.Min(80, tok.RawLine.Length)]}");
        }

        Console.WriteLine($"\nTotal [P] tasks: {count}\n");
    }
}
