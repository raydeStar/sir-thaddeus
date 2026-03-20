using System.Text.Json.Serialization;

namespace SirThaddeus.KnowledgeStore;

// ────────────────────────────────────────────────────────────────
//  Workspace Root
// ────────────────────────────────────────────────────────────────

/// <summary>
/// A user-approved folder that Sir Thaddeus can access.
/// Knowledge roots allow read+write; reference roots are read-only.
/// </summary>
public sealed record WorkspaceRoot
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("absolutePath")]
    public string AbsolutePath { get; init; } = string.Empty;

    [JsonPropertyName("accessLevel")]
    public WorkspaceAccessLevel AccessLevel { get; init; }

    [JsonPropertyName("allowIndexing")]
    public bool AllowIndexing { get; init; } = true;

    /// <summary>
    /// When true, all write operations require explicit user confirmation
    /// before execution. Recommended for new roots until trust is established.
    /// When false, routine writes (journal appends, state updates) proceed
    /// automatically. Destructive operations (replace, instruction edits)
    /// ALWAYS require confirmation regardless of this setting.
    /// </summary>
    [JsonPropertyName("confirmWrites")]
    public bool ConfirmWrites { get; init; } = true;
}

/// <summary>
/// Access level for a workspace root.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkspaceAccessLevel
{
    /// <summary>Read-only. Used for codebases, reference material, PDFs.</summary>
    ReferenceReadOnly,

    /// <summary>Read+write. Used for journals, projects, game state, notes.</summary>
    KnowledgeReadWrite
}

// ────────────────────────────────────────────────────────────────
//  Store Policy
// ────────────────────────────────────────────────────────────────

/// <summary>
/// Configurable limits for knowledge store operations.
/// </summary>
public sealed record StorePolicy
{
    /// <summary>Max files per folder (not counting subfolders or _archive/).</summary>
    public int MaxFilesPerFolder { get; init; } = 200;

    /// <summary>Max subfolder nesting depth from root.</summary>
    public int MaxFolderDepth { get; init; } = 3;

    /// <summary>Max total size of a single Knowledge Root (50 MB).</summary>
    public long MaxRootSizeBytes { get; init; } = 50 * 1024 * 1024;

    /// <summary>Max size per individual file (512 KB).</summary>
    public long MaxFileSizeBytes { get; init; } = 512 * 1024;

    /// <summary>Max top-level domain folders per root.</summary>
    public int MaxDomainFolders { get; init; } = 20;

    /// <summary>Allowed extensions for file creation.</summary>
    public string[] AllowedWriteExtensions { get; init; } = [".md"];

    /// <summary>Allowed extensions for file reading.</summary>
    public string[] AllowedReadExtensions { get; init; } =
        [".md", ".txt", ".json", ".pdf", ".docx"];
}

// ────────────────────────────────────────────────────────────────
//  Validation Result
// ────────────────────────────────────────────────────────────────

/// <summary>
/// Result of a guard validation check.
/// </summary>
public sealed record ValidationResult
{
    public bool IsAllowed { get; init; }
    public string? Reason { get; init; }

    public static ValidationResult Allowed => new() { IsAllowed = true };

    public static ValidationResult Denied(string reason) =>
        new() { IsAllowed = false, Reason = reason };
}

// ────────────────────────────────────────────────────────────────
//  Tool Result
// ────────────────────────────────────────────────────────────────

/// <summary>
/// Result returned from knowledge store tool operations.
/// </summary>
public sealed record KnowledgeToolResult
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; init; }

    [JsonPropertyName("filePath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FilePath { get; init; }

    public static KnowledgeToolResult Ok(string message, string? content = null, string? filePath = null) =>
        new() { Success = true, Message = message, Content = content, FilePath = filePath };

    public static KnowledgeToolResult Fail(string message) =>
        new() { Success = false, Message = message };
}

// ────────────────────────────────────────────────────────────────
//  Frontmatter
// ────────────────────────────────────────────────────────────────

/// <summary>
/// YAML frontmatter metadata for a knowledge store file.
/// </summary>
public sealed record Frontmatter
{
    public List<string> Tags { get; init; } = [];
    public List<string> Mentions { get; init; } = [];
    public string Summary { get; init; } = string.Empty;
    public DateTime Created { get; init; } = DateTime.UtcNow;
    public DateTime Updated { get; init; } = DateTime.UtcNow;
    public string Type { get; init; } = "note";
}

// ────────────────────────────────────────────────────────────────
//  Tag Index Entry
// ────────────────────────────────────────────────────────────────

/// <summary>
/// A single entry in the tag index, representing one indexed file.
/// </summary>
public sealed record IndexEntry
{
    public string RelativePath { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public DateTime Updated { get; init; }
    public List<string> Tags { get; init; } = [];
    public List<string> Mentions { get; init; } = [];
}

// ────────────────────────────────────────────────────────────────
//  Retrieval Types
// ────────────────────────────────────────────────────────────────

/// <summary>
/// Depth of context retrieval.
/// </summary>
public enum RetrievalDepth
{
    /// <summary>Return file paths only.</summary>
    TagsOnly,

    /// <summary>Return frontmatter summaries (default).</summary>
    Summaries,

    /// <summary>Return full file content for top matches.</summary>
    FullContent
}

/// <summary>
/// Context retrieved from the knowledge store for LLM injection.
/// </summary>
public sealed class RetrievedContext
{
    public int MatchedFiles { get; set; }
    public List<string> FileList { get; set; } = [];
    public List<FileSummary> Summaries { get; set; } = [];
    public List<FileContent> FullContent { get; set; } = [];
}

public sealed record FileSummary
{
    public string Path { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public List<string> Tags { get; init; } = [];
}

public sealed record FileContent
{
    public string Path { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
}

// ────────────────────────────────────────────────────────────────
//  Domain Routing
// ────────────────────────────────────────────────────────────────

/// <summary>
/// Result of routing a user message to a knowledge domain.
/// </summary>
public sealed record DomainMatch
{
    public string? Domain { get; init; }
    public DomainConfidence Confidence { get; init; }
}

public enum DomainConfidence
{
    None,
    WeakKeywordMatch,
    StrongKeywordMatch,
    SessionContinuity,
    ExplicitReference
}

// ────────────────────────────────────────────────────────────────
//  Instruction Editing
// ────────────────────────────────────────────────────────────────

/// <summary>
/// A proposed edit to an _instructions.md file, pending user confirmation.
/// </summary>
public sealed class InstructionEditProposal
{
    public string FilePath { get; set; } = string.Empty;
    public string OriginalContent { get; set; } = string.Empty;
    public string ProposedContent { get; set; } = string.Empty;
    public string ChangeDescription { get; set; } = string.Empty;
    public EditStatus Status { get; set; }
}

public enum EditStatus
{
    AwaitingConfirmation,
    Confirmed,
    Rejected
}

// ────────────────────────────────────────────────────────────────
//  File Naming
// ────────────────────────────────────────────────────────────────

/// <summary>
/// Describes the intent behind creating a file, used by FileNamingPolicy.
/// </summary>
public sealed record FileCreationIntent
{
    public DateTime Date { get; init; } = DateTime.UtcNow;
    public string ProposedName { get; init; } = string.Empty;
    public string? SubType { get; init; }
}

// ────────────────────────────────────────────────────────────────
//  File Conflict Resolution
// ────────────────────────────────────────────────────────────────

/// <summary>
/// Action to take when a write target already exists.
/// </summary>
public enum WriteAction
{
    Create,
    Append,
    SkipDuplicate
}

// ────────────────────────────────────────────────────────────────
//  Memory Classification
// ────────────────────────────────────────────────────────────────

/// <summary>
/// Classification for incoming content persistence behavior.
/// </summary>
public enum ContentClassification
{
    /// <summary>Chat only, not stored.</summary>
    Ephemeral,

    /// <summary>Optionally stored if user is in an active session.</summary>
    Loggable,

    /// <summary>Explicitly stored in knowledge root.</summary>
    Durable
}
