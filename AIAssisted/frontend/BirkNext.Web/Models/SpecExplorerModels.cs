namespace BirkNext.Web.Models;

public enum SpecNodeType
{
    Module,            // H1
    Section,           // H2
    SubSection,        // H3
    DeepSection,       // H4+
    Requirement,       // FR / NFR / REQ
    UserStory,         // US / UC
    SuccessCriterion,  // SC
    AcceptanceTest,    // AC / TS
    Clarification,     // detected clarification
    Entity,            // CamelCase / domain term
    DomainItem,        // Assumption / Dependency / Event / Operation / API
    TableSection,      // markdown table block
    TableRow,          // one data row of a table
}

public enum TableType
{
    Generic,
    RequirementMap,
    UserStoryMap,
    TestMapping,
    Traceability,
    EntityModel,
    ApiSpec,
    DependencyMap,
}

public enum CoverageState { Unknown, Covered, Partial, Missing }

public sealed class SpecNode
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..10];
    public required string Title { get; init; }
    public required SpecNodeType NodeType { get; init; }
    public int HeadingLevel { get; init; }     // 1–6 for headings, 0 for items
    public string? SpecItemId { get; init; }   // FR-001, SC-003, US-05 etc.
    public string Excerpt { get; set; } = string.Empty;
    public List<SpecNode> Children { get; } = [];
    public CoverageState Coverage { get; set; } = CoverageState.Unknown;

    // Table-specific fields (populated on TableSection / TableRow nodes)
    public TableType TableKind { get; init; } = TableType.Generic;
    public List<string> ColumnHeaders { get; init; } = [];
    public List<string> CellValues { get; init; } = [];
    public List<string> LinkedSpecItemIds { get; init; } = [];

    // Descendant counts — populated by SpecExplorerService after tree build
    public int ReqCount { get; set; }
    public int UserStoryCount { get; set; }
    public int TestCount { get; set; }
    public int ClarCount { get; set; }
    public int ScCount { get; set; }
    public int TotalDescendants { get; set; }
}

public sealed class SpecHealth
{
    public int TotalHeadings { get; init; }
    public int Requirements { get; init; }
    public int UserStories { get; init; }
    public int Tests { get; init; }
    public int Clarifications { get; init; }
    public int SuccessCriteria { get; init; }
    public int Entities { get; init; }
    public int DomainItems { get; init; }
    public int TablesDetected { get; init; }
    public int TotalItems => Requirements + UserStories + Tests + Clarifications + SuccessCriteria + Entities + DomainItems;
}

public sealed class SpecTree
{
    public List<SpecNode> Roots { get; init; } = [];
    public SpecHealth Health { get; init; } = new();
}
