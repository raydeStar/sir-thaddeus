using Avalonia.Interactivity;
using SirThaddeus.Contracts;
using System.Linq;
using System.Text;

namespace SirThaddeus.UI.Avalonia;

public partial class MainWindow
{
    private async void SendButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_submitInProgress)
        {
            return;
        }

        var prompt = PromptBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            UpdateComposerState();
            return;
        }

        _submitInProgress = true;
        UpdateComposerState();
        try
        {
            await SubmitPromptAsync(prompt, voiceInitiated: false);
        }
        finally
        {
            _submitInProgress = false;
            UpdateComposerState();
        }
    }

    private async Task SubmitPromptAsync(string prompt, bool voiceInitiated)
    {
        // Show the user message and thinking indicator immediately so the UI
        // feels responsive while the runtime connection is established.
        _pendingUserPrompt = null;
        PromptBox.Text = string.Empty;

        var runtimePrompt = prompt;
        if (_attachedDocument is not null)
        {
            runtimePrompt = _attachedDocument.BuildContextBlock(prompt) + "\n" + prompt;
            AppendTranscript($"[system] Attached context injected: {_attachedDocument.FileName}");
            _attachedDocument = null;
            UpdateAttachmentUi();
        }

        if (_currentSession.Title == "New Chat")
        {
            _currentSession.Title = BuildSessionTitle(prompt);
            UpdateConversationTitle();
        }

        _lastUserPrompt = prompt;
        AppendTranscript($"[user] {prompt}");
        _currentSession.AddPendingAssistantMessage();
        ScrollChatToBottom();

        var connected = await EnsureRuntimeConnectedAsync(
            allowStartRuntime: _uiSettings.AutoStartRuntime,
            appendTranscriptOnFailure: false,
            waitForReadyRetries: _uiSettings.AutoStartRuntime ? 0 : 30,
            waitForReadyDelayMs: 500);
        if (!connected || _runtimeApiClient is null)
        {
            // Don't strand the user with their prompt gone. Restore it so they
            // can retry once the runtime is ready.
            _currentSession.ClearPendingAssistantMessage();
            _currentSession.AddMessage(
                "system",
                "Runtime isn't ready yet. If you just started LM Studio (or the headless runtime), give it a moment and try again. Your prompt has been restored.");
            PromptBox.Text = prompt;
            PromptBox.CaretIndex = prompt.Length;
            _pendingUserPrompt = prompt;
            UpdateComposerState();
            return;
        }

        try
        {
            _voiceInitiatedRun = voiceInitiated;
            ResetWorkflowProgressUi();

            var priorMessages = _currentSession.Messages
                .Where(message => !message.IsPending
                    && (message.Role is "user" or "assistant")
                    && !string.IsNullOrWhiteSpace(message.Content))
                .Select(message => new ChatHistoryMessage(message.Role, message.Content))
                .ToList();

            var run = await _runtimeApiClient.StartRunAsync(
                runtimePrompt,
                CancellationToken.None,
                _currentSession.ConversationId,
                priorMessages.Count > 0 ? priorMessages : null);
            _activeRunId = run.RunId;

            if (voiceInitiated)
            {
                SetVoiceChatStatus("Responding...");
            }

            _assistantBuffersByRunId[run.RunId] = new StringBuilder();
            UpdateComposerState();
            StartEventStream(run.RunId);
            UpdateComposerState();
        }
        catch (Exception ex)
        {
            _voiceInitiatedRun = false;
            _currentSession.ClearPendingAssistantMessage();
            AppendTranscript($"[error] {ex.Message}");
            UpdateComposerState();
        }
    }
}