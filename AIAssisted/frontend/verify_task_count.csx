#!/usr/bin/env dotnet-script
// Task Explorer Verification Script
// Run: dotnet script verify_task_count.csx

#r "nuget: System.Text.RegularExpressions, 4.3.1"

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.IO;

// Load the actual file
var tasksPath = @"C:\Users\ajaan\source\sdd-repos\BirkNext\SampleData\autorisasjon\tasks.md";
var markdown = File.ReadAllText(tasksPath);

Console.WriteLine("=== TASK EXPLORER VERIFICATION ===\n");
Console.WriteLine($"File: {tasksPath}");
Console.WriteLine($"File size: {markdown.Length} bytes\n");

// Regex patterns (from TaskExplorerService)
var checkboxTaskRe = new Regex(@"^\s*[-*]\s+\[([xX ])\]\s+(.+)$", RegexOptions.Multiline);
var bareTaskRe = new Regex(@"^\s*[-*]?\s*T(\d{2,4}[a-zA-Z]*)\b\s*[-–.]?\s*(.*)$", RegexOptions.Multiline);
var taskIdRe = new Regex(@"\bT(\d{2,4}[a-zA-Z]*)\b");
var parallelRe = new Regex(@"\[P\]", RegexOptions.IgnoreCase);
var userStoryTagRe = new Regex(@"\[US(\d+(?:[–\-]\d+)?)\]|\[Story\???\]", RegexOptions.IgnoreCase);

// Find all checkbox tasks
var checkboxMatches = checkboxTaskRe.Matches(markdown);
Console.WriteLine($"Checkbox tasks found: {checkboxMatches.Count}");

var allTasks = new List<(string taskId, bool completed, bool parallel, string userStory, int lineNum)>();
var lineNum = 1;
var lines = markdown.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

foreach (var line in lines)
{
    var cm = checkboxTaskRe.Match(line);
    if (cm.Success)
    {
        var completed = cm.Groups[1].Value is "x" or "X";
        var body = cm.Groups[2].Value.Trim();

        var taskMatch = taskIdRe.Match(body);
        if (taskMatch.Success)
        {
            var captured = taskMatch.Groups[1].Value;
            var digitMatch = Regex.Match(captured, @"^(\d+)([a-zA-Z]*)$");
            if (digitMatch.Success)
            {
                var digits = digitMatch.Groups[1].Value.PadLeft(3, '0');
                var suffix = digitMatch.Groups[2].Value;
                var taskId = $"T{digits}{suffix}";

                var isParallel = parallelRe.IsMatch(body);
                var usMatch = userStoryTagRe.Match(body);
                var userStory = usMatch.Success && usMatch.Groups[1].Success ? $"US{usMatch.Groups[1].Value}" : null;

                allTasks.Add((taskId, completed, isParallel, userStory, lineNum));
            }
        }
    }
    lineNum++;
}

Console.WriteLine("\n=== ALL PARSED TASKS ===\n");
Console.WriteLine("Idx | TaskId  | Completed | Parallel | UserStory | Line#");
Console.WriteLine("----+---------+-----------+----------+-----------+-------");

for (int i = 0; i < allTasks.Count; i++)
{
    var (taskId, completed, parallel, us, line) = allTasks[i];
    Console.WriteLine($"{i+1:3} | {taskId,-7} | {(completed ? "YES" : "NO"),-9} | {(parallel ? "YES" : "NO"),-8} | {us ?? "-",-9} | {line}");
}

Console.WriteLine($"\n=== EXACT COUNTS ===\n");
Console.WriteLine($"Total task nodes: {allTasks.Count}");
Console.WriteLine($"Distinct Task IDs: {allTasks.Select(t => t.taskId).Distinct().Count()}");
Console.WriteLine($"Completed: {allTasks.Count(t => t.completed)}");
Console.WriteLine($"Incomplete: {allTasks.Count(t => !t.completed)}");
Console.WriteLine($"Parallel: {allTasks.Count(t => t.parallel)}");

Console.WriteLine($"\n=== VERIFY SPECIFIC IDs ===\n");
var requiredIds = new[] { "T001", "T002", "T006", "T018", "T019", "T024", "T028", "T032", "T033", "T033a", "T034", "T035", "T036", "T037" };
foreach (var id in requiredIds)
{
    var matches = allTasks.Where(t => t.taskId == id).ToList();
    if (matches.Count == 0)
        Console.WriteLine($"❌ {id,-7} MISSING");
    else if (matches.Count > 1)
        Console.WriteLine($"⚠️  {id,-7} DUPLICATE ({matches.Count} occurrences)");
    else
        Console.WriteLine($"✓  {id,-7} found (line {matches[0].lineNum})");
}

Console.WriteLine($"\n=== PHASE DISTRIBUTION ===\n");
var phasePattern = new Regex(@"^## Phase (\d+):", RegexOptions.Multiline);
var phaseMatches = phasePattern.Matches(markdown);
Console.WriteLine($"Phases found in headings: {phaseMatches.Count}");

// Map tasks to phases (crude heuristic: task belongs to phase before next phase heading)
var phaseRanges = new Dictionary<int, (int start, int end)>();
var phaseHeadingLines = new List<int>();
lineNum = 1;
foreach (var line in lines)
{
    var m = Regex.Match(line, @"^## Phase (\d+):");
    if (m.Success && int.TryParse(m.Groups[1].Value, out var phaseNum))
    {
        phaseHeadingLines.Add(lineNum);
    }
    lineNum++;
}

// Build phase ranges
for (int i = 0; i < phaseHeadingLines.Count; i++)
{
    var phaseNum = i + 1;
    var startLine = phaseHeadingLines[i];
    var endLine = i + 1 < phaseHeadingLines.Count ? phaseHeadingLines[i + 1] : lines.Length;
    phaseRanges[phaseNum] = (startLine, endLine);
}

Console.WriteLine("\nTasks per phase:");
for (int p = 1; p <= 7; p++)
{
    if (phaseRanges.TryGetValue(p, out var range))
    {
        var tasksInPhase = allTasks.Where(t => t.lineNum >= range.start && t.lineNum < range.end).ToList();
        Console.WriteLine($"Phase {p}: {tasksInPhase.Count} tasks ({string.Join(", ", tasksInPhase.Select(t => t.taskId))})");
    }
    else
    {
        Console.WriteLine($"Phase {p}: NO HEADING FOUND");
    }
}

int totalInPhases = Enumerable.Range(1, 7)
    .Where(p => phaseRanges.ContainsKey(p))
    .Sum(p => allTasks.Count(t => t.lineNum >= phaseRanges[p].start && t.lineNum < phaseRanges[p].end));

Console.WriteLine($"\nSum of all phases: {totalInPhases}");

Console.WriteLine($"\n=== CHECK FOR DUPLICATES ===\n");
var taskGroups = allTasks.GroupBy(t => t.taskId).Where(g => g.Count() > 1);
if (!taskGroups.Any())
    Console.WriteLine("✓ No duplicate task IDs");
else
    foreach (var group in taskGroups)
        Console.WriteLine($"❌ {group.Key} appears {group.Count()} times");

Console.WriteLine($"\n=== FINAL CONCLUSION ===\n");
if (allTasks.Count == 39 &&
    allTasks.Count(t => t.completed) == 39 &&
    allTasks.Select(t => t.taskId).Distinct().Count() == 39 &&
    !taskGroups.Any())
{
    Console.WriteLine("✓ PASS - All 39 tasks parsed correctly");
}
else
{
    Console.WriteLine("❌ FAIL - Mismatch detected");
    Console.WriteLine($"   Expected: 39 total, 39 completed, 39 distinct, 0 duplicates");
    Console.WriteLine($"   Got: {allTasks.Count} total, {allTasks.Count(t => t.completed)} completed, {allTasks.Select(t => t.taskId).Distinct().Count()} distinct, {taskGroups.Sum(g => g.Count() - 1)} duplicates");
}
