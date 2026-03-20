using System.Globalization;

namespace SirThaddeus.KnowledgeStore;

/// <summary>
/// Automatically archives older files in time-series folders
/// when they approach the MaxFilesPerFolder limit.
/// Moves files to _archive/{yyyy-MM}/ subfolders.
/// </summary>
public sealed class TimeSeriesArchiver
{
    private readonly StorePolicy _policy;

    public TimeSeriesArchiver(StorePolicy policy)
    {
        _policy = policy;
    }

    /// <summary>
    /// Check if a folder needs archiving and return a recommendation.
    /// </summary>
    public ArchiveRecommendation CheckFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath))
            return ArchiveRecommendation.None;

        var mdFiles = Directory.GetFiles(folderPath, "*.md")
            .Where(f => !Path.GetFileName(f).StartsWith('_'))
            .ToList();

        var count = mdFiles.Count;
        var threshold = (int)(_policy.MaxFilesPerFolder * 0.9); // 90% of limit

        if (count < threshold)
            return ArchiveRecommendation.None;

        // Count how many files are from previous months
        var now = DateTime.UtcNow;
        var archivable = mdFiles
            .Where(f => IsDateBasedFile(Path.GetFileName(f), out var date) && date.Month != now.Month)
            .Count();

        return new ArchiveRecommendation
        {
            FolderPath = folderPath,
            TotalFiles = count,
            ArchivableFiles = archivable,
            IsUrgent = count >= _policy.MaxFilesPerFolder,
            Message = count >= _policy.MaxFilesPerFolder
                ? $"Folder has {count} files (at limit). Archive needed before new files can be created."
                : $"Folder has {count} files ({_policy.MaxFilesPerFolder - count} remaining). Consider archiving."
        };
    }

    /// <summary>
    /// Archive files from previous months into _archive/{yyyy-MM}/ subfolders.
    /// Only moves date-based files that are not from the current month.
    /// </summary>
    public ArchiveResult Archive(string folderPath)
    {
        if (!Directory.Exists(folderPath))
            return new ArchiveResult { Success = false, Message = "Folder does not exist." };

        var now = DateTime.UtcNow;
        var archiveRoot = Path.Combine(folderPath, "_archive");
        var movedFiles = new List<string>();

        var mdFiles = Directory.GetFiles(folderPath, "*.md")
            .Where(f => !Path.GetFileName(f).StartsWith('_'))
            .ToList();

        foreach (var file in mdFiles)
        {
            var fileName = Path.GetFileName(file);
            if (!IsDateBasedFile(fileName, out var fileDate))
                continue;

            // Only archive files from previous months
            if (fileDate.Year == now.Year && fileDate.Month == now.Month)
                continue;

            var monthFolder = Path.Combine(
                archiveRoot,
                fileDate.ToString("yyyy-MM", CultureInfo.InvariantCulture));

            if (!Directory.Exists(monthFolder))
                Directory.CreateDirectory(monthFolder);

            var destPath = Path.Combine(monthFolder, fileName);
            if (!File.Exists(destPath))
            {
                File.Move(file, destPath);
                movedFiles.Add(fileName);
            }
        }

        return new ArchiveResult
        {
            Success = true,
            MovedFiles = movedFiles,
            Message = movedFiles.Count > 0
                ? $"Archived {movedFiles.Count} files to _archive/."
                : "No files needed archiving."
        };
    }

    /// <summary>
    /// Check if a filename follows the date pattern (yyyy-MM-dd.md).
    /// </summary>
    public static bool IsDateBasedFile(string fileName, out DateTime date)
    {
        date = default;
        var name = Path.GetFileNameWithoutExtension(fileName);
        // Also handle prefixed date files like "bloodwork-2026-01-15.md"
        if (name is not null && name.Length >= 10)
        {
            var datePart = name[^10..]; // Last 10 chars
            if (DateTime.TryParseExact(datePart, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                return true;

            // Try the whole name as a date
            if (DateTime.TryParseExact(name, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                return true;
        }

        return false;
    }
}

/// <summary>
/// Recommendation from the archiver about a folder's state.
/// </summary>
public sealed record ArchiveRecommendation
{
    public static readonly ArchiveRecommendation None = new()
    {
        FolderPath = string.Empty,
        TotalFiles = 0,
        ArchivableFiles = 0,
        IsUrgent = false,
        Message = string.Empty
    };

    public string FolderPath { get; init; } = string.Empty;
    public int TotalFiles { get; init; }
    public int ArchivableFiles { get; init; }
    public bool IsUrgent { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Result of an archiving operation.
/// </summary>
public sealed record ArchiveResult
{
    public bool Success { get; init; }
    public List<string> MovedFiles { get; init; } = [];
    public string Message { get; init; } = string.Empty;
}
