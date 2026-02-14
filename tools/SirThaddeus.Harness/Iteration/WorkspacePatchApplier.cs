using SirThaddeus.Harness.Models;

namespace SirThaddeus.Harness.Iteration;

public sealed class WorkspacePatchApplier
{
    public PatchApplyResult Apply(
        IReadOnlyList<JudgePatchSuggestion> patches,
        IReadOnlyList<string> allowedTargets,
        int maxFiles,
        int maxLines)
    {
        var snapshots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var changedFiles = new List<string>();
        var applied = 0;
        var lineBudget = 0;

        foreach (var patch in patches)
        {
            if (applied >= maxFiles)
                break;

            if (string.IsNullOrWhiteSpace(patch.File) ||
                string.IsNullOrWhiteSpace(patch.Find))
            {
                continue;
            }

            var fullPath = ToAbsolutePath(patch.File);
            if (!File.Exists(fullPath))
                continue;
            if (!IsAllowedTarget(fullPath, allowedTargets))
                continue;

            var original = File.ReadAllText(fullPath);
            var idx = original.IndexOf(patch.Find, StringComparison.Ordinal);
            if (idx < 0)
                continue;

            var replacement = patch.Replace ?? "";
            var lineDelta = Math.Abs(CountLines(replacement) - CountLines(patch.Find));
            if (lineBudget + lineDelta > maxLines)
                continue;

            if (!snapshots.ContainsKey(fullPath))
                snapshots[fullPath] = original;

            var updated = original.Remove(idx, patch.Find.Length).Insert(idx, replacement);
            File.WriteAllText(fullPath, updated);
            changedFiles.Add(fullPath);
            applied++;
            lineBudget += lineDelta;
        }

        return new PatchApplyResult
        {
            AppliedCount = applied,
            ChangedFiles = changedFiles,
            OriginalSnapshots = snapshots
        };
    }

    public void Rollback(PatchApplyResult result)
    {
        foreach (var pair in result.OriginalSnapshots)
            File.WriteAllText(pair.Key, pair.Value);
    }

    private static bool IsAllowedTarget(string absolutePath, IReadOnlyList<string> allowedTargets)
    {
        if (allowedTargets.Count == 0)
            return false;

        var workspace = Directory.GetCurrentDirectory();
        var relative = Path.GetRelativePath(workspace, absolutePath).Replace('\\', '/');
        return allowedTargets.Any(target =>
        {
            var normalized = target.Replace('\\', '/').Trim();
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            return relative.StartsWith(normalized, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static int CountLines(string value) =>
        value.Count(ch => ch == '\n') + 1;

    private static string ToAbsolutePath(string path)
    {
        if (Path.IsPathRooted(path))
            return path;
        return Path.GetFullPath(path, Directory.GetCurrentDirectory());
    }
}

public sealed record PatchApplyResult
{
    public int AppliedCount { get; init; }
    public IReadOnlyList<string> ChangedFiles { get; init; } = [];
    public IReadOnlyDictionary<string, string> OriginalSnapshots { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
