namespace BirkNext.Web.Models;

public enum SpecNodeType
{
    Module,            // H1
    Section,           // H2
    SubSection,        // H3
    DeepSection,       // H4+
    Requirement,       // FR / NFR / REQ
    UserStory,         // US / UC or "User Story N" heading
    SuccessCriterion,  // SC
    AcceptanceTest,    // AC / TS (inline pattern)
    BddScenario,       // Scenario N + Given/When/Then block
    Clarification,     // detected clarification (inline fallback)
    QaPair,            // Q: ... A: ... paired clarification
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

public enum SectionSemantics
{
    Generic,
    Clarifications,
    EdgeCases,
    Assumptions,
    ApiSurface,
    Observability,
    Security,
    Performance,
    UserStory,
    AcceptanceScenarios,
    RequirementsSection,
    SuccessCriteriaSection,
}

public enum CoverageState { Unknown, Covered, Partial, Missing }

public sealed class SpecNode
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..10];
    public required string Title { get; init; }
    public required SpecNodeType NodeType { get; init; }
    public int HeadingLevel { get; init; }        // 1–6 for headings, 0 for items
    public string? SpecItemId { get; init; }      // FR-001, SC-003, US-05 etc.
    public string Excerpt { get; set; } = string.Empty;
    public string? FullContent { get; set; }      // complete accumulated content, no truncation
    public SectionSemantics Semantics { get; init; } = SectionSemantics.Generic;
    public List<SpecNode> Children { get; } = [];
    public CoverageState Coverage { get; set; } = CoverageState.Unknown;

    // Q/A pair fields (QaPair nodes)
    public string? QuestionText { get; init; }
    public string? AnswerText { get; init; }

    // BDD scenario fields (BddScenario nodes)
    public string? BddGiven { get; init; }
    public string? BddWhen { get; init; }
    public string? BddThen { get; init; }

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
    public int BddScenarios { get; init; }
    public int Clarifications { get; init; }
    public int SuccessCriteria { get; init; }
    public int Entities { get; init; }
    public int DomainItems { get; init; }
    public int TablesDetected { get; init; }
    public int TotalItems => Requirements + UserStories + Tests + BddScenarios + Clarifications + SuccessCriteria + Entities + DomainItems;
}

public sealed class SpecTree
{
    public List<SpecNode> Roots { get; init; } = [];
    public SpecHealth Health { get; init; } = new();
}
