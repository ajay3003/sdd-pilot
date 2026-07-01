# Quickstart: Access Administration Panel

**Feature branch**: `005-access-admin-panel`

---

## Running the Application

```bash
# Build
dotnet build M2LB.Frontend.slnx

# Run in dev mode (no backend required)
dotnet run --project M2LB.Frontend.Web

# Run all tests
dotnet test M2LB.Frontend.slnx
```

---

## Enabling Admin Screens in Dev Mode

The admin module is access-gated. In dev mode (`UseDevAuth=true`), authentication is handled by `DevAuthStateProvider` and access rights are controlled via `OperationOverrideService`.

To see the admin navigation items:

1. Start the app with `UseDevAuth=true, UseMockData=true` (default in `appsettings.Development.json`)
2. Open the developer settings panel (gear icon in the nav bar)
3. Enable the `Autorisasjonstjeneste:` operations you want to test

Without these operations enabled, the admin navigation items will not appear (fail-closed, Constitution III). This is intentional — it validates the access-gating logic in development.

**Operations to enable per screen**:
| Screen | Minimum required |
|--------|-----------------|
| Operation Catalogue | `Autorisasjonstjeneste:LesOperasjonskatalog` |
| General Roles | `Autorisasjonstjeneste:LesGenerelleRoller` |
| Child-Specific Roles | `Autorisasjonstjeneste:LesBarnespesifikkeRoller` |
| User Access | `Autorisasjonstjeneste:LesBrukertilgang` |
| Emergency Access | `Autorisasjonstjeneste:LesNødtilgang` |
| Audit Log | `Autorisasjonstjeneste:LesRevisjonslogg` |

---

## Adding Mock Service Implementations

When `UseMockData=true`, each of the 7 new service interfaces needs a mock implementation. Follow the pattern in `M2LB.Frontend.Web/Modules/Person/Services/Mocks/MockPersonService.cs`:

```csharp
// M2LB.Frontend.Web/Modules/Admin/Services/Mocks/MockOperationService.cs
public class MockOperationService : IOperationService
{
    public Task<Result<IReadOnlyList<Operation>>> GetOperationsAsync()
        => Task.FromResult(Result<IReadOnlyList<Operation>>.Success(SeedData.Operations));

    // ... other methods return Result.Success with seed data
}
```

Register mock implementations in `Program.cs` inside the `if (useMockData)` block.

---

## Writing bUnit Tests

Tests live in `M2LB.Frontend.Tests/Modules/Admin/`. Each file covers one page component.

**Test class setup**:
```csharp
public class OperationCataloguePageTests : BunitContext
{
    private readonly Mock<IOperationService> _operationService = new();
    private readonly Mock<IAdminBadgeService> _badgeService = new();

    public OperationCataloguePageTests()
    {
        _operationService
            .Setup(s => s.GetOperationsAsync())
            .ReturnsAsync(Result<IReadOnlyList<Operation>>.Success(TestData.Operations));

        Services.AddSingleton(_operationService.Object);
        Services.AddSingleton(_badgeService.Object);
        // Add NotificationService (needed even if not asserted)
        Services.AddSingleton<NotificationService>();
    }
}
```

**Rendering with authorization**:
```csharp
[Fact]
public async Task ShowsOperationList_WhenUserHasReadAccess()
{
    var userId = "test-oid";
    AddAuthorization()
        .SetAuthorized(userId)
        .SetClaims(new Claim("oid", userId));

    var cut = Render<OperationCataloguePage>();
    await cut.WaitForStateAsync(
        () => cut.Markup.Contains("LesPersonopplysninger"),
        TimeSpan.FromSeconds(3));

    cut.Markup.Should().Contain("LesPersonopplysninger");
}
```

**Test ID convention**: Tag each test method with a comment `// T[NNN]` matching the test case number from SC-009. This links test code to the specification.

---

## Key Files Changed in This Feature

| Change | File |
|--------|------|
| Admin sub-menu + badge counters | `M2LB.Frontend.Web/Layout/NavMenu.razor` |
| New service registrations (7 services + badge service) | `M2LB.Frontend.Web/Program.cs` |
| Shared reusable confirm dialog | `M2LB.Frontend.Web/Shared/Components/ConfirmDialog.razor` |
| Badge counter service interface + implementation | `M2LB.Frontend.Web/Shared/Services/IAdminBadgeService.cs` |
| All six page components | `M2LB.Frontend.Web/Modules/Admin/Pages/` |
| All 15 reusable module components | `M2LB.Frontend.Web/Modules/Admin/Components/` |
| All 7 service interfaces + implementations | `M2LB.Frontend.Web/Modules/Admin/Services/` |
| All 7 mock service implementations | `M2LB.Frontend.Web/Modules/Admin/Services/Mocks/` |
| All 47 bUnit tests (6 test files) | `M2LB.Frontend.Tests/Modules/Admin/` |

---

## Constitution Compliance Checklist

Before submitting a PR, verify:

- [ ] All service calls return `Result<T>` — no exceptions thrown to callers
- [ ] No `HttpClient` used directly in any component
- [ ] `NavMenu` admin items hidden for users without the corresponding `Autorisasjonstjeneste:` operation
- [ ] `GisVedNødtilgang` toggle state only updates after confirmed `200 OK` (no optimistic update)
- [ ] No PII (child identity, national IDs) in URLs, page titles, or browser history
- [ ] All confirmation dialogs stay open on API error and show inline error message
- [ ] 47 bUnit tests pass: `dotnet test M2LB.Frontend.slnx`
- [ ] All UI text is in Norwegian; all code, comments, and documentation in English
