using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using SirThaddeus.Contracts;
using SirThaddeus.UI.Avalonia.ViewModels;
using System.IO;

namespace SirThaddeus.UI.Avalonia;

public partial class MainWindow
{
    private async void CopyLastAssistantButton_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastAssistantMessage))
        {
            AppendTranscript("[system] Nothing to copy yet.");
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            AppendTranscript("[error] Clipboard is unavailable on this platform.");
            return;
        }

        await clipboard.SetTextAsync(_lastAssistantMessage);
        AppendTranscript("[system] Copied last assistant message.");
    }

    private void RetryLastPromptButton_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastUserPrompt))
        {
            AppendTranscript("[system] Nothing to retry yet.");
            return;
        }

        PromptBox.Text = _lastUserPrompt;
        PromptBox.CaretIndex = _lastUserPrompt.Length;
        SendButton_Click(sender, e);
    }

    private async void ReadAloudButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_readAloudActive)
        {
            await RequestVoiceCancelAsync("read aloud button");
            return;
        }

        if (string.IsNullOrWhiteSpace(_lastAssistantMessage))
        {
            AppendTranscript("[system] Nothing to read aloud yet.");
            return;
        }

        using var readAloudCancellation = new CancellationTokenSource();
        _readAloudCancellation = readAloudCancellation;
        _readAloudActive = true;
        MarkReadAloudStarted(_lastAssistantMessage.Length);

        try
        {
            await _ttsPlaybackService.SpeakAsync(_lastAssistantMessage, readAloudCancellation.Token);
            MarkReadAloudCompleted(_lastAssistantMessage.Length);
            AppendTranscript("[system] Read aloud complete.");
        }
        catch (OperationCanceledException) when (readAloudCancellation.IsCancellationRequested)
        {
            MarkPushToTalkCanceled(
                headline: "Read aloud canceled.",
                detail: "Speech playback was stopped before completion.");
        }
        catch (Exception ex)
        {
            AppendTranscript("[error] Read aloud failed: " + ex.Message);
            MarkPushToTalkFailure("Read aloud failed.", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_readAloudCancellation, readAloudCancellation))
            {
                _readAloudCancellation = null;
            }

            _readAloudActive = false;
        }
    }

    private async Task AutoSpeakResponseAsync(string text)
    {
        if (_readAloudActive || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        using var cts = new CancellationTokenSource();
        _readAloudCancellation = cts;
        _readAloudActive = true;
        MarkReadAloudStarted(text.Length);

        try
        {
            await _ttsPlaybackService.SpeakAsync(text, cts.Token);
            MarkReadAloudCompleted(text.Length);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            MarkPushToTalkCanceled(
                headline: "Auto-read canceled.",
                detail: "Voice response playback was interrupted via VoiceHost.");
        }
        catch (Exception ex)
        {
            MarkPushToTalkFailure("Auto-read failed.", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_readAloudCancellation, cts))
            {
                _readAloudCancellation = null;
            }

            _readAloudActive = false;
        }
    }

    private void ShowSourcesButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_lastAssistantSources.Count == 0)
        {
            var lastAssistant = _currentSession.Messages.LastOrDefault(m => m.Role == "assistant" && !m.IsPending);
            if (lastAssistant is not null && lastAssistant.SourceCards.Count > 0)
            {
                _lastAssistantSources = lastAssistant.SourceCards.Select(card => card.Url).ToArray();
            }
        }

        if (_lastAssistantSources.Count == 0 && !string.IsNullOrWhiteSpace(_lastAssistantMessage))
        {
            _lastAssistantSources = ExtractUrls(_lastAssistantMessage);
        }

        var sources = _lastAssistantSources;
        if (sources.Count == 0)
        {
            AppendTranscript("[system] No source URLs detected in the last assistant response.");
            return;
        }

        AppendTranscript("[system] Sources from last assistant response:");
        foreach (var url in sources)
        {
            AppendTranscript("[source] " + url);
        }
    }

    private static ChatMessageItem? ResolveMessageFromMenuItem(object? sender)
    {
        if (sender is not MenuItem menuItem)
        {
            return null;
        }

        if (menuItem.DataContext is ChatMessageItem fromDataContext)
        {
            return fromDataContext;
        }

        if (menuItem.Parent is ContextMenu contextMenu)
        {
            return (contextMenu.DataContext ?? (contextMenu.PlacementTarget as Control)?.DataContext) as ChatMessageItem;
        }

        return null;
    }

    private async void CopyMessage_Click(object? sender, RoutedEventArgs e)
    {
        var message = ResolveMessageFromMenuItem(sender);
        if (message is null || string.IsNullOrWhiteSpace(message.Content))
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            AppendTranscript("[error] Clipboard is unavailable on this platform.");
            return;
        }

        await clipboard.SetTextAsync(message.Content);
        AppendTranscript("[system] Copied message to clipboard.");
    }

    private void RetryMessage_Click(object? sender, RoutedEventArgs e)
    {
        var message = ResolveMessageFromMenuItem(sender);
        if (message is null)
        {
            return;
        }

        var prompt = message.IsUser ? message.Content
            : !string.IsNullOrWhiteSpace(message.RetryPrompt) ? message.RetryPrompt
            : _lastUserPrompt;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            AppendTranscript("[system] Nothing to retry.");
            return;
        }

        PromptBox.Text = prompt;
        PromptBox.CaretIndex = prompt.Length;
        SendButton_Click(sender, e);
    }

    private async void ReadAloudMessage_Click(object? sender, RoutedEventArgs e)
    {
        var message = ResolveMessageFromMenuItem(sender);
        if (message is null || string.IsNullOrWhiteSpace(message.Content))
        {
            return;
        }

        if (_readAloudActive)
        {
            await RequestVoiceCancelAsync("read aloud message context");
            return;
        }

        using var readAloudCancellation = new CancellationTokenSource();
        _readAloudCancellation = readAloudCancellation;
        _readAloudActive = true;
        MarkReadAloudStarted(message.Content.Length);

        try
        {
            await _ttsPlaybackService.SpeakAsync(message.Content, readAloudCancellation.Token);
            MarkReadAloudCompleted(message.Content.Length);
            AppendTranscript("[system] Read aloud complete.");
        }
        catch (OperationCanceledException) when (readAloudCancellation.IsCancellationRequested)
        {
            MarkPushToTalkCanceled("Read aloud canceled.", "Speech playback was stopped.");
        }
        catch (Exception ex)
        {
            AppendTranscript("[error] Read aloud failed: " + ex.Message);
            MarkPushToTalkFailure("Read aloud failed.", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_readAloudCancellation, readAloudCancellation))
            {
                _readAloudCancellation = null;
            }

            _readAloudActive = false;
        }
    }

    private void AssistantSourceCardOpenButton_Click(object? sender, RoutedEventArgs e)
    {
        var url = (sender as Button)?.Tag as string;
        OpenExternalUrl(url);
    }

    private async void AttachFileButton_Click(object? sender, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null)
        {
            AppendTranscript("[error] File picker is unavailable on this platform.");
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Attach a document",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Supported Documents")
                {
                    Patterns = ["*.txt", "*.csv", "*.md", "*.html", "*.htm", "*.json", "*.log"]
                }
            ]
        });

        var file = files.FirstOrDefault();
        if (file is null)
        {
            return;
        }

        try
        {
            var content = await ReadAttachmentTextAsync(file, CancellationToken.None);
            if (string.IsNullOrWhiteSpace(content))
            {
                AppendTranscript("[system] Selected file is empty.");
                return;
            }

            if (content.Length > 200_000)
            {
                content = content[..200_000];
                AppendTranscript("[system] Attachment was trimmed to 200,000 characters.");
            }

            _attachedDocument = new AttachedDocumentContext(file.Name, content);
            UpdateAttachmentUi();
            AppendTranscript($"[system] Attached file ready: {file.Name}");
        }
        catch (Exception ex)
        {
            AppendTranscript("[error] Attachment failed: " + ex.Message);
        }
    }

    private void RemoveAttachmentButton_Click(object? sender, RoutedEventArgs e)
    {
        _attachedDocument = null;
        UpdateAttachmentUi();
    }

    private void UpdateAttachmentUi()
    {
        if (_attachedDocument is null)
        {
            AttachmentChip.IsVisible = false;
            AttachmentNameText.Text = string.Empty;
            AttachmentMetaText.Text = string.Empty;
            return;
        }

        AttachmentChip.IsVisible = true;
        AttachmentNameText.Text = _attachedDocument.FileName;
        AttachmentMetaText.Text = _attachedDocument.IsSmall
            ? $"{_attachedDocument.RawContent.Length:N0} chars (inline)"
            : $"{_attachedDocument.RawContent.Length:N0} chars (context excerpts)";
    }

    private static async Task<string> ReadAttachmentTextAsync(IStorageFile file, CancellationToken cancellationToken)
    {
        if (file.TryGetLocalPath() is { Length: > 0 } path && File.Exists(path))
        {
            return await File.ReadAllTextAsync(path, cancellationToken);
        }

        await using var stream = await file.OpenReadAsync();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private static IReadOnlyList<string> ExtractUrls(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var urls = new List<string>();
        var tokens = text.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            var trimmed = token.Trim(',', '.', ';', ')', ']', '}', '>');
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out _))
            {
                urls.Add(trimmed);
            }
        }

        return urls;
    }

    private static IReadOnlyList<ChatSourceCardItem> CreateAssistantSourceCards(
        IReadOnlyList<AssistantSourceCardPayload>? sourceCards)
    {
        if (sourceCards is null || sourceCards.Count == 0)
        {
            return [];
        }

        var cards = new List<ChatSourceCardItem>(sourceCards.Count);
        foreach (var card in sourceCards)
        {
            if (string.IsNullOrWhiteSpace(card.Url))
            {
                continue;
            }

            var item = new ChatSourceCardItem
            {
                Title = string.IsNullOrWhiteSpace(card.Title) ? card.Url : card.Title.Trim(),
                Url = card.Url,
                Domain = NormalizeSourceCardDomain(card.Domain, card.Url),
                Excerpt = Truncate(card.Excerpt?.Trim() ?? string.Empty, 220),
                PublishedLabel = FormatSourceCardPublishedLabel(card.PublishedAt),
                ThumbnailUrl = card.Thumbnail?.Trim() ?? string.Empty,
                FaviconBase64 = card.Favicon?.Trim() ?? string.Empty
            };

            item.BeginLoadImages();
            cards.Add(item);
        }

        return cards;
    }

    private static string NormalizeSourceCardDomain(string? domain, string? url)
    {
        if (!string.IsNullOrWhiteSpace(domain))
        {
            return domain.Trim();
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Host
            : string.Empty;
    }

    private static string FormatSourceCardPublishedLabel(string? publishedAt)
    {
        if (string.IsNullOrWhiteSpace(publishedAt))
        {
            return string.Empty;
        }

        return DateTimeOffset.TryParse(publishedAt, out var parsed)
            ? parsed.LocalDateTime.ToString("MMM d, h:mm tt")
            : string.Empty;
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text;
        }

        return text[..maxLength].TrimEnd() + "...";
    }
}