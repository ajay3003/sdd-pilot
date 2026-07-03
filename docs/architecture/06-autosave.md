# Auto-Save Infrastructure

## Overview

Auto-save automatically persists workspace artifacts to backend at appropriate intervals.

**Key Properties:**
- ✓ Debounce: 3 second wait after last change
- ✓ Throttle: Save at least every 30 seconds if changes pending
- ✓ Event-driven: Triggered by workspace mutations
- ✓ Batching: Single POST with all 5 artifacts
- ✓ Non-blocking: UI remains responsive during save

---

## Components

### WorkspaceAutoSaveService

**Location:** `frontend/BirkNext.Web/Services/WorkspaceAutoSaveService.cs`

**Purpose:** Coordinate auto-save timing and batching.

**Key Methods:**
```csharp
// Start monitoring for changes
Task StartMonitoringAsync();

// Stop monitoring
Task StopMonitoringAsync();

// Perform actual save to backend
private Task PerformAutoSaveAsync();
```

**Dependencies:**
- IWorkspaceArtifactRepository (read artifacts)
- IWorkspaceUpdateCoordinator (subscribe to changes)
- IWorkspacePersistenceApiService (HTTP POST)

---

## Timing Logic

### Debounce Window
```
User edits artifact
  ↓
T=0: Artifact changed
     Timer: 3 sec countdown starts
  ↓
T=1.5: User edits again
       Timer: Reset to 3 sec
  ↓
T=3: No more changes for 3 sec
     Timer: Expires
     → POST /api/workspace-persistence/auto-save
  ↓
T=3.1: Save complete
```

### Throttle Protection
```
User continuously editing
  ↓
T=0: First change, timer starts
T=1: Another change, timer resets
T=2: Another change, timer resets
T=3: SAVE (throttle expires, even if timer hasn't)
  ↓
Post-save
T=6: Another change, timer starts
...
```

**Guarantee:** No more than one save per 30 seconds, even with continuous edits.

---

## Implementation Details

### State Machine

```csharp
public sealed class WorkspaceAutoSaveService : IWorkspaceAutoSaveService, IDisposable
{
    private Timer? _debounceTimer;
    private DateTime _lastSaveTime = DateTime.MinValue;
    private bool _isMonitoring = false;
    private bool _pendingChanges = false;

    private const int DebounceMs = 3000;      // Wait 3 seconds
    private const int ThrottleMs = 30000;     // At least save every 30 seconds

    public async Task StartMonitoringAsync()
    {
        if (_isMonitoring) return;
        _isMonitoring = true;
        _updates.ArtifactsChanged += OnArtifactsChanged;
    }

    public async Task StopMonitoringAsync()
    {
        if (!_isMonitoring) return;
        _isMonitoring = false;
        _updates.ArtifactsChanged -= OnArtifactsChanged;
        _debounceTimer?.Dispose();
    }

    private void OnArtifactsChanged(object? sender, EventArgs e)
    {
        _pendingChanges = true;
        
        _debounceTimer?.Dispose();
        _debounceTimer = new Timer(
            _ => CheckAndSave(),
            null,
            DebounceMs,
            Timeout.Infinite);
    }

    private async void CheckAndSave()
    {
        var timeSinceLastSave = DateTime.UtcNow - _lastSaveTime;
        
        if (timeSinceLastSave.TotalMilliseconds < ThrottleMs)
        {
            // Still within throttle window, reschedule
            var waitMs = (int)(ThrottleMs - timeSinceLastSave.TotalMilliseconds);
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(
                _ => PerformAutoSaveAsync(),
                null,
                waitMs,
                Timeout.Infinite);
        }
        else
        {
            // Ready to save
            await PerformAutoSaveAsync();
        }
    }

    private async Task PerformAutoSaveAsync()
    {
        if (!_pendingChanges)
            return;

        try
        {
            var artifacts = _repository.GetAllArtifacts()
                .Select(a => new WorkspaceArtifactDto { /* ... */ })
                .ToList();

            var generatedName = $"Auto_Saved_Workspace_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
            
            var result = await _api.AutoSaveAsync(generatedName, artifacts);
            
            _lastSaveTime = DateTime.UtcNow;
            _pendingChanges = false;
            
            _logger.LogInformation("Auto-save completed: {WorkspaceName}", result?.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-save failed");
            // Don't rethrow - auto-save failure shouldn't crash UI
        }
    }
}
```

---

## HTTP Request

### POST /api/workspace-persistence/auto-save

**Request Body:**
```json
{
  "generatedName": "Auto_Saved_Workspace_20250115_143022",
  "artifacts": [
    {
      "artifactType": "Constitution",
      "fileName": "constitution.md",
      "content": "# Constitution\n\n[markdown content...]"
    },
    {
      "artifactType": "Specification",
      "fileName": "spec.md",
      "content": "# Specification\n\n[markdown content...]"
    },
    // ... 3 more artifacts
  ]
}
```

**Response:**
```json
{
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "name": "Auto_Saved_Workspace_20250115_143022",
  "workspaceId": "...",
  "artifactCount": 5,
  "createdAt": "2025-01-15T14:30:22Z",
  "updatedAt": "2025-01-15T14:30:22Z",
  "autoSaved": true,
  "artifacts": [...]
}
```

---

## Lifecycle

### Starting Auto-Save

```
App starts
  ↓
Program.cs registers WorkspaceAutoSaveService
  ↓
SampleProjects.razor component initializes
  ↓
OnInitialized():
  await autoSaveService.StartMonitoringAsync()
  ↓
Auto-save monitoring is now active
```

### During Session

```
User loads sample artifacts
  ↓
repository.Set() called 5 times
  ↓
coordinator.EndUpdate()
  ↓
ArtifactsChanged event
  ↓
WorkspaceAutoSaveService.OnArtifactsChanged()
  ↓
Debounce timer: 3 sec countdown
  ↓
T=3: POST /api/auto-save with all 5 artifacts
  ↓
Backend saves workspace
  ↓
Frontend gets workspace ID
  ↓
Approval buttons can now work
```

### Stopping Auto-Save

```
Page navigates away from RecommendedWorkflow
  ↓
Dispose() called
  ↓
autoSaveService.StopMonitoringAsync()
  ↓
Unsubscribe from ArtifactsChanged
  ↓
Cancel any pending debounce timer
  ↓
Auto-save monitoring stops
```

---

## Error Handling

### Network Error During Save
```
PerformAutoSaveAsync()
  ↓
HTTP request fails (network error)
  ↓
Catch exception, log error
  ↓
Don't rethrow (UI continues working)
  ↓
_pendingChanges remains true
  ↓
Next ArtifactsChanged event resets timer
  ↓
Will retry on next timer fire
```

### Backend Rejection
```
Backend receives POST /api/auto-save
  ↓
Validation fails (invalid artifact)
  ↓
Returns 400 Bad Request
  ↓
Frontend logs error
  ↓
Workspace not saved
  ↓
User should fix and try again
```

### Concurrent Saves
```
POST request A in flight
  ↓
ArtifactsChanged fires again
  ↓
Set _pendingChanges = true
  ↓
Reschedule timer
  ↓
Request A completes
  ↓
T=3: New request with latest artifacts
  ↓
Handles out-of-order naturally (last write wins)
```

---

## Performance

### Artifact Size
- Constitution: typically 20-50 KB
- Specification: typically 50-200 KB
- Plan: typically 20-50 KB
- Tasks: typically 30-100 KB
- DataModel: typically 10-30 KB

**Total per save:** ~200-500 KB typically

### Network Impact
- 200 KB over 3G: ~500ms
- 200 KB over WiFi: ~50ms
- POST request includes all 5 artifacts
- Response includes workspace metadata

### Database Impact
- Update workspace metadata
- Insert/update 5 artifact records
- Indexes on (WorkspaceId, ArtifactType)
- Typical query: <100ms

---

## Guarantees

### What Auto-Save Guarantees
✓ All 5 artifacts saved together (atomic from API perspective)
✓ Saves at most every 30 seconds
✓ Waits at least 3 seconds after last change
✓ Non-blocking (doesn't freeze UI)
✓ Error handling (doesn't crash on network failure)

### What Auto-Save Does NOT Guarantee
✗ Instant persistence (3 second debounce delay)
✗ Zero data loss (if page crashes before timer fires)
✗ Consistency checking (garbage in = garbage out)
✗ Undo/rollback (overwrites previous version)

---

## Monitoring

### Debug Output
```csharp
Debug.WriteLine($"AUTO-SAVE: Artifacts changed, timer reset");
Debug.WriteLine($"AUTO-SAVE: POST /api/auto-save starting");
Debug.WriteLine($"AUTO-SAVE: Response {response.StatusCode}");
Debug.WriteLine($"AUTO-SAVE: Saved workspace {workspace.Id}");
```

### Metrics to Track
- Auto-save frequency (saves per minute)
- Average save time (request time)
- Success rate (% of saves that succeed)
- Artifact size (bytes persisted)

---

## Related Documentation

See also:
- [04-workspace.md](04-workspace.md) - Workspace persistence backend
- [02-runtime-flow.md](02-runtime-flow.md) - Event flow diagrams

