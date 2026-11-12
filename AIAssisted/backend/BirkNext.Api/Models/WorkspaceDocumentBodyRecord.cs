namespace BirkNext.Api.Models;

/// <summary>
/// One document body of a saved workspace's lifecycle (an artifact revision's text), stored once by its content address: the lowercase hex
/// SHA-256 of its UTF-8 bytes, verified on upload. Immutable (a database trigger refuses updates; Local Data Reset deletes). The workspace
/// lifecycle JSON keeps only the address, so a save no longer re-sends every document of every import.
/// </summary>
public class WorkspaceDocumentBodyRecord
{
    public string Sha256 { get; set; } = "";
    public string Content { get; set; } = "";
    /// <summary>Length in characters (what the lifecycle manifest reports as <c>ContentLength</c>).</summary>
    public int Length { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
