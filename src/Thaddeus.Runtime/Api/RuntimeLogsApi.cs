using System.Text.Json;
using System.Text.Json.Serialization;
using Thaddeus.Runtime.Hosting;

namespace Thaddeus.Runtime.Api;

/// <summary>
/// Read-only endpoints for inspecting the runtime log files under the lock
/// directory. The UI uses these to show a tail view without exposing arbitrary
/// file reads.
/// </summary>
public static class RuntimeLogsApi
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static IEndpointRouteBuilder MapRuntimeLogsApi(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/api/logs", (int? limit, RuntimeOptions options) =>
        {
            var capped = limit is null or < 1 ? 25 : Math.Min(limit.Value, 200);
            var logs = ListRecent(GetLogsRoot(options), capped);
            return Results.Json(new RuntimeLogListResponse(logs), JsonOptions);
        }).WithName("ListRuntimeLogs");

        app.MapGet("/api/logs/{fileName}", (string fileName, int? tail, RuntimeOptions options) =>
        {
            if (!IsSafeLogFileName(fileName))
                return Results.BadRequest(new { error = "invalid fileName" });

            var root = GetLogsRoot(options);
            if (string.IsNullOrWhiteSpace(root))
                return Results.NotFound(new { error = "logs root unavailable", fileName });

            var path = Path.GetFullPath(Path.Combine(root, fileName));
            var fullRoot = Path.GetFullPath(root);
            var rootPrefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
                ? fullRoot
                : fullRoot + Path.DirectorySeparatorChar;

            if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                return Results.NotFound(new { error = "log not found", fileName });

            var cappedTail = tail is null or < 1 ? 400 : Math.Min(tail.Value, 2_000);
            var lines = ReadTailLines(path, cappedTail);
            return Results.Json(new RuntimeLogResponse(fileName, lines), JsonOptions);
        }).WithName("GetRuntimeLog");

        return app;
    }

    public static string GetLogsRoot(RuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var lockDir = Path.GetDirectoryName(options.LockFilePath);
        return string.IsNullOrEmpty(lockDir) ? string.Empty : Path.Combine(lockDir, "logs");
    }

    public static IReadOnlyList<RuntimeLogSummary> ListRecent(string root, int limit)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return Array.Empty<RuntimeLogSummary>();

        try
        {
            return new DirectoryInfo(root)
                .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                .Where(fileInfo => IsSafeLogFileName(fileInfo.Name))
                .OrderByDescending(fileInfo => fileInfo.LastWriteTimeUtc)
                .Take(Math.Max(0, limit))
                .Select(fileInfo =>
                {
                    var (lineCount, lastLine) = CountLinesAndLastLine(fileInfo.FullName);
                    return new RuntimeLogSummary(
                        FileName: fileInfo.Name,
                        ModifiedAt: fileInfo.LastWriteTimeUtc,
                        SizeBytes: fileInfo.Length,
                        LineCount: lineCount,
                        LastLine: lastLine);
                })
                .ToArray();
        }
        catch
        {
            return Array.Empty<RuntimeLogSummary>();
        }
    }

    public static IReadOnlyList<RuntimeLogLine> ReadTailLines(string path, int tail)
    {
        if (tail <= 0) return Array.Empty<RuntimeLogLine>();

        var lines = new Queue<RuntimeLogLine>(tail);
        var lineNumber = 0;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string? lineText;
            while ((lineText = reader.ReadLine()) is not null)
            {
                lineNumber++;
                lines.Enqueue(new RuntimeLogLine(lineNumber, lineText));
                while (lines.Count > tail)
                    lines.Dequeue();
            }
        }
        catch
        {
            return Array.Empty<RuntimeLogLine>();
        }

        return lines.ToArray();
    }

    public static bool IsSafeLogFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 160)
            return false;
        if (!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
            return false;

        var extension = Path.GetExtension(fileName);
        if (!string.Equals(extension, ".log", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".jsonl", StringComparison.OrdinalIgnoreCase))
            return false;

        foreach (var character in fileName)
        {
            if (!char.IsLetterOrDigit(character) && character is not '.' and not '_' and not '-')
                return false;
        }

        return true;
    }

    private static (int lineCount, string? lastLine) CountLinesAndLastLine(string path)
    {
        try
        {
            var lineCount = 0;
            string? lastLine = null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string? lineText;
            while ((lineText = reader.ReadLine()) is not null)
            {
                lineCount++;
                if (!string.IsNullOrWhiteSpace(lineText))
                    lastLine = lineText;
            }
            return (lineCount, lastLine);
        }
        catch
        {
            return (0, null);
        }
    }
}

public sealed record RuntimeLogSummary(
    string FileName,
    DateTimeOffset ModifiedAt,
    long SizeBytes,
    int LineCount,
    string? LastLine);

public sealed record RuntimeLogListResponse(IReadOnlyList<RuntimeLogSummary> Logs);

public sealed record RuntimeLogLine(int Number, string Text);

public sealed record RuntimeLogResponse(string FileName, IReadOnlyList<RuntimeLogLine> Lines);