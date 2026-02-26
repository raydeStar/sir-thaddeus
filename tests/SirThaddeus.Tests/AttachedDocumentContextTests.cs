using SirThaddeus.DesktopRuntime.Services;
using Xunit;

namespace SirThaddeus.Tests;

public sealed class AttachedDocumentContextTests
{
    // ─────────────────────────────────────────────────────────────────
    // Small file (< 2500 chars) — inline
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void SmallFile_IsSmall_ReturnsTrue()
    {
        var doc = new AttachedDocumentContext("notes.txt", "Hello world");
        Assert.True(doc.IsSmall);
    }

    [Fact]
    public void SmallFile_BuildContextBlock_ContainsFullContent()
    {
        const string content = "This is a short document with some notes.";
        var doc = new AttachedDocumentContext("notes.txt", content);

        var block = doc.BuildContextBlock("summarize this");

        Assert.Contains("[ATTACHED DOCUMENT: notes.txt]", block);
        Assert.Contains(content, block);
        Assert.Contains("[END DOCUMENT]", block);
    }

    [Fact]
    public void SmallFile_BuildContextBlock_IgnoresQuery()
    {
        const string content = "Alpha beta gamma delta.";
        var doc = new AttachedDocumentContext("test.md", content);

        var block1 = doc.BuildContextBlock("tell me about alpha");
        var block2 = doc.BuildContextBlock("what is gamma?");

        // Small files are always inlined in full regardless of query
        Assert.Contains(content, block1);
        Assert.Contains(content, block2);
    }

    // ─────────────────────────────────────────────────────────────────
    // Large file (>= 2500 chars) — RAG
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void LargeFile_IsSmall_ReturnsFalse()
    {
        var doc = new AttachedDocumentContext("big.txt", new string('x', 3000));
        Assert.False(doc.IsSmall);
    }

    [Fact]
    public void LargeFile_BuildContextBlock_ContainsDocumentMarkers()
    {
        var content = GenerateLargeDocument();
        var doc = new AttachedDocumentContext("report.csv", content);

        var block = doc.BuildContextBlock("what are the results?");

        Assert.Contains("[ATTACHED DOCUMENT: report.csv]", block);
        Assert.Contains("[END DOCUMENT]", block);
        Assert.Contains("relevant excerpts", block);
    }

    [Fact]
    public void LargeFile_BuildContextBlock_DoesNotExceedMaxLength()
    {
        var content = GenerateLargeDocument();
        var doc = new AttachedDocumentContext("big.html", content);

        var block = doc.BuildContextBlock("find the keyword");

        // The context block should be much shorter than the full document
        Assert.True(block.Length < content.Length,
            $"Context block ({block.Length}) should be shorter than full doc ({content.Length})");
    }

    [Fact]
    public void LargeFile_RanksRelevantChunksHigher()
    {
        // Build a document where "quantum physics" appears only in one section
        var sections = new List<string>();
        for (var i = 0; i < 20; i++)
            sections.Add($"Section {i}: This section discusses general topics about cooking recipes and gardening tips. " +
                         "It contains various information about soil quality and seasonal planting.");

        // Insert a specific section about quantum physics
        sections[12] = "Section 12: This section covers quantum physics and the behavior of subatomic particles. " +
                       "Einstein's theory of relativity and quantum entanglement are discussed in detail here.";

        var content = string.Join("\n\n", sections);
        var doc = new AttachedDocumentContext("research.txt", content);

        var block = doc.BuildContextBlock("tell me about quantum physics");

        // The quantum physics section should appear in the context
        Assert.Contains("quantum", block, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LargeFile_EmptyQuery_ReturnsSomeChunks()
    {
        var content = GenerateLargeDocument();
        var doc = new AttachedDocumentContext("data.txt", content);

        var block = doc.BuildContextBlock("");

        // Even with empty query, should return some content (first chunks)
        Assert.Contains("[ATTACHED DOCUMENT: data.txt]", block);
        Assert.Contains("[END DOCUMENT]", block);
        // Should have actual content between markers
        var markerEnd = block.IndexOf('\n', block.IndexOf("[ATTACHED DOCUMENT"));
        var endMarker = block.IndexOf("[END DOCUMENT]");
        Assert.True(endMarker - markerEnd > 50, "Should contain meaningful content even with empty query");
    }

    // ─────────────────────────────────────────────────────────────────
    // Threshold boundary
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void AtThreshold_IsLargeFile()
    {
        var doc = new AttachedDocumentContext("edge.txt",
            new string('a', AttachedDocumentContext.InlineThreshold));
        Assert.False(doc.IsSmall); // >= threshold is large
    }

    [Fact]
    public void JustBelowThreshold_IsSmallFile()
    {
        var doc = new AttachedDocumentContext("edge.txt",
            new string('a', AttachedDocumentContext.InlineThreshold - 1));
        Assert.True(doc.IsSmall);
    }

    // ─────────────────────────────────────────────────────────────────
    // Properties
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void FileName_IsPreserved()
    {
        var doc = new AttachedDocumentContext("my-notes.md", "content");
        Assert.Equal("my-notes.md", doc.FileName);
    }

    [Fact]
    public void RawContent_IsPreserved()
    {
        const string content = "Hello\nWorld\n123";
        var doc = new AttachedDocumentContext("test.txt", content);
        Assert.Equal(content, doc.RawContent);
    }

    // ─────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────

    private static string GenerateLargeDocument()
    {
        var paragraphs = new List<string>();
        for (var i = 0; i < 30; i++)
        {
            paragraphs.Add(
                $"Paragraph {i}: Lorem ipsum dolor sit amet, consectetur adipiscing elit. " +
                "Sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. " +
                "Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris. " +
                "Duis aute irure dolor in reprehenderit in voluptate velit esse cillum.");
        }
        return string.Join("\n\n", paragraphs);
    }
}
