namespace BirkNext.Web.Services;

/// <summary>
/// The page reads no workspace document content (specifications, plans, tasks, imported documents): it renders without loading the bodies a
/// bootstrap manifest deferred. Anything it reaches through the explorers still loads them on demand.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class WorkspaceDocumentsNotReadAttribute : Attribute;
