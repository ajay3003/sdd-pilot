using System.Text.Json;
using BirkNext.TestPlanning;

namespace BirkNext.Api.Services.TestPlanning;

/// <summary>
/// Test plan templates (2026-11-26): the read-only built-ins plus the installation's custom templates, stored in App_Data beside the dashboard
/// settings (manual-testing/plan-templates.json; the same storage pattern). A template is structure only. Archiving keeps it (and every plan made
/// from it unchanged); nothing here touches a plan, a case, a revision or Azure DevOps.
/// </summary>
public sealed class TestPlanTemplateStore(string directory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private string FilePath => System.IO.Path.Combine(directory, "plan-templates.json");

    private sealed record Stored(List<TestPlanTemplate> Templates);

    private List<TestPlanTemplate> Custom()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath), Json)?.Templates ?? [] : []; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    private void Write(List<TestPlanTemplate> custom)
    {
        Directory.CreateDirectory(directory);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new Stored(custom), Json));
        File.Move(temp, FilePath, overwrite: true);
    }

    /// <summary>Built-ins first, then custom templates by name; archived ones only when asked for.</summary>
    public List<TestPlanTemplate> List(bool includeArchived = false)
    {
        lock (_gate)
            return [.. TestPlanTemplateRules.BuiltIn, .. Custom().Where(t => includeArchived || !t.Archived).OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public TestPlanTemplate? Find(string id) => List(includeArchived: true).FirstOrDefault(t => t.Id == id);

    /// <summary>Creates (id null) or changes a custom template. Built-ins are refused (copy them with Duplicate). The suites are validated.</summary>
    public TestPlanTemplate Save(string? id, string name, string? description, List<TemplateSuite> suites, string actor, DateTimeOffset now, Guid? savedFromPlan = null)
    {
        if (TestPlanTemplateRules.Validate(name, suites) is { } invalid) throw new ArgumentException(invalid);
        lock (_gate)
        {
            var custom = Custom();
            var clean = name.Trim()[..Math.Min(name.Trim().Length, 120)];
            if (List(includeArchived: false).Any(t => t.Id != id && string.Equals(t.Name.Trim(), clean, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"Det finnes allerede en mal som heter «{clean}».");
            TestPlanTemplate saved;
            if (id is null)
            {
                saved = new TestPlanTemplate { Id = "custom:" + Guid.NewGuid().ToString("N")[..12], Name = clean, Description = description?.Trim(), Suites = suites,
                    CreatedBy = actor, CreatedAt = now, UpdatedBy = actor, UpdatedAt = now, SavedFromPlan = savedFromPlan };
                custom.Add(saved);
            }
            else
            {
                if (id.StartsWith("builtin:", StringComparison.Ordinal)) throw new InvalidOperationException("Innebygde maler kan ikke endres. Lag en kopi og endre den.");
                var index = custom.FindIndex(t => t.Id == id);
                if (index < 0) throw new System.Collections.Generic.KeyNotFoundException("Malen finnes ikke.");
                saved = custom[index] with { Name = clean, Description = description?.Trim(), Suites = suites, UpdatedBy = actor, UpdatedAt = now };
                custom[index] = saved;
            }
            Write(custom);
            return saved;
        }
    }

    /// <summary>Archive or restore a custom template, or duplicate any template (a built-in's copy is editable).</summary>
    public TestPlanTemplate Apply(string id, TemplateAction action, string actor, DateTimeOffset now)
    {
        lock (_gate)
        {
            var custom = Custom();
            var source = List(includeArchived: true).FirstOrDefault(t => t.Id == id) ?? throw new System.Collections.Generic.KeyNotFoundException("Malen finnes ikke.");
            if (action == TemplateAction.Duplicate)
            {
                var name = source.Name + " (kopi)";
                for (var n = 2; custom.Any(t => t.Name == name); n++) name = $"{source.Name} (kopi {n})";
                var copy = source with { Id = "custom:" + Guid.NewGuid().ToString("N")[..12], Name = name, BuiltIn = false, Archived = false, CreatedBy = actor, CreatedAt = now, UpdatedBy = actor, UpdatedAt = now, SavedFromPlan = null };
                custom.Add(copy);
                Write(custom);
                return copy;
            }
            if (source.BuiltIn) throw new InvalidOperationException("Innebygde maler kan ikke arkiveres.");
            var index = custom.FindIndex(t => t.Id == id);
            custom[index] = custom[index] with { Archived = action == TemplateAction.Archive, UpdatedBy = actor, UpdatedAt = now };
            Write(custom);
            return custom[index];
        }
    }
}
