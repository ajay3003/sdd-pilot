using System.Security.Cryptography;
using System.Text;
using BirkNext.Web.Models;

namespace BirkNext.Web.Services;

public sealed record TaskAlignmentSnapshot(
    string ProjectName,
    string SpecificationHash,
    string TasksHash);

public sealed class TaskAlignmentSessionService
{
    public AlignmentReport? Report { get; private set; }
    public TaskAlignmentSnapshot? Snapshot { get; private set; }

    public bool HasCurrentResult(string? projectName, string specText, string tasksText) =>
        Report is not null && Snapshot == CreateSnapshot(projectName, specText, tasksText);

    public void SaveResult(AlignmentReport report, string? projectName, string specText, string tasksText)
    {
        Report = report;
        Snapshot = CreateSnapshot(projectName, specText, tasksText);
    }

    public void Clear()
    {
        Report = null;
        Snapshot = null;
    }

    public static TaskAlignmentSnapshot CreateSnapshot(string? projectName, string specText, string tasksText) =>
        new(
            NormalizeProjectName(projectName),
            HashText(specText),
            HashText(tasksText));

    private static string NormalizeProjectName(string? projectName) =>
        string.IsNullOrWhiteSpace(projectName) ? string.Empty : projectName.Trim();

    private static string HashText(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
