using Microsoft.Extensions.Hosting;
using SirThaddeus.Wiki;
using Thaddeus.Runtime.Hosting;

namespace Thaddeus.Runtime.Memory;

/// <summary>
/// One-shot, idempotent migration from the legacy memos store
/// (<see cref="IMemoStore"/>, JSON files under <c>&lt;lockDir&gt;/memos</c>)
/// into the user's default wiki root as pages under a "Memos" folder.
///
/// <para>The wiki has hierarchy, revisions, full-text search, and an
/// actual editor; memos have none of those. Rather than maintain two
/// near-identical scratchpad systems, this service migrates a user's
/// existing memos on boot and the memos surface is then retired.</para>
///
/// <para><b>Safety contract:</b></para>
/// <list type="bullet">
///   <item>Idempotent: a sentinel file at <c>&lt;lockDir&gt;/memos.migrated</c>
///   prevents a second run from duplicating pages.</item>
///   <item>Non-destructive: source memos are <em>never</em> deleted by this
///   service. A user can compare wiki pages to original JSON files and only
///   wipe the memos folder when they're confident.</item>
///   <item>Fail-safe: if any single memo fails to migrate, the migration
///   logs and bails — the sentinel is NOT written, so the next boot retries.</item>
///   <item>Only runs when memos exist AND no prior migration sentinel is
///   present. New installs (no memos) just write the sentinel and move on.</item>
/// </list>
/// </summary>
public sealed class MemosToWikiMigrator : IHostedService
{
    private const string MigrationFolderName = "Memos (migrated)";
    private const string SourceMemoMarker = "source_memo_id:";

    private readonly IMemoStore _memos;
    private readonly IWikiStore _wiki;
    private readonly ILogger<MemosToWikiMigrator> _logger;
    private readonly string _sentinelPath;

    public MemosToWikiMigrator(
        IMemoStore memos,
        IWikiStore wiki,
        ILogger<MemosToWikiMigrator> logger,
        RuntimeOptions options)
    {
        _memos = memos;
        _wiki = wiki;
        _logger = logger;
        var lockDir = Path.GetDirectoryName(options.LockFilePath)
            ?? throw new InvalidOperationException("LockFilePath has no directory.");
        _sentinelPath = Path.Combine(lockDir, "memos.migrated");
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(_sentinelPath))
        {
            _logger.LogDebug("memos.migration.skipped reason=sentinel_exists path={Path}", _sentinelPath);
            return;
        }

        IReadOnlyList<Thaddeus.SharedTypes.Memo> memos;
        try
        {
            memos = await _memos.ListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "memos.migration.list_failed — will retry next boot");
            return;
        }

        if (memos.Count == 0)
        {
            // Nothing to migrate: write the sentinel so we don't re-check
            // every boot. This is the common path for fresh installs.
            await WriteSentinelAsync("no memos present at first boot", 0).ConfigureAwait(false);
            return;
        }

        // Pick the first wiki root as the destination. If no roots exist
        // yet (unlikely on a real install — runtime seeds the default
        // wiki root at startup) we can't migrate; retry next boot.
        IReadOnlyList<WikiRoot> roots;
        try
        {
            roots = await _wiki.ListRootsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "memos.migration.wiki_unavailable — will retry next boot");
            return;
        }

        if (roots.Count == 0)
        {
            _logger.LogInformation("memos.migration.deferred reason=no_wiki_roots count={Count}", memos.Count);
            return;
        }

        var targetRoot = roots[0];
        WikiFolder folder;
        try
        {
            folder = await GetOrCreateMigrationFolderAsync(targetRoot.Id, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "memos.migration.folder_create_failed rootId={RootId} name={Name}",
                targetRoot.Id, MigrationFolderName);
            return;
        }

        HashSet<string> migratedIds;
        try
        {
            migratedIds = await LoadMigratedMemoIdsAsync(targetRoot.Id, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "memos.migration.scan_existing_failed rootId={RootId}",
                targetRoot.Id);
            return;
        }
        var migrated = 0;
        var skipped = 0;
        foreach (var memo in memos)
        {
            if (migratedIds.Contains(memo.Id))
            {
                skipped++;
                continue;
            }

            try
            {
                var body = ComposeBody(memo);
                var title = string.IsNullOrWhiteSpace(memo.Title) ? "Untitled memo" : memo.Title;
                await _wiki.CreatePageAsync(
                    targetRoot.Id,
                    folder.Id,
                    title,
                    body,
                    cancellationToken).ConfigureAwait(false);
                migrated++;
                migratedIds.Add(memo.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "memos.migration.page_failed memoId={MemoId} title={Title}",
                    memo.Id, memo.Title);
                // Bail without writing the sentinel so the next boot can
                // try again. Partial migration is acceptable — wiki pages
                // already created stay; missing memos will be re-attempted
                // (and any pages already created will become duplicates
                // under a new "Memos (migrated) (n)" folder).
                return;
            }
        }

        await WriteSentinelAsync(
            $"migrated {migrated} memos into root {targetRoot.Id} folder {folder.Id}; skipped {skipped} already migrated",
            migrated).ConfigureAwait(false);

        _logger.LogInformation("memos.migration.complete count={Count} skipped={Skipped} rootId={RootId} folderId={FolderId}",
            migrated, skipped, targetRoot.Id, folder.Id);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static string ComposeBody(Thaddeus.SharedTypes.Memo memo)
    {
        // Preserve metadata that has no wiki equivalent (tags, pinned
        // state, creation time) in a small frontmatter-style header above
        // the original markdown body. The user can clean it up after the
        // migration; the bytes are kept.
        var sb = new System.Text.StringBuilder();
        var tagsCsv = (memo.Tags is { Count: > 0 })
            ? string.Join(", ", memo.Tags)
            : null;
        var hasHeader = true;
        if (hasHeader)
        {
            sb.AppendLine("<!--");
            sb.AppendLine("Migrated from legacy memo:");
            sb.AppendLine($"  {SourceMemoMarker} {memo.Id}");
            if (memo.Pinned) sb.AppendLine("  pinned: true");
            if (tagsCsv is not null) sb.AppendLine($"  tags: {tagsCsv}");
            if (memo.CreatedAt != default) sb.AppendLine($"  created: {memo.CreatedAt:o}");
            sb.AppendLine("-->");
            sb.AppendLine();
        }
        sb.Append(memo.Body ?? string.Empty);
        return sb.ToString();
    }

    private async Task<WikiFolder> GetOrCreateMigrationFolderAsync(
        string rootId,
        CancellationToken cancellationToken)
    {
        var tree = await _wiki.GetTreeAsync(rootId, cancellationToken).ConfigureAwait(false);
        var existing = tree?.Folders.FirstOrDefault(f =>
            f.ParentFolderId is null &&
            string.Equals(f.Name, MigrationFolderName, StringComparison.Ordinal));
        if (existing is not null) return existing;

        return await _wiki.CreateFolderAsync(
            rootId,
            MigrationFolderName,
            parentFolderId: null,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<HashSet<string>> LoadMigratedMemoIdsAsync(
        string rootId,
        CancellationToken cancellationToken)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var tree = await _wiki.GetTreeAsync(rootId, cancellationToken).ConfigureAwait(false);
        if (tree is null) return ids;

        var migrationFolderIds = tree.Folders
            .Where(f => string.Equals(f.Name, MigrationFolderName, StringComparison.Ordinal))
            .Select(f => f.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (migrationFolderIds.Count == 0) return ids;

        foreach (var page in tree.Pages.Where(p => p.FolderId is not null && migrationFolderIds.Contains(p.FolderId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var doc = await _wiki.GetPageAsync(page.Id, cancellationToken).ConfigureAwait(false);
            var memoId = TryReadSourceMemoId(doc?.Markdown);
            if (!string.IsNullOrWhiteSpace(memoId))
                ids.Add(memoId);
        }

        return ids;
    }

    private static string? TryReadSourceMemoId(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return null;
        using var reader = new StringReader(markdown);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed == "-->") break;
            if (!trimmed.StartsWith(SourceMemoMarker, StringComparison.Ordinal))
                continue;

            var id = trimmed[SourceMemoMarker.Length..].Trim();
            return id.Length == 0 ? null : id;
        }

        return null;
    }

    private async Task WriteSentinelAsync(string note, int migratedCount)
    {
        try
        {
            var contents =
                $"migrated_at: {DateTimeOffset.UtcNow:o}\n" +
                $"migrated_count: {migratedCount}\n" +
                $"note: {note}\n";
            await File.WriteAllTextAsync(_sentinelPath, contents).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Failure to write the sentinel means we'll re-run the
            // migration on next boot, which is the safest failure mode.
            _logger.LogWarning(ex, "memos.migration.sentinel_write_failed path={Path}", _sentinelPath);
        }
    }
}
