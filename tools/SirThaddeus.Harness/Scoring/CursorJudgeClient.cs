using System.Text.Json;
using SirThaddeus.Harness.Cli;
using SirThaddeus.Harness.Models;

namespace SirThaddeus.Harness.Scoring;

public sealed class CursorJudgeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public async Task<CursorJudgeResult?> ExecuteAsync(
        HarnessJudgeMode judgeMode,
        CursorJudgePacket packet,
        string packetPath,
        string resultPath,
        int timeoutMs,
        bool required,
        CancellationToken cancellationToken)
    {
        if (judgeMode == HarnessJudgeMode.None)
            return null;

        if (judgeMode == HarnessJudgeMode.Model)
            return null; // v1: reserved command surface, no model judge integration yet.

        await File.WriteAllTextAsync(packetPath, JsonSerializer.Serialize(packet, JsonOptions), cancellationToken);

        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(resultPath))
            {
                var resultJson = await File.ReadAllTextAsync(resultPath, cancellationToken);
                var result = JsonSerializer.Deserialize<CursorJudgeResult>(resultJson, JsonOptions);
                Validate(result, resultPath);
                return result;
            }

            await Task.Delay(300, cancellationToken);
        }

        if (required)
            throw new TimeoutException($"Timed out waiting for judge result at {resultPath}.");

        return null;
    }

    private static void Validate(CursorJudgeResult? result, string path)
    {
        if (result is null)
            throw new InvalidOperationException($"Judge result is null: {path}");
        if (result.Score < 0 || result.Score > 10)
            throw new InvalidOperationException($"Judge score must be 0..10: {path}");
        if (result.Patches.Any(p => string.IsNullOrWhiteSpace(p.File)))
            throw new InvalidOperationException($"Judge patches must include file paths: {path}");
    }
}
