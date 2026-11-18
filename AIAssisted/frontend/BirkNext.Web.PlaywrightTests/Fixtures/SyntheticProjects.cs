using System.IO.Compression;
using System.Text;
using Microsoft.Playwright;

namespace BirkNext.Web.PlaywrightTests.Fixtures;

/// <summary>
/// Each browser test establishes its own project through the supported user path — Import Project → Show the package's documents — instead of
/// relying on whatever project happens to be current, on Demo Projects (an optional, separately configured feature) or on another test.
/// The archive is synthetic (a small Spec Kit feature) and uniquely named, so tests never share or overwrite each other's project.
/// </summary>
public static class SyntheticProjects
{
    /// <summary>A unique project name for one test (letters and digits only, as a folder name).</summary>
    public static string NewName(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    /// <summary>A Spec Kit project: one feature with a specification (two requirements), a plan, tasks and optionally a data model — no source code, no secrets.</summary>
    public static byte[] SpecificationArchive(string name, bool withDataModel = false)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string text)
            {
                using var writer = new StreamWriter(zip.CreateEntry($"{name}/{path}").Open(), new UTF8Encoding(false));
                writer.Write(text);
            }
            Add("specs/001-library-loans/spec.md", $"""
                # Feature Specification: Library loans ({name})

                ## User Scenarios & Testing

                ### User Story 1 - Register a loan (Priority: P1)

                A librarian registers a loan for a valid book number and member card.

                ## Requirements

                ### Functional Requirements

                - **FR-001**: The system MUST register a loan for a valid book number and member card.
                - **FR-002**: The system MUST list overdue loans for a member.
                """);
            Add("specs/001-library-loans/plan.md", """
                # Implementation Plan: Library loans

                **Language/Version**: C# / .NET 8 · **Primary Dependencies**: ASP.NET Core, Blazor WebAssembly · **Storage**: PostgreSQL
                """);
            Add("specs/001-library-loans/tasks.md", """
                # Tasks: Library loans

                - [x] T001 [US1] Register loan endpoint (FR-001)
                - [ ] T002 [US1] Overdue loan list (FR-002)
                """);
            // A data model whose Data Model Quality findings are known from its rules: five columns have no type, "email" is sensitive without a
            // classification and "member_id" looks like a foreign key with no relationship — seven warnings.
            if (withDataModel) Add("specs/001-library-loans/data-model.md", """
                # Data Model: Library loans

                ## Table: member

                | Column | Type | Nullable | Description |
                |--------|------|----------|-------------|
                | id | uuid | no | Primary key |
                | name |  | no | Display name |
                | email | string | no | Contact address |
                | phone |  | yes | Contact phone |
                | card_number |  | no | Library card |

                ## Table: loan

                | Column | Type | Nullable | Description |
                |--------|------|----------|-------------|
                | id | uuid | no | Primary key |
                | member_id | uuid | no | Borrowing member |
                | book_number |  | no | Catalogue number |
                | due_date |  | no | Return date |
                """);
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// Imports <paramref name="archive"/> as <paramref name="name"/>, makes it the current project and confirms it after a reload (the selection is
    /// persisted by the server, not only held by this tab). Returns when <c>Current import</c> names exactly this project.
    /// </summary>
    public static async Task ImportAndUseAsync(IPage page, string name, byte[] archive)
    {
        var frontend = BrowserTestServers.FrontendUrl;
        await page.GotoAsync($"{frontend}/project-import?import=source", new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
        var chooser = page.GetByTestId("pi-choose-file");
        await chooser.WaitForAsync(new() { State = WaitForSelectorState.Attached, Timeout = 120000 });
        await chooser.SetInputFilesAsync(new FilePayload { Name = $"{name}.zip", MimeType = "application/zip", Buffer = archive });
        await page.GetByTestId("pi-preview").WaitForAsync(new() { Timeout = 120000 });
        var importAgain = page.GetByTestId("pi-duplicate-import-again");
        if (await importAgain.CountAsync() > 0) await importAgain.ClickAsync();
        await page.GetByTestId("pi-import").ClickAsync();
        var result = page.GetByTestId("pi-result");
        await result.WaitForAsync(new() { Timeout = 300000 });
        var outcome = await result.GetAttributeAsync("data-outcome");
        if (outcome is "failed" or "rejected")
            throw new InvalidOperationException($"Synthetic project {name} was not imported: {await result.InnerTextAsync()}");

        await page.GotoAsync($"{frontend}/project-import", new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
        var use = page.Locator($"[data-testid=pi-previous-use][aria-label=\"Show {name}'s documents\"]");
        await use.WaitForAsync(new() { Timeout = 120000 });
        await use.ClickAsync();
        await Assertions.Expect(page.GetByTestId("pi-current-name")).ToHaveTextAsync(name, new() { Timeout = 60000 });

        await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Assertions.Expect(page.GetByTestId("pi-current-name")).ToHaveTextAsync(name, new() { Timeout = 120000 });
    }
}
