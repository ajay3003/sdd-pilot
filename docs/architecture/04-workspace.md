# Workspace Persistence & Artifact Management

## Overview

BirkNext maintains workspace state across two layers:
1. **Frontend (In-Memory):** WorkspaceArtifactRepository
2. **Backend (Database):** WorkspacePersistenceService + AppDbContext

---

## Frontend: WorkspaceArtifactRepository

### Location
`frontend/BirkNext.Web/Services/WorkspaceArtifactRepository.cs`

### Purpose
Single in-memory store for artifacts during browser session.

### Interface

```csharp
public interface IWorkspaceArtifactRepository
{
    bool Has(WorkspaceArtifactType type);
    WorkspaceArtifact? Get(WorkspaceArtifactType type);
    IEnumerable<(WorkspaceArtifactType, WorkspaceArtifact)> GetAllArtifacts();
    
    void Set(WorkspaceArtifactType type, string text, 
        string? fileName = null, string? originalPath = null, DateTime? timestamp = null);
    void Clear(WorkspaceArtifactType type);
}
```

### Key Methods

**Set() - Add/Update Artifact**
```csharp
repository.Set(WorkspaceArtifactType.Constitution, constitutionText, 
    fileName: "constitution.md", timestamp: DateTime.UtcNow);
```
- Creates or updates artifact
- Stores metadata (filename, path, timestamp)
- Does NOT trigger events directly
- Does NOT auto-save

**Get() - Retrieve Single Artifact**
```csharp
var artifact = repository.Get(WorkspaceArtifactType.Specification);
if (artifact != null)
{
    var text = artifact.Text;
    var fileName = artifact.FileName;
}
```

**GetAllArtifacts() - Retrieve All**
```csharp
var allArtifacts = repository.GetAllArtifacts();
foreach (var (type, artifact) in allArtifacts)
{
    Console.WriteLine($"{type}: {artifact.FileName}");
}
```

### Lifecycle

1. **Session Start**
   - Repository created (singleton)
   - Empty (no artifacts)

2. **Load Sample Project**
   - Set() called 5 times (one per artifact)
   - ReviewContext rebuilt
   - Auto-save scheduled

3. **Open Saved Workspace**
   - Clear() called 5 times (remove old)
   - Set() called 5 times (load new)
   - ReviewContext rebuilt immediately

4. **Session End**
   - Repository garbage collected
   - Unsaved changes lost (auto-save should have saved them)

### Constraints

- **Lifetime:** Entire browser session
- **Size:** Limited by browser memory
- **Concurrency:** Single-threaded JavaScript (no issues)
- **Persistence:** No built-in persistence (auto-save handles it)

---

## Backend: Workspace Persistence

### Database Schema

```sql
-- Workspaces table
CREATE TABLE Workspaces (
    Id GUID PRIMARY KEY,
    UserId NVARCHAR(255) NOT NULL,
    Name NVARCHAR(255) NOT NULL,
    ProjectName NVARCHAR(255),
    Description NVARCHAR(MAX),
    CreatedAt DATETIMEOFFSET,
    UpdatedAt DATETIMEOFFSET,
    LastOpenedAt DATETIMEOFFSET,
    Version INT,
    ParserVersion NVARCHAR(50),
    ReviewContextVersion NVARCHAR(50),
    ArtifactSetHash NVARCHAR(255),
    AutoSaved BIT,
    Favorite BIT,
    FOREIGN KEY (UserId) REFERENCES AspNetUsers(Id)
);

-- WorkspaceArtifacts table
CREATE TABLE WorkspaceArtifacts (
    Id GUID PRIMARY KEY,
    WorkspaceId GUID NOT NULL,
    ArtifactType NVARCHAR(50) NOT NULL,
    FileName NVARCHAR(255),
    OriginalPath NVARCHAR(MAX),
    Content NVARCHAR(MAX),
    ContentHash NVARCHAR(255),
    Encoding NVARCHAR(50),
    ParseVersion NVARCHAR(50),
    CreatedAt DATETIMEOFFSET,
    UpdatedAt DATETIMEOFFSET,
    FOREIGN KEY (WorkspaceId) REFERENCES Workspaces(Id) ON DELETE CASCADE
);
```

### WorkspacePersistenceService

**Location:** `backend/BirkNext.Api/Services/WorkspacePersistenceService.cs`

**Key Methods:**

```csharp
// Save current workspace with artifacts
Task<SavedWorkspace> SaveCurrentAsync(string? name = null, 
    List<WorkspaceArtifactDto>? artifacts = null);

// Save as new workspace
Task<SavedWorkspace> SaveAsAsync(string name, 
    List<WorkspaceArtifactDto>? artifacts = null);

// Auto-save (creates workspace if needed)
Task<SavedWorkspace> AutoSaveAsync(string? generatedName = null, 
    List<WorkspaceArtifactDto>? artifacts = null);

// Load workspace
Task<SavedWorkspace?> LoadAsync(Guid workspaceId);

// Get current workspace state
Task<WorkspaceStateDto> GetCurrentStateAsync();

// Track current workspace
Task SetCurrentWorkspaceAsync(Guid workspaceId);
```

### Auto-Save Flow

```
WorkspaceAutoSaveService (frontend)
  ↓
1. Collect all artifacts from repository
2. Build HTTP POST request
3. POST /api/workspace-persistence/auto-save
   {
     "generatedName": "Auto_Saved_Workspace_2025_01_15_143022",
     "artifacts": [ /* 5 artifacts */ ]
   }
  ↓
WorkspacePersistenceController (backend)
  ↓
1. Deserialize request
2. Call service.AutoSaveAsync(name, artifacts)
  ↓
WorkspacePersistenceService.AutoSaveAsync()
  ↓
1. Check if workspace already exists
   - If _currentWorkspaceId is set: use existing
   - If null: query database for most recently updated workspace
2. If no workspace exists: call SaveAsAsync()
3. If workspace exists: update artifacts
4. Save to database
5. Return SavedWorkspace
  ↓
Response: 200 OK with SavedWorkspace
  ↓
Frontend: Auto-save complete
```

### Get Current Workspace State

```
GetCurrentStateAsync()
  ↓
1. Check _currentWorkspaceId (in-memory for this request)
   - If set: use it
2. If null: query database for most recently updated workspace by userId
3. Load workspace details
4. Build WorkspaceStateDto
  ↓
Return:
{
  "currentWorkspaceId": "...",
  "workspaceName": "...",
  "projectName": "...",
  "artifactCount": 5,
  "status": "AutoSaved",
  "lastSavedAt": "2025-01-15T14:30:22Z"
}
```

**Critical:** This allows approval buttons to work across requests because workspace ID is retrieved from database if not in memory.

---

## Data Flow: Save Workspace

### Scenario: User manually clicks "Save"

```
RecommendedWorkflow page
  ↓
Call: workspacePersistenceApi.SaveAsync(artifacts)
  ↓
WorkspacePersistenceController.Save()
  ↓
POST /api/workspace-persistence/save
{
  "name": "My Analysis",
  "artifacts": [ /* 5 artifacts */ ]
}
  ↓
Service.SaveCurrentAsync("My Analysis", artifacts)
  ↓
1. Check if workspace exists
2. If exists: update artifacts
3. If not: create new workspace
4. Update UpdatedAt = now
5. Save to database
  ↓
Response: 200 OK with SavedWorkspace
{
  "id": "...",
  "name": "My Analysis",
  "artifacts": [ /* 5 artifacts */ ],
  "createdAt": "...",
  "updatedAt": "..."
}
```

### Scenario: Auto-Save (3 second debounce)

```
User modifies Constitution artifact
  ↓
(3 second debounce timer)
  ↓
WorkspaceAutoSaveService timer fires
  ↓
Call: workspacePersistenceApi.AutoSaveAsync(artifacts)
  ↓
POST /api/workspace-persistence/auto-save
{
  "generatedName": "Auto_Saved_Workspace_...",
  "artifacts": [ /* 5 artifacts */ ]
}
  ↓
Service.AutoSaveAsync(name, artifacts)
  ↓
1. Check _currentWorkspaceId
   - If null: query database
2. If workspace exists: update artifacts
3. If not: create with AutoSaved = true
4. Save to database
  ↓
Response: 200 OK
  ↓
Next approval button click has valid WorkspaceId
```

---

## Data Flow: Load Workspace

### Scenario: User opens saved workspace

```
WorkspaceManager component
  ↓
User selects workspace from list
  ↓
Call: workspacePersistenceApi.GetAsync(workspaceId)
  ↓
GET /api/workspace/{workspaceId}
  ↓
Controller returns SavedWorkspaceDto
{
  "id": "...",
  "name": "...",
  "artifacts": [
    { "artifactType": "Constitution", "content": "..." },
    { "artifactType": "Specification", "content": "..." },
    ...
  ]
}
  ↓
Frontend: WorkspaceSessionRestoreService.RestoreWorkspaceAsync()
  ↓
1. Clear repository
2. Set each artifact in repository
3. Call ReviewContextProvider.RebuildAsync()
4. ReviewContext now current with loaded artifacts
  ↓
RecommendedWorkflow page
  ↓
Call: provider.GetCurrent()
  ↓
ReviewContext with loaded artifacts available
  ↓
Approval buttons can find workspace ID
```

---

## Current Workspace Tracking

### Problem
After auto-save creates a workspace, next HTTP request is a new scoped instance with no knowledge of that workspace ID.

### Solution
WorkspacePersistenceService.GetCurrentStateAsync() queries database:

```csharp
public async Task<WorkspaceStateDto> GetCurrentStateAsync()
{
    // Check in-memory (for this request)
    if (_currentWorkspaceId.HasValue)
        return GetState(_currentWorkspaceId.Value);

    // Query database for most recent by userId
    var workspace = await _context.Workspaces
        .Where(w => w.UserId == _userId)
        .OrderByDescending(w => w.UpdatedAt)
        .FirstOrDefaultAsync();

    if (workspace != null)
        return GetState(workspace.Id);

    return new WorkspaceStateDto(); // No workspace loaded
}
```

**Guarantee:** Approval buttons get valid workspace ID even across requests.

---

## Artifact Integrity

### Content Hash
Each artifact stores SHA256 hash of content for:
- Dirty state tracking
- Duplicate detection
- Change detection

### Artifact Set Hash
Workspace stores hash of all 5 artifacts together for:
- Detecting any change to artifact set
- Comparing before/after save

### Encoding
Artifacts stored as UTF-8 in database, but encoding metadata tracked for compatibility.

---

## Performance Considerations

### Query Optimization
- Load workspace with artifacts eagerly (avoid N+1)
- Index on (UserId, UpdatedAt) for GetCurrentStateAsync

### Large Artifacts
- Specification/Plan might be 100KB+ (large documents)
- Stored in NVARCHAR(MAX)
- No indexing on content (only on id, workspace id)

### Auto-Save Timing
- 3 second debounce: avoids excessive saves on rapid edits
- 30 second throttle: ensures save eventually (even if continuous edits)
- POST request includes all 5 artifacts (~few KB each)

---

## Backup Strategy

### What Gets Backed Up
- Workspace metadata
- All 5 artifacts for each workspace
- Approval state
- Change history (UpdatedAt timestamps)

### What Does NOT Get Backed Up
- ReviewContext (can be rebuilt from artifacts)
- Semantic models (can be rebuilt from artifacts)
- Session state (ephemeral)

---

## Migration & Versioning

### Semantic Versioning
Each workspace tracks:
- **ParserVersion:** Which markdown parsers were used
- **ReviewContextVersion:** Which ReviewContext schema was used

Allows future migrations to handle old workspaces.

---

## Database Constraints

### Foreign Keys
- WorkspaceArtifacts.WorkspaceId → Workspaces.Id (ON DELETE CASCADE)
  - Deleting workspace deletes all artifacts

### Indexes
- Workspaces(UserId, UpdatedAt)
- WorkspaceArtifacts(WorkspaceId, ArtifactType)

### Unique Constraints
- None (multiple SavedWorkspace objects with same name allowed)

---

## Related Services

See also:
- [06-autosave.md](06-autosave.md) - Auto-save mechanism
- [05-workflow.md](05-workflow.md) - Approval workflow

