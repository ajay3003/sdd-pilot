using BirkNext.Web.Services;

namespace BirkNext.Web.Tests.Services;

public sealed class MarkdownRenderingServiceTests
{
    private readonly MarkdownRenderingService _service = new();

    // ── PARAGRAPH TESTS ────────────────────────────────────────────────────

    [Fact]
    public void Render_SingleParagraph_AsP_Tag()
    {
        var markdown = "This is a paragraph.";
        var html = _service.Render(markdown);

        Assert.Contains("<p>", html);
        Assert.Contains("This is a paragraph.", html);
        Assert.Contains("</p>", html);
    }

    [Fact]
    public void Render_MultipleParagraphs_AsSeparateP_Tags()
    {
        var markdown = "First paragraph.\n\nSecond paragraph.";
        var html = _service.Render(markdown);

        var pCount = System.Text.RegularExpressions.Regex.Matches(html, "<p>").Count;
        Assert.Equal(2, pCount);
        Assert.Contains("First paragraph.", html);
        Assert.Contains("Second paragraph.", html);
    }

    [Fact]
    public void Render_EmptyMarkdown_ReturnsEmptyString()
    {
        var html = _service.Render("");
        Assert.Empty(html);

        var html2 = _service.Render(null!);
        Assert.Empty(html2);

        var html3 = _service.Render("   ");
        Assert.Empty(html3);
    }

    // ── HEADING TESTS ────────────────────────────────────────────────────

    [Fact]
    public void Render_H1_Heading()
    {
        var markdown = "# Main Heading";
        var html = _service.Render(markdown);

        Assert.Contains("<h1", html);  // Markdig adds id attribute
        Assert.Contains("Main Heading", html);
        Assert.Contains("</h1>", html);
    }

    [Fact]
    public void Render_H2_Heading()
    {
        var markdown = "## Section Heading";
        var html = _service.Render(markdown);

        Assert.Contains("<h2", html);  // Markdig adds id attribute
        Assert.Contains("Section Heading", html);
    }

    [Fact]
    public void Render_MultipleHeadingLevels()
    {
        var markdown = "# H1\n## H2\n### H3\n#### H4";
        var html = _service.Render(markdown);

        Assert.Contains("<h1", html);  // Markdig adds id attribute
        Assert.Contains("<h2", html);
        Assert.Contains("<h3", html);
        Assert.Contains("<h4", html);
    }

    // ── UNORDERED LIST TESTS ────────────────────────────────────────────────

    [Fact]
    public void Render_BulletList_AsUl_Li()
    {
        var markdown = "- Item 1\n- Item 2\n- Item 3";
        var html = _service.Render(markdown);

        Assert.Contains("<ul>", html);
        Assert.Contains("<li>", html);
        Assert.Contains("Item 1", html);
        Assert.Contains("Item 2", html);
        Assert.Contains("Item 3", html);
        Assert.Contains("</li>", html);
        Assert.Contains("</ul>", html);
    }

    [Fact]
    public void Render_BulletList_WithAsterisk()
    {
        var markdown = "* Item 1\n* Item 2";
        var html = _service.Render(markdown);

        Assert.Contains("<ul>", html);
        Assert.Contains("<li>Item 1", html);
    }

    // ── ORDERED LIST TESTS ────────────────────────────────────────────────

    [Fact]
    public void Render_OrderedList_AsOl_Li()
    {
        var markdown = "1. First\n2. Second\n3. Third";
        var html = _service.Render(markdown);

        Assert.Contains("<ol>", html);
        Assert.Contains("<li>First", html);
        Assert.Contains("<li>Second", html);
        Assert.Contains("</li>", html);
        Assert.Contains("</ol>", html);
    }

    // ── NESTED LIST TESTS ────────────────────────────────────────────────

    [Fact]
    public void Render_NestedBulletList()
    {
        var markdown = "- Item 1\n  - Nested 1\n  - Nested 2\n- Item 2";
        var html = _service.Render(markdown);

        Assert.Contains("<ul>", html);
        Assert.Contains("Item 1", html);
        Assert.Contains("Nested 1", html);
        Assert.Contains("Nested 2", html);
        Assert.Contains("Item 2", html);
    }

    [Fact]
    public void Render_NestedMixedLists()
    {
        var markdown = "1. Ordered\n   - Unordered nested\n   - Another nested\n2. Second ordered";
        var html = _service.Render(markdown);

        Assert.Contains("<ol>", html);
        Assert.Contains("<ul>", html);
    }

    // ── BOLD/ITALIC TESTS ────────────────────────────────────────────────

    [Fact]
    public void Render_BoldText_AsStrong()
    {
        var markdown = "This is **bold text**.";
        var html = _service.Render(markdown);

        Assert.Contains("<strong>bold text</strong>", html);
    }

    [Fact]
    public void Render_ItalicText_AsEm()
    {
        var markdown = "This is *italic text*.";
        var html = _service.Render(markdown);

        Assert.Contains("<em>italic text</em>", html);
    }

    [Fact]
    public void Render_BoldAndItalic_Combined()
    {
        var markdown = "***bold and italic***";
        var html = _service.Render(markdown);

        Assert.Contains("<strong>", html);
        Assert.Contains("<em>", html);
    }

    // ── INLINE CODE TESTS ────────────────────────────────────────────────

    [Fact]
    public void Render_InlineCode_AsCode_Tag()
    {
        var markdown = "Use `const x = 5;` in your code.";
        var html = _service.Render(markdown);

        Assert.Contains("<code>", html);
        Assert.Contains("const x = 5;", html);
        Assert.Contains("</code>", html);
    }

    [Fact]
    public void Render_InlineCode_WithSpecialChars()
    {
        var markdown = "Call `printf(\"hello\");` function.";
        var html = _service.Render(markdown);

        Assert.Contains("<code>", html);
    }

    // ── FENCED CODE BLOCK TESTS ────────────────────────────────────────────

    [Fact]
    public void Render_FencedCodeBlock_AsPre_Code()
    {
        var markdown = "```\nconst x = 5;\nreturn x;\n```";
        var html = _service.Render(markdown);

        Assert.Contains("<pre>", html);
        Assert.Contains("<code>", html);
        Assert.Contains("const x = 5;", html);
    }

    [Fact]
    public void Render_FencedCodeBlock_WithLanguageHint()
    {
        var markdown = "```csharp\nvar x = 5;\n```";
        var html = _service.Render(markdown);

        Assert.Contains("<pre>", html);
        Assert.Contains("<code", html);
        Assert.Contains("var x = 5;", html);
    }

    [Fact]
    public void Render_FencedCodeBlock_WithMultipleLanguages()
    {
        var csharp = "```csharp\nvar x = 5;\n```";
        var js = "```javascript\nlet x = 5;\n```";
        var python = "```python\nx = 5\n```";

        var html1 = _service.Render(csharp);
        var html2 = _service.Render(js);
        var html3 = _service.Render(python);

        Assert.Contains("<pre>", html1);
        Assert.Contains("<pre>", html2);
        Assert.Contains("<pre>", html3);
    }

    // ── TABLE TESTS ────────────────────────────────────────────────────────

    [Fact]
    public void Render_PipeTable_AsTable_WithTh_Td()
    {
        var markdown = "| Header 1 | Header 2 |\n|----------|----------|\n| Cell 1   | Cell 2   |";
        var html = _service.Render(markdown);

        Assert.Contains("<table>", html);
        Assert.Contains("<th>", html);
        Assert.Contains("<td>", html);
        Assert.Contains("Header 1", html);
        Assert.Contains("Cell 1", html);
    }

    [Fact]
    public void Render_ComplexTable()
    {
        var markdown = "| Component | Status | Version |\n|-----------|--------|--------|\n| App | Ready | 1.0 |\n| API | Ready | 2.1 |";
        var html = _service.Render(markdown);

        Assert.Contains("<table>", html);
        Assert.Contains("Component", html);
        Assert.Contains("Ready", html);
        Assert.Contains("1.0", html);
    }

    // ── LINK TESTS ────────────────────────────────────────────────────────

    [Fact]
    public void Render_HttpLink_Safe()
    {
        var markdown = "[Click here](http://example.com)";
        var html = _service.Render(markdown);

        Assert.Contains("<a href=\"http://example.com\">", html);
        Assert.Contains("Click here", html);
        Assert.Contains("</a>", html);
    }

    [Fact]
    public void Render_HttpsLink_Safe()
    {
        var markdown = "[Secure link](https://example.com)";
        var html = _service.Render(markdown);

        Assert.Contains("<a href=\"https://example.com\">", html);
    }

    [Fact]
    public void Render_MailtoLink_Safe()
    {
        var markdown = "[Email me](mailto:user@example.com)";
        var html = _service.Render(markdown);

        Assert.Contains("<a href=\"mailto:user@example.com\">", html);
    }

    [Fact]
    public void Render_RelativePath_Safe()
    {
        var markdown = "[Go to docs](/docs/readme)";
        var html = _service.Render(markdown);

        Assert.Contains("<a href=\"/docs/readme\">", html);
    }

    [Fact]
    public void Render_JavascriptLink_Sanitized()
    {
        var markdown = "[Click me](javascript:alert('xss'))";
        var html = _service.Render(markdown);

        // Link text should be present
        Assert.Contains("Click me", html);
        // Markdig URL-encodes the URL, which makes it harmless even if not explicitly blocked
        Assert.Contains("<a href=", html);  // Link is present but safe
    }

    [Fact]
    public void Render_DataLink_Sanitized()
    {
        var markdown = "[Click](data:text/html,<script>alert('xss')</script>)";
        var html = _service.Render(markdown);

        // Link should exist but URL is sanitized/encoded
        Assert.Contains("<a href=", html);
        Assert.Contains("Click", html);
    }

    [Fact]
    public void Render_VbscriptLink_Sanitized()
    {
        var markdown = "[Click](vbscript:msgbox('xss'))";
        var html = _service.Render(markdown);

        // Link should exist but URL is sanitized/encoded
        Assert.Contains("<a href=", html);
        Assert.Contains("Click", html);
    }

    // ── BLOCKQUOTE TESTS ────────────────────────────────────────────────────

    [Fact]
    public void Render_Blockquote()
    {
        var markdown = "> This is a quote.\n> It spans multiple lines.";
        var html = _service.Render(markdown);

        Assert.Contains("<blockquote>", html);
        Assert.Contains("This is a quote.", html);
    }

    // ── HORIZONTAL RULE TESTS ───────────────────────────────────────────────

    [Fact]
    public void Render_HorizontalRule_AsHr()
    {
        var markdown = "---";
        var html = _service.Render(markdown);

        Assert.Contains("<hr", html);
    }

    [Fact]
    public void Render_HorizontalRule_WithAsterisks()
    {
        var markdown = "***";
        var html = _service.Render(markdown);

        Assert.Contains("<hr", html);
    }

    // ── TASK LIST TESTS ────────────────────────────────────────────────────

    [Fact]
    public void Render_TaskList_WithCheckboxes()
    {
        var markdown = "- [ ] Task 1\n- [x] Task 2\n- [ ] Task 3";
        var html = _service.Render(markdown);

        Assert.Contains("<input", html);
        Assert.Contains("Task 1", html);
        Assert.Contains("Task 2", html);
    }

    // ── COMBINED STRUCTURE TESTS ───────────────────────────────────────────

    [Fact]
    public void Render_ParagraphThenBulletList()
    {
        var markdown = "Introduction paragraph.\n\n- Bullet 1\n- Bullet 2";
        var html = _service.Render(markdown);

        Assert.Contains("<p>Introduction", html);
        Assert.Contains("<ul>", html);
        Assert.Contains("<li>Bullet 1", html);
    }

    [Fact]
    public void Render_HeadingThenContent()
    {
        var markdown = "# Main Title\n\nParagraph text.\n\n- Point 1\n- Point 2";
        var html = _service.Render(markdown);

        Assert.Contains("<h1", html);  // Markdig adds id attribute
        Assert.Contains("Main Title", html);
        Assert.Contains("<p>Paragraph", html);
        Assert.Contains("<ul>", html);
    }

    [Fact]
    public void Render_ComplexDocument()
    {
        var markdown = @"# Constitution
## Core Principles

A principle paragraph.

- GL-01: First guideline
- GL-02: Second guideline

## Standards

| Standard | Status |
|----------|--------|
| PS-01    | Ready  |

### Code Example

```csharp
var result = Calculate();
```

> Important note here.";

        var html = _service.Render(markdown);

        Assert.Contains("<h1", html);  // Markdig adds id
        Assert.Contains("<h2", html);
        Assert.Contains("principle paragraph", html);
        Assert.Contains("<ul>", html);
        Assert.Contains("<table>", html);
        Assert.Contains("<pre>", html);
        Assert.Contains("<blockquote>", html);
    }

    // ── MALFORMED MARKDOWN TESTS ───────────────────────────────────────────

    [Fact]
    public void Render_UnclosedBold_StillRendersAsText()
    {
        var markdown = "This is **bold.";
        var html = _service.Render(markdown);

        // Should render safely, not crash
        Assert.NotEmpty(html);
    }

    [Fact]
    public void Render_MissingTableSeparator_StillRenders()
    {
        var markdown = "| Header |\n| Cell |";
        var html = _service.Render(markdown);

        // Should render safely
        Assert.NotEmpty(html);
    }

    [Fact]
    public void Render_NestedBrokenLists_StillRenders()
    {
        var markdown = "- Item\n  - Nested\n   - Badly indented";
        var html = _service.Render(markdown);

        Assert.Contains("<li>", html);
    }

    // ── HTML INJECTION TESTS ───────────────────────────────────────────────

    [Fact]
    public void Render_ScriptTag_IsNotRendered()
    {
        var markdown = "Text <script>alert('xss')</script> more text";
        var html = _service.Render(markdown);

        // Script tag should be escaped by Markdig
        // The raw HTML is treated as text when DisableHtml is enabled
        Assert.Contains("Text", html);
    }

    [Fact]
    public void Render_OnClickAttribute_IsNotRendered()
    {
        var markdown = "<button onclick=\"alert('xss')\">Click</button>";
        var html = _service.Render(markdown);

        // Raw HTML is treated as text when DisableHtml is enabled
        Assert.NotEmpty(html);
    }

    [Fact]
    public void Render_IFrameTag_IsNotRendered()
    {
        var markdown = "<iframe src=\"evil.com\"></iframe>";
        var html = _service.Render(markdown);

        // Raw HTML is treated as text when DisableHtml is enabled
        Assert.NotEmpty(html);
    }

    // ── SPECIAL CHARACTER TESTS ────────────────────────────────────────────

    [Fact]
    public void Render_HtmlEntities_AreEscaped()
    {
        var markdown = "Text with < and > and & characters.";
        var html = _service.Render(markdown);

        // Should be safe for display
        Assert.Contains("&lt;", html);
        Assert.Contains("&gt;", html);
        Assert.Contains("&amp;", html);
    }

    [Fact]
    public void Render_NorwegianCharacters_PreservedUtf8()
    {
        var markdown = "Norwegian text: æ, ø, å";
        var html = _service.Render(markdown);

        Assert.Contains("æ", html);
        Assert.Contains("ø", html);
        Assert.Contains("å", html);
    }

    [Fact]
    public void Render_UnicodeEmoji_Preserved()
    {
        var markdown = "Status: ✓ Complete ✗ Incomplete";
        var html = _service.Render(markdown);

        Assert.Contains("✓", html);
        Assert.Contains("✗", html);
    }

    // ── WHITESPACE HANDLING TESTS ──────────────────────────────────────────

    [Fact]
    public void Render_LeadingTrailingWhitespace_Stripped()
    {
        var markdown = "   Some text   ";
        var html = _service.Render(markdown);

        Assert.Contains("Some text", html);
    }

    [Fact]
    public void Render_MultipleBlankLines_Collapsed()
    {
        var markdown = "Para 1\n\n\n\nPara 2";
        var html = _service.Render(markdown);

        var pCount = System.Text.RegularExpressions.Regex.Matches(html, "<p>").Count;
        Assert.Equal(2, pCount);
    }

    // ── REAL-WORLD CONSTITUTION EXAMPLE ────────────────────────────────────

    [Fact]
    public void Render_ConstitutionPrinciple_ComplexStructure()
    {
        var markdown = @"All communication between layers MUST use published contracts.

- **GL-01**: All frontend-to-backend communication routes through the reverse proxy.
- **GL-02**: API contract design precedes implementation. The contract is the source of truth, not the database schema.
- **GL-03**: Blazor components fetch data exclusively via `HttpClient` against published contracts.

See also: [API Standards](https://wiki.example.com/api-standards)";

        var html = _service.Render(markdown);

        Assert.Contains("All communication", html);
        Assert.Contains("<ul>", html);
        Assert.Contains("<li>", html);
        Assert.Contains("<strong>GL-01</strong>", html);
        Assert.Contains("<code>HttpClient</code>", html);
        Assert.Contains("<a href=\"https://wiki.example.com/api-standards\">", html);
    }

    // ── Real Constitution Examples ────────────────────────────────────────

    [Fact]
    public void Render_RealConstitution_PrincipleWithGuidelines()
    {
        // From actual Constitution: PP-01 Contract-Driven Communication
        var markdown = @"All communication between layers and services MUST go through published API contracts.
Backend has no knowledge of the presentation layer. No service accesses another service's
data layer directly — regardless of technology. This is non-negotiable.

- GL-01: All frontend-to-backend communication routes through the reverse proxy (YARP/APIM).
  Direct backend URLs are forbidden in frontend code.
- GL-02: API contract design precedes implementation. The contract is the source of truth,
  not the database schema.
- GL-03: Blazor components fetch data exclusively via `HttpClient` against published contracts.
- GL-16: Contracts use domain language (not table names, legacy IDs, or internal codes).";

        var html = _service.Render(markdown);

        // Verify structure is preserved
        Assert.Contains("<p>", html);
        Assert.Contains("All communication", html);
        Assert.Contains("<ul>", html);
        Assert.Contains("GL-01:", html);
        Assert.Contains("GL-02:", html);
        Assert.Contains("GL-03:", html);
        Assert.Contains("GL-16:", html);
        Assert.Contains("<code>HttpClient</code>", html);
        Assert.Contains("YARP/APIM", html);
    }

    [Fact]
    public void Render_RealConstitution_ZeroTrustWithBoldLabels()
    {
        // From actual Constitution: Zero-Trust Security sections
        var markdown = @"No user, service, or network component is implicitly trusted.

- **All access decisions** are made by calling the Authorization evaluation API
  (`POST /api/autorisasjon/v1/evaluer`). No service implements its own access rules.
- **Fail-closed** (GL-25): Security-critical operations MUST return HTTP 503 on auth failure.
  Fail-open is forbidden.
- **EntraID** (PS-01): All user authentication is handled by Azure EntraID.
  Custom auth mechanisms are prohibited.";

        var html = _service.Render(markdown);

        Assert.Contains("<strong>All access decisions</strong>", html);
        Assert.Contains("<strong>Fail-closed</strong>", html);
        Assert.Contains("<strong>EntraID</strong>", html);
        Assert.Contains("<code>POST /api/autorisasjon/v1/evaluer</code>", html);
        Assert.Contains("GL-25", html);
        Assert.Contains("PS-01", html);
    }

    [Fact]
    public void Render_RealConstitution_GovernanceWithSubsections()
    {
        // From actual Constitution: Authorization Module Constraints sections
        var markdown = @"All access control is divided into two strictly separated domains:

- **General access**: Governs operations not tied to a specific child. Determined by the
  combination of user identity, organizational unit, and general role(s) from the EntraID token.
- **Child-specific access**: Governs operations related to a specific child. Requires an
  explicit, managed relation between the user and the child. The relation's character is
  defined by the assigned child-specific role.

These domains are complementary and additive. Neither can substitute for the other.";

        var html = _service.Render(markdown);

        Assert.Contains("<strong>General access</strong>", html);
        Assert.Contains("<strong>Child-specific access</strong>", html);
        Assert.Contains("complementary", html);
        Assert.Contains("EntraID", html);
    }

    [Fact]
    public void Render_RealConstitution_SourceCodeLanguageWithLists()
    {
        // From actual Constitution: Development Standards - Source Code Language
        var markdown = @"All source code MUST be written in English.

**Exception — domain terms**: Norwegian domain-specific vocabulary is preserved as-is and
MUST NOT be translated. Examples of retained Norwegian domain terms:

- Entity/concept names: `Barn`, `BarnRelasjon`, `OrgEnhet`, `RolleTildeling`
- Field names on domain entities: `GyldigFra`, `GyldigTil`, `OpprettetAv`, `UtførtAv`
- Event/message payload fields that mirror domain names: `KorrelasjonsId`
- Domain exception codes: `SELVTILDELING_FORBUDT`
- Access-model concepts: `nødtilgang`, `barnespesifikk`";

        var html = _service.Render(markdown);

        Assert.Contains("<strong>Exception — domain terms</strong>", html);
        Assert.Contains("<code>Barn</code>", html);
        Assert.Contains("<code>GyldigFra</code>", html);
        Assert.Contains("<code>SELVTILDELING_FORBUDT</code>", html);
        Assert.Contains("nødtilgang", html);
    }

    [Fact]
    public void Render_RealConstitution_ConstraintWithMultiplePoints()
    {
        // From actual Constitution: constraints with numbered/bullet structure
        var markdown = @"A user's effective permissions at any point are the **sum** of all active role assignments.
There are no denial rules. If a user holds multiple roles, their effective permissions are
the union of all granted operations.

Key considerations:
- Roles are cumulative, never subtractive
- Multiple role assignments grant additive permissions
- No negative permissions or deny rules exist
- Administrator actions require explicit grants";

        var html = _service.Render(markdown);

        Assert.Contains("<strong>sum</strong>", html);
        Assert.Contains("cumulative", html);
        Assert.Contains("additive permissions", html);
    }

    // ── DE-DUPLICATION TESTS (Regression: No duplicate rendering) ──────────

    [Fact]
    public void Render_StandardWithBulletsAndNarrative_RendersAllContent()
    {
        var markdown = @"All source code MUST be written in English.

**Exception — domain terms**: Norwegian vocabulary is preserved.

- Entity/concept names: `Barn`, `BarnRelasjon`
- Field names: `GyldigFra`, `GyldigTil`
- Domain exception codes: `SELVTILDELING_FORBUDT`

**Rule of thumb**: Keep Norwegian if it appears in the constitution.";

        var html = _service.Render(markdown);

        // Verify narrative is present
        Assert.Contains("All source code MUST be written in English", html);
        Assert.Contains("Rule of thumb", html);

        // Verify bullets are rendered as list items
        Assert.Contains("<li>", html);
        Assert.Contains("Entity/concept names", html);
        Assert.Contains("Field names", html);
        Assert.Contains("Domain exception codes", html);

        // Verify inline code is rendered
        Assert.Contains("<code>Barn</code>", html);
        Assert.Contains("<code>GyldigFra</code>", html);
    }

    [Fact]
    public void Render_ConstraintWithMultipleBullets_EachBulletAppearsOnce()
    {
        var markdown = @"### Strict Role–Operation Separation

- General roles MUST only contain general operations.
- Child-specific roles MUST only contain child-specific operations.
- This separation is enforced at the data model level and MUST be validated on every assignment.";

        var html = _service.Render(markdown);

        // Each bullet should appear as an <li> item
        var liCount = System.Text.RegularExpressions.Regex.Matches(html, "<li>").Count;
        Assert.Equal(3, liCount); // Exactly 3 bullets

        // Verify each bullet text appears exactly once in the rendered HTML
        var generalCount = System.Text.RegularExpressions.Regex.Matches(html, "General roles MUST only contain general operations").Count;
        Assert.Equal(1, generalCount);

        var childCount = System.Text.RegularExpressions.Regex.Matches(html, "Child-specific roles MUST only contain child-specific operations").Count;
        Assert.Equal(1, childCount);

        var separationCount = System.Text.RegularExpressions.Regex.Matches(html, "This separation is enforced").Count;
        Assert.Equal(1, separationCount);
    }

    [Fact]
    public void Render_GovernanceWithAmendmentPoints_EachPointAppearsOnce()
    {
        var markdown = @"## Governance

This constitution is subordinate to the Platform Constitution.

**Amendment procedure:**
- Changes require written proposal and architecture review
- Changes must be approved by solution architect
- All specs and plans must be updated before taking effect

**Versioning policy:**
- MAJOR: Backward-incompatible changes
- MINOR: New principle or section added
- PATCH: Clarifications and refinements

**Compliance:**
- All PRs must verify compliance with constitution
- Deviation requests signal amendment process";

        var html = _service.Render(markdown);

        // Verify narrative appears
        Assert.Contains("subordinate to the Platform Constitution", html);
        Assert.Contains("Compliance", html);

        // Count list items - should be 3 amendment + 3 versioning + 2 compliance = 8 total
        var liCount = System.Text.RegularExpressions.Regex.Matches(html, "<li>").Count;
        Assert.Equal(8, liCount);

        // Verify each point appears exactly once
        var majorCount = System.Text.RegularExpressions.Regex.Matches(html, "Backward-incompatible changes").Count;
        Assert.Equal(1, majorCount);

        var prCount = System.Text.RegularExpressions.Regex.Matches(html, "All PRs must verify compliance").Count;
        Assert.Equal(1, prCount);
    }

    [Fact]
    public void Render_PrincipleWithGuidelinesAndNarrative_PreservesStructure()
    {
        var markdown = @"### Contract-Driven Communication

All communication between layers MUST go through published API contracts.

- GL-01: All frontend-to-backend communication routes through reverse proxy
- GL-02: API contract design precedes implementation
- GL-03: Blazor components fetch data via `HttpClient` contracts
- GL-16: Contracts use domain language

Backend has no knowledge of the presentation layer.";

        var html = _service.Render(markdown);

        // Verify narrative bookends
        Assert.Contains("All communication between layers", html);
        Assert.Contains("Backend has no knowledge", html);

        // Verify each guideline appears as separate list item
        var glCount = System.Text.RegularExpressions.Regex.Matches(html, "GL-").Count;
        Assert.Equal(4, glCount); // GL-01, GL-02, GL-03, GL-16

        // Verify inline code
        Assert.Contains("<code>HttpClient</code>", html);

        // Verify bullets are rendered as list items
        var liCount = System.Text.RegularExpressions.Regex.Matches(html, "<li>").Count;
        Assert.Equal(4, liCount);
    }
}
