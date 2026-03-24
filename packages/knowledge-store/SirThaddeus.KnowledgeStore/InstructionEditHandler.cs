namespace SirThaddeus.KnowledgeStore;

/// <summary>
/// Handles instruction file edits with mandatory user confirmation.
/// The AI proposes, the user approves, the system writes.
/// </summary>
public sealed class InstructionEditHandler
{
    private readonly IKnowledgeStoreTools _store;
    private InstructionEditProposal? _pendingProposal;

    public InstructionEditHandler(IKnowledgeStoreTools store)
    {
        _store = store;
    }

    /// <summary>
    /// The currently pending proposal, if any.
    /// </summary>
    public InstructionEditProposal? PendingProposal => _pendingProposal;

    /// <summary>
    /// Draft an instruction edit. Returns a proposal with before/after
    /// content for user review. Does NOT write to disk.
    /// </summary>
    public async Task<InstructionEditProposal> ProposeEditAsync(
        string rootId,
        string domainPath,
        string userRequest,
        string llmDraftedEdit)
    {
        var proposal = await _store.ProposeInstructionEditAsync(
            rootId, domainPath, llmDraftedEdit);

        proposal.ChangeDescription = userRequest;
        proposal.Status = EditStatus.AwaitingConfirmation;
        _pendingProposal = proposal;

        return proposal;
    }

    /// <summary>
    /// Format the proposal as a human-readable diff for display in chat.
    /// </summary>
    public static string FormatDiffForChat(InstructionEditProposal proposal)
    {
        var parts = new List<string>
        {
            $"**Proposed change to** `{proposal.FilePath}`:",
            ""
        };

        if (string.IsNullOrEmpty(proposal.OriginalContent))
        {
            parts.Add("**New file:**");
            parts.Add("```markdown");
            parts.Add(proposal.ProposedContent);
            parts.Add("```");
        }
        else
        {
            parts.Add("**Current content:**");
            parts.Add("```markdown");
            parts.Add(proposal.OriginalContent);
            parts.Add("```");
            parts.Add("");
            parts.Add("**Proposed content:**");
            parts.Add("```markdown");
            parts.Add(proposal.ProposedContent);
            parts.Add("```");
        }

        parts.Add("");
        parts.Add("Apply this change?");

        return string.Join('\n', parts);
    }

    /// <summary>
    /// Confirm the pending edit and write to disk.
    /// </summary>
    public async Task<KnowledgeToolResult> ConfirmAsync(string rootId)
    {
        if (_pendingProposal is null)
            return KnowledgeToolResult.Fail("No pending proposal to confirm.");

        if (_pendingProposal.Status != EditStatus.AwaitingConfirmation)
            return KnowledgeToolResult.Fail("Proposal is not awaiting confirmation.");

        _pendingProposal.Status = EditStatus.Confirmed;

        var result = await _store.WriteInstructionFileAsync(
            rootId, _pendingProposal.FilePath, _pendingProposal.ProposedContent);

        _pendingProposal = null;
        return result;
    }

    /// <summary>
    /// Reject the pending edit.
    /// </summary>
    public KnowledgeToolResult Reject()
    {
        if (_pendingProposal is null)
            return KnowledgeToolResult.Fail("No pending proposal to reject.");

        _pendingProposal.Status = EditStatus.Rejected;
        _pendingProposal = null;

        return KnowledgeToolResult.Ok("Instruction edit cancelled.");
    }
}
