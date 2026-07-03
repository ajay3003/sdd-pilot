# Extension Guide: How to Extend BirkNext

## Design Principles

Before extending BirkNext, understand these principles:

1. **ReviewContext is Derived, Not Cached**
   - Don't build ReviewContext in pages
   - Always call `reviewContextProvider.GetCurrent()`
   - ReviewContext rebuilds automatically on artifact changes

2. **Single Event Pipeline**
   - ArtifactsChanged → ReviewContextProvider.RebuildAsync() → ReviewContextChanged
   - Subscribe to the right event for your use case

3. **No Page Lifecycle Ownership**
   - Pages don't own auto-save
   - Pages don't own ReviewContext rebuilds
   - Pages don't own workspace coordination

4. **Workspace Artifacts are Source of Truth**
   - ReviewContext is derived from artifacts
   - Semantic models are derived from ReviewContext
   - Always trace back to artifacts as source

---

## Adding a New Analysis Page

### Step 1: Inject ReviewContextProvider

```razor
@page "/my-analysis"
@inject IReviewContextProvider ReviewContextProvider

@code {
    private ReviewContext? _context;

    protected override void OnInitialized()
    {
        _context = ReviewContextProvider.GetCurrent();
        if (_context == null)
        {
            ErrorMessage = "Load a workspace first";
            return;
        }

        // Use _context for analysis
        var requirements = _context.GetRequirements();
    }
}
```

### Step 2: React to Changes (If Needed)

```razor
@code {
    protected override void OnInitialized()
    {
        ReviewContextProvider.ReviewContextChanged += OnReviewContextChanged;
    }

    private void OnReviewContextChanged(object? sender, EventArgs e)
    {
        _context = ReviewContextProvider.GetCurrent();
        StateHasChanged();
    }

    void IDisposable.Dispose()
    {
        ReviewContextProvider.ReviewContextChanged -= OnReviewContextChanged;
    }
}
```

### Step 3: Query ReviewContext

```csharp
// Get requirements with test coverage
var withTests = _context.GetRequirementsWithTests();

// Get requirements without tests
var withoutTests = _context.GetRequirementsWithoutTests();

// Check coverage
bool hasCoverage = _context.HasTestCoverage("REQ-123");

// Get tasks linked to requirement
var linkedTasks = _context.GetLinkedTasks("REQ-123");

// Get all entities
var entities = _context.GetDataEntities();
```

---

## Adding a New Semantic Model

### Step 1: Create the Semantic Model Class

```csharp
namespace BirkNext.Web.Models;

public sealed class MyCustomSemanticModel
{
    public List<MyCustomItem> Items { get; set; } = new();
}

public sealed class MyCustomItem
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
}
```

### Step 2: Create the Parser

```csharp
namespace BirkNext.Web.Services;

public sealed class MyCustomParser
{
    public MyCustomDocument Parse(string markdown)
    {
        // Parse markdown into document
        return new MyCustomDocument { /* ... */ };
    }
}

public sealed class MyCustomDocument
{
    public List<string> Items { get; set; } = new();
}
```

### Step 3: Create the Analysis Service

```csharp
public sealed class MyCustomAnalysisService
{
    public static MyCustomSemanticModel BuildSemanticModel(MyCustomDocument document)
    {
        var model = new MyCustomSemanticModel();
        
        foreach (var item in document.Items)
        {
            model.Items.Add(new MyCustomItem
            {
                Id = item,
                Title = item
            });
        }

        return model;
    }
}
```

### Step 4: Add to ReviewContextProvider

```csharp
// In ReviewContextProvider.RebuildAsync()
var myCustomModel = BuildMyCustomModel();

// In ReviewContextFactory.Create()
// Add to ReviewContext properties
MyCustomSemanticModel Custom { get; init; }
```

---

## Adding a New Workspace Artifact Type

### Step 1: Add to WorkspaceArtifactType Enum

```csharp
namespace BirkNext.Web.Models;

public enum WorkspaceArtifactType
{
    Constitution,
    Specification,
    Plan,
    Tasks,
    DataModel,
    MyNewArtifact  // ← Add here
}
```

### Step 2: Create Parser and Analysis Service

Follow the pattern above in "Adding a New Semantic Model"

### Step 3: Update ReviewContextProvider

Add builder method:

```csharp
private MySemanticModel BuildMyModel()
{
    if (!_artifacts.Has(WorkspaceArtifactType.MyNewArtifact))
        return new MySemanticModel();

    var artifact = _artifacts.Get(WorkspaceArtifactType.MyNewArtifact);
    if (artifact?.Text == null)
        return new MySemanticModel();

    try
    {
        var document = _parser.Parse(artifact.Text);
        return MyAnalysisService.BuildSemanticModel(document);
    }
    catch
    {
        return new MySemanticModel();
    }
}
```

### Step 4: Update Database

Add column to WorkspaceArtifacts:

```sql
-- Migration
ALTER TABLE WorkspaceArtifacts
ADD MyNewArtifactContent NVARCHAR(MAX);
```

---

## Adding a New Workflow Step

### Step 1: Define Step in RecommendedWorkflowService

```csharp
private async Task<WorkflowStepViewModel> BuildMyStep()
{
    var isReady = CheckMyStepReadiness();
    
    return new WorkflowStepViewModel
    {
        Key = "my_step",
        Title = "My Custom Step",
        Status = isReady ? "ready" : "blocked",
        Blockers = GetMyStepBlockers()
    };
}

private bool CheckMyStepReadiness()
{
    // Determine readiness based on artifacts
    // Return true if step can proceed
}
```

### Step 2: Add to Workflow Steps List

```csharp
public async Task<IReadOnlyList<WorkflowStepViewModel>> BuildStepsAsync()
{
    var steps = new List<WorkflowStepViewModel>
    {
        // ... existing steps ...
        await BuildMyStep()
    };

    return steps;
}
```

### Step 3: Add Approval Handling

```csharp
// In RecommendedWorkflowController
[HttpPost("approve-step")]
public async Task<WorkflowReadiness> ApproveStepAsync(string stepKey)
{
    if (stepKey == "my_step")
    {
        // Handle my step approval
        await _service.ApproveMyStepAsync(workspaceId);
    }
}
```

---

## Common Extension Patterns

### Pattern 1: New Analysis Service

```csharp
// Create service
public interface IMyAnalysisService
{
    MyAnalysisResult Analyze(ReviewContext context);
}

// Register in DI
builder.Services.AddSingleton<IMyAnalysisService, MyAnalysisService>();

// Inject in page
@inject IMyAnalysisService analyzer

// Use
var result = analyzer.Analyze(_context);
```

### Pattern 2: New Query API

```csharp
// Add to ReviewContext
public MyCustomItem? FindMyItem(string id)
{
    return Custom.Items.FirstOrDefault(i => i.Id == id);
}

// Use in page
var item = _context.FindMyItem("my-id");
```

### Pattern 3: New Event Subscription

```csharp
@inject IWorkspaceUpdateCoordinator updates

@code {
    protected override void OnInitialized()
    {
        updates.ArtifactsChanged += OnArtifactsChanged;
    }

    private void OnArtifactsChanged(object? sender, EventArgs e)
    {
        // React to workspace changes
    }
}
```

---

## Testing Extensions

### Unit Test Template

```csharp
[Fact]
public void MySemanticModelShouldBuildCorrectly()
{
    // Arrange
    var document = new MyDocument { /* ... */ };

    // Act
    var model = MyAnalysisService.BuildSemanticModel(document);

    // Assert
    Assert.NotNull(model);
    Assert.NotEmpty(model.Items);
}
```

### Integration Test Template

```csharp
[Fact]
public async Task MyAnalysisPageShouldDisplayResults()
{
    // Arrange: Setup ReviewContext
    var reviewContext = ReviewContextFactory.Create(/* ... */);

    // Act: Run analysis
    var result = new MyAnalysisService().Analyze(reviewContext);

    // Assert
    Assert.NotNull(result);
}
```

---

## Performance Considerations

### ReviewContext Rebuild Time
- Typical: <100ms
- Large specs: 100-500ms
- Never blocks UI (async)

### When Adding Semantic Models
- Keep parsing fast (no network calls)
- Cache parsed results in service
- Make BuildSemanticModel() deterministic

### Database Queries
- Use eager loading (avoid N+1)
- Index on (WorkspaceId, Type)
- Filter at database layer

---

## Common Pitfalls

### ❌ Don't: Cache ReviewContext in a Page

```csharp
// WRONG
private ReviewContext? _context;

protected override void OnInitialized()
{
    _context = ReviewContextProvider.GetCurrent();
    // _context goes stale on next artifact change!
}
```

### ✓ Do: Call GetCurrent() When Needed

```csharp
// RIGHT
protected override void OnInitialized()
{
    var context = ReviewContextProvider.GetCurrent();
    // Use context immediately, don't cache
}
```

### ❌ Don't: Build ReviewContext in a Page

```csharp
// WRONG
var tempContext = ReviewContextFactory.Create(/*...*/);
```

### ✓ Do: Use ReviewContextProvider

```csharp
// RIGHT
var context = ReviewContextProvider.GetCurrent();
```

### ❌ Don't: Parse Markdown Directly

```csharp
// WRONG (in a page)
var model = MyAnalysisService.BuildSemanticModel(
    MyParser.Parse(artifact.Text)
);
```

### ✓ Do: Let ReviewContextProvider Handle It

```csharp
// RIGHT (in ReviewContextProvider only)
var model = MyAnalysisService.BuildSemanticModel(
    MyParser.Parse(artifact.Text)
);
```

---

## Debugging Checklist

- [ ] ReviewContextProvider is injected (not ReviewContextFactory)
- [ ] ReviewContext.GetCurrent() returns non-null
- [ ] Artifacts are loaded (check WorkspaceArtifactRepository)
- [ ] Semantic models are non-empty
- [ ] Events are firing in correct order
- [ ] No ReviewContext caching in pages
- [ ] No duplicate parsing logic

---

## Questions & Support

When extending BirkNext:

1. **"Where should I add X?"**
   - Analysis logic → Analysis service
   - Semantic parsing → Analysis service
   - UI rendering → Page component
   - Event coordination → WorkspaceUpdateCoordinator

2. **"Should I call ReviewContextProvider.GetCurrent()?"**
   - Yes, if you need workspace semantic state
   - No, if you're analyzing already-parsed documents

3. **"How do I react to artifact changes?"**
   - Subscribe to ReviewContextProvider.ReviewContextChanged
   - Or to WorkspaceUpdateCoordinator.ArtifactsChanged
   - Or to WorkflowReadinessService.ReadinessChanged

See [01-overview.md](01-overview.md) for architecture principles.

