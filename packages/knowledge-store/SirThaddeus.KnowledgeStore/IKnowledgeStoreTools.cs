namespace SirThaddeus.KnowledgeStore;

/// <summary>
/// Tools the LLM invokes to read and write knowledge store files.
/// All operations are validated by KnowledgeStoreGuard before execution.
/// </summary>
public interface IKnowledgeStoreTools
{
    /// <summary>
    /// Append content to an existing file.
    /// Most common operation: journal entries, log additions, notes.
    /// </summary>
    Task<KnowledgeToolResult> AppendToFileAsync(
        string rootId, string relativePath, string content);

    /// <summary>
    /// Replace a specific section in a file (find-and-replace).
    /// Used for state updates: HP changes, schedule modifications.
    /// </summary>
    Task<KnowledgeToolResult> UpdateSectionAsync(
        string rootId, string relativePath,
        string oldContent, string newContent);

    /// <summary>
    /// Create a new file. Naming enforced by FileNamingPolicy.
    /// FileConflictResolver checks for duplicates first.
    /// </summary>
    Task<KnowledgeToolResult> CreateFileAsync(
        string rootId, string relativePath, string content);

    /// <summary>
    /// Read a file's full content. Used for on-demand Tier 3 loading.
    /// </summary>
    Task<KnowledgeToolResult> ReadFileAsync(string rootId, string relativePath);

    /// <summary>
    /// List files in a directory with optional glob pattern.
    /// </summary>
    Task<KnowledgeToolResult> ListFilesAsync(
        string rootId, string relativeFolderPath, string? pattern = null);

    /// <summary>
    /// Propose an edit to an _instructions.md file.
    /// Returns a proposal for user review. Does NOT write.
    /// </summary>
    Task<InstructionEditProposal> ProposeInstructionEditAsync(
        string rootId, string relativePath, string proposedContent);

    /// <summary>
    /// Write an instruction file after user confirmation.
    /// Only callable through the InstructionEditHandler flow.
    /// </summary>
    Task<KnowledgeToolResult> WriteInstructionFileAsync(
        string rootId, string relativePath, string content);
}
