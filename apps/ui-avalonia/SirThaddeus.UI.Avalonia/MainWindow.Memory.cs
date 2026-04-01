using Avalonia.Controls;
using Avalonia.Interactivity;
using SirThaddeus.Contracts;

namespace SirThaddeus.UI.Avalonia;

public partial class MainWindow
{
    private async void RefreshMemoryButton_Click(object? sender, RoutedEventArgs e)
    {
        await RefreshMemoryAsync();
    }

    private async Task RefreshMemoryAsync()
    {
        if (_runtimeApiClient is null)
        {
            MemoryStatusText.Text = "Memory: runtime not connected";
            _memoryFacts.Clear();
            _memoryEvents.Clear();
            _memoryChunks.Clear();
            _memoryNuggets.Clear();
            return;
        }

        try
        {
            var response = await _runtimeApiClient.GetMemoryAsync(MemoryFilterBox.Text, 40, CancellationToken.None);
            _memoryFacts.Clear();
            _memoryEvents.Clear();
            _memoryChunks.Clear();
            _memoryNuggets.Clear();

            foreach (var fact in response.Facts)
            {
                _memoryFacts.Add(new MemoryFactRowViewModel(fact));
            }

            foreach (var evt in response.Events)
            {
                _memoryEvents.Add(new MemoryEventRowViewModel(evt));
            }

            foreach (var chunk in response.Chunks)
            {
                _memoryChunks.Add(new MemoryChunkRowViewModel(chunk));
            }

            foreach (var nugget in response.Nuggets)
            {
                _memoryNuggets.Add(new MemoryNuggetRowViewModel(nugget));
            }

            MemoryStatusText.Text = $"Memory loaded. Facts={response.TotalFacts}, Events={response.TotalEvents}, Chunks={response.TotalChunks}, Nuggets={response.TotalNuggets}";
        }
        catch (Exception ex)
        {
            MemoryStatusText.Text = "Memory load failed: " + ex.Message;
        }
    }

    private async void MemoryFactsList_CellEditEnded(object? sender, DataGridCellEditEndedEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit && e.Row.DataContext is MemoryFactRowViewModel row)
        {
            if (_runtimeApiClient is null)
            {
                return;
            }

            try
            {
                await _runtimeApiClient.SaveMemoryFactAsync(row.MemoryId, row.ToSaveRequest(), CancellationToken.None);
            }
            catch (Exception ex)
            {
                MemoryStatusText.Text = $"Failed to save fact: {ex.Message}";
            }
        }
    }

    private async void MemoryEventsList_CellEditEnded(object? sender, DataGridCellEditEndedEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit && e.Row.DataContext is MemoryEventRowViewModel row)
        {
            if (_runtimeApiClient is null)
            {
                return;
            }

            try
            {
                await _runtimeApiClient.SaveMemoryEventAsync(row.EventId, row.ToSaveRequest(), CancellationToken.None);
            }
            catch (Exception ex)
            {
                MemoryStatusText.Text = $"Failed to save event: {ex.Message}";
            }
        }
    }

    private async void MemoryChunksList_CellEditEnded(object? sender, DataGridCellEditEndedEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit && e.Row.DataContext is MemoryChunkRowViewModel row)
        {
            if (_runtimeApiClient is null)
            {
                return;
            }

            try
            {
                await _runtimeApiClient.SaveMemoryChunkAsync(row.ChunkId, row.ToSaveRequest(), CancellationToken.None);
            }
            catch (Exception ex)
            {
                MemoryStatusText.Text = $"Failed to save chunk: {ex.Message}";
            }
        }
    }

    private async void MemoryNuggetsList_CellEditEnded(object? sender, DataGridCellEditEndedEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit && e.Row.DataContext is MemoryNuggetRowViewModel row)
        {
            if (_runtimeApiClient is null)
            {
                return;
            }

            try
            {
                await _runtimeApiClient.SaveMemoryNuggetAsync(row.NuggetId, row.ToSaveRequest(), CancellationToken.None);
            }
            catch (Exception ex)
            {
                MemoryStatusText.Text = $"Failed to save nugget: {ex.Message}";
            }
        }
    }

    private sealed class MemoryFactRowViewModel
    {
        public string MemoryId { get; init; }
        public string? ProfileId { get; set; }
        public string Subject { get; set; }
        public string Predicate { get; set; }
        public string Object { get; set; }
        public double Confidence { get; set; }
        public string? SourceRef { get; set; }

        public MemoryFactRowViewModel(MemoryFactItemDto dto)
        {
            MemoryId = dto.MemoryId;
            ProfileId = dto.ProfileId;
            Subject = dto.Subject;
            Predicate = dto.Predicate;
            Object = dto.Object;
            Confidence = dto.Confidence;
            SourceRef = dto.SourceRef;
        }

        public SaveMemoryFactRequest ToSaveRequest() =>
            new SaveMemoryFactRequest(ProfileId, Subject, Predicate, Object, Confidence, SourceRef);
    }

    private sealed class MemoryEventRowViewModel
    {
        public string EventId { get; init; }
        public string? ProfileId { get; set; }
        public string Type { get; set; }
        public string Title { get; set; }
        public string? Summary { get; set; }
        public DateTimeOffset? WhenUtc { get; set; }
        public double Confidence { get; set; }
        public string? SourceRef { get; set; }

        public MemoryEventRowViewModel(MemoryEventItemDto dto)
        {
            EventId = dto.EventId;
            ProfileId = dto.ProfileId;
            Type = dto.Type;
            Title = dto.Title;
            Summary = dto.Summary;
            WhenUtc = dto.WhenUtc;
            Confidence = dto.Confidence;
            SourceRef = dto.SourceRef;
        }

        public SaveMemoryEventRequest ToSaveRequest() =>
            new SaveMemoryEventRequest(ProfileId, Type, Title, Summary, WhenUtc, Confidence, SourceRef);
    }

    private sealed class MemoryChunkRowViewModel
    {
        public string ChunkId { get; init; }
        public string SourceType { get; set; }
        public string? SourceRef { get; set; }
        public string Text { get; set; }
        public DateTimeOffset? WhenUtc { get; set; }

        public MemoryChunkRowViewModel(MemoryChunkItemDto dto)
        {
            ChunkId = dto.ChunkId;
            SourceType = dto.SourceType;
            SourceRef = dto.SourceRef;
            Text = dto.Text;
            WhenUtc = dto.WhenUtc;
        }

        public SaveMemoryChunkRequest ToSaveRequest() =>
            new SaveMemoryChunkRequest(SourceType, Text, WhenUtc, SourceRef);
    }

    private sealed class MemoryNuggetRowViewModel
    {
        public string NuggetId { get; init; }
        public string Text { get; set; }
        public string? Tags { get; set; }
        public double Weight { get; set; }
        public int PinLevel { get; set; }

        public MemoryNuggetRowViewModel(MemoryNuggetItemDto dto)
        {
            NuggetId = dto.NuggetId;
            Text = dto.Text;
            Tags = dto.Tags;
            Weight = dto.Weight;
            PinLevel = dto.PinLevel;
        }

        public SaveMemoryNuggetRequest ToSaveRequest() =>
            new SaveMemoryNuggetRequest(Text, Tags, Weight, PinLevel);
    }
}