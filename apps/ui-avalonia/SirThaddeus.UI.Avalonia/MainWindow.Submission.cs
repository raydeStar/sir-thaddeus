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
        var connected = await EnsureRuntimeConnectedAsync(
            allowStartRuntime: _uiSettings.AutoStartRuntime,
            appendTranscriptOnFailure: true);
        if (!connected || _runtimeApiClient is null)
        {
            _pendingUserPrompt = prompt;
            UpdateComposerState();
            return;
        }

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

        try
        {
            if (_currentSession.Title == "New Chat")
            {
                _currentSession.Title = BuildSessionTitle(prompt);
                UpdateConversationTitle();
            }

            _lastUserPrompt = prompt;
            _voiceInitiatedRun = voiceInitiated;
            ResetWorkflowProgressUi();

            var priorMessages = _currentSession.Messages
                .Where(message => !message.IsPending
                    && (message.Role is "user" or "assistant")
                    && !string.IsNullOrWhiteSpace(message.Content))
                .Select(message => new ChatHistoryMessage(message.Role, message.Content))
                .ToList();

            AppendTranscript($"[user] {prompt}");
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
            _currentSession.AddPendingAssistantMessage();
            UpdateComposerState();
            StartEventStream(run.RunId);
            UpdateComposerState();
        }
        catch (Exception ex)
        {
            _voiceInitiatedRun = false;
            AppendTranscript($"[error] {ex.Message}");
            UpdateComposerState();
        }
    }
}