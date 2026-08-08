using Compendio.Application.Abstractions;
using Compendio.Domain;
using Compendio.Domain.Content;
using Compendio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Compendio.Engine;

/// <param name="Added">Files on disk with no database row.</param>
/// <param name="Updated">Rows whose content hash no longer matches the file.</param>
/// <param name="Removed">Rows whose file is gone.</param>
/// <param name="ParseFailures">Files that could not be read at all, with their paths.</param>
public sealed record ReconciliationReport(int Added, int Updated, int Removed, IReadOnlyList<string> ParseFailures)
{
    public bool FoundDrift => Added > 0 || Updated > 0 || Removed > 0;
}

/// <summary>
/// Walks the whole content folder and makes the database agree with it.
/// </summary>
/// <remarks>
/// The safety net under the watcher. Because the folder is the source of truth, a full pass can
/// always repair any drift — dropped watcher events, a crash mid-batch, files copied in while the
/// service was stopped, a restore from backup. It runs at startup and on demand, and it is the
/// reason the watcher is allowed to be best-effort.
/// </remarks>
public sealed class Reconciler(
    IDbContextFactory<CompendioDbContext> dbFactory,
    IContentStore store,
    IContentPipeline pipeline,
    IPermissionEvaluator permissions,
    ILogger<Reconciler> logger)
{
    public async Task<ReconciliationReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var added = 0;
        var updated = 0;
        var failures = new List<string>();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var known = await db.Pages
            .AsNoTracking()
            .Select(p => new { p.Path, p.ContentHash })
            .ToDictionaryAsync(p => p.Path, p => p.ContentHash, StringComparer.Ordinal, cancellationToken);

        var seen = new HashSet<string>(StringComparer.Ordinal);

        var disk = new List<ContentEntry>();
        await foreach (var entry in store.EnumerateAsync(ContentPath.Root, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            disk.Add(entry);
        }

        // Before anything is ingested, because after it there is nothing left to correlate: the new
        // path already has rows and the old one is already gone.
        if (await CorrelateFolderRenamesAsync(db, disk, known, cancellationToken))
        {
            known = await db.Pages
                .AsNoTracking()
                .Select(p => new { p.Path, p.ContentHash })
                .ToDictionaryAsync(p => p.Path, p => p.ContentHash, StringComparer.Ordinal, cancellationToken);
        }

        foreach (var entry in disk)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.IsFolder)
            {
                // assets/ is where a page's images live, not a place in the wiki. Giving it a folder
                // row would put an "assets" node in the navigation tree beside every page folder
                // that has ever had an image pasted into it.
                if (!PathPolicy.IsAssets(entry.Path))
                {
                    await pipeline.EnsureFolderAsync(entry.Path, cancellationToken);
                }

                continue;
            }

            if (entry.Path.Extension != CompendioConstants.MarkdownExtension)
            {
                await pipeline.IngestChangeAsync(entry.Path, cancellationToken);
                continue;
            }

            seen.Add(entry.Path.Value);

            try
            {
                var hash = await store.HashAsync(entry.Path, cancellationToken);
                if (hash is null)
                {
                    continue;
                }

                if (!known.TryGetValue(entry.Path.Value, out var storedHash))
                {
                    await pipeline.IngestChangeAsync(entry.Path, cancellationToken);
                    added++;
                }
                else if (!string.Equals(storedHash, hash, StringComparison.OrdinalIgnoreCase))
                {
                    await pipeline.IngestChangeAsync(entry.Path, cancellationToken);
                    updated++;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // Reported by path, never by content: `doctor` prints these and must not print
                // anything from inside a file.
                failures.Add(entry.Path.Value);
                logger.LogWarning("Could not reconcile '{Path}': {Reason}", entry.Path.Value, e.GetType().Name);
            }
        }

        var removed = 0;
        foreach (var path in known.Keys.Where(p => !seen.Contains(p)))
        {
            await pipeline.IngestDeleteAsync(ContentPath.FromTrusted(path), cancellationToken);
            removed++;
        }

        await PruneMissingFoldersAsync(cancellationToken);
        permissions.Invalidate();

        var report = new ReconciliationReport(added, updated, removed, failures);

        if (report.FoundDrift)
        {
            logger.LogInformation(
                "Reconciliation: {Added} added, {Updated} updated, {Removed} removed, {Failures} unreadable.",
                added, updated, removed, failures.Count);
        }

        return report;
    }

    /// <summary>
    /// Correlates a folder that disappeared with one that appeared, and moves it rather than
    /// rebuilding it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is criterion 9's first half: renaming a restricted folder on disk keeps it restricted.
    /// Without correlation a rename is a delete plus a create — the old path's access rules are
    /// tombstoned and the new path inherits from its parent, so a folder that was visible to three
    /// people becomes visible to everybody, with the same documents still in it. The pages also lose
    /// their identity, and with it their history.
    /// </para>
    /// <para>
    /// The evidence required is deliberately strong: the same set of page paths, relative to the
    /// folder, with the same content hashes. Two folders that match on that are the same folder. A
    /// folder holding no pages cannot be correlated by content and is not guessed at — it is
    /// tombstoned as before, which is the safe direction.
    /// </para>
    /// </remarks>
    private async Task<bool> CorrelateFolderRenamesAsync(
        CompendioDbContext db,
        IReadOnlyList<ContentEntry> disk,
        IReadOnlyDictionary<string, string> known,
        CancellationToken cancellationToken)
    {
        var diskFolders = disk
            .Where(e => e.IsFolder && !PathPolicy.IsAssets(e.Path))
            .Select(e => e.Path.Value)
            .ToHashSet(StringComparer.Ordinal);

        var storedFolders = await db.Folders
            .AsNoTracking()
            .Where(f => f.Path != string.Empty)
            .Select(f => f.Path)
            .ToListAsync(cancellationToken);

        var vanished = storedFolders.Where(p => !diskFolders.Contains(p)).ToList();
        if (vanished.Count == 0)
        {
            return false;
        }

        var appeared = diskFolders.Where(p => !storedFolders.Contains(p, StringComparer.Ordinal)).ToList();
        if (appeared.Count == 0)
        {
            return false;
        }

        var diskPages = disk
            .Where(e => !e.IsFolder && e.Path.Extension == CompendioConstants.MarkdownExtension)
            .Select(e => e.Path.Value)
            .ToList();

        var correlated = false;
        var handled = new List<string>();

        // Shallowest first: rebasing a parent carries its children with it, so a nested folder is
        // already dealt with by the time its own turn would come.
        foreach (var from in vanished.OrderBy(p => p.Length))
        {
            if (handled.Any(h => from.StartsWith(h + "/", StringComparison.Ordinal)))
            {
                continue;
            }

            var contents = RelativePages(known.Keys, from);
            if (contents.Count == 0)
            {
                continue;
            }

            foreach (var to in appeared)
            {
                if (to.StartsWith(from + "/", StringComparison.Ordinal) ||
                    from.StartsWith(to + "/", StringComparison.Ordinal))
                {
                    continue;
                }

                var candidate = RelativePages(diskPages, to);
                if (candidate.Count != contents.Count || !candidate.SetEquals(contents))
                {
                    continue;
                }

                if (!await ContentMatchesAsync(known, from, to, contents, cancellationToken))
                {
                    continue;
                }

                logger.LogInformation(
                    "Correlated an external folder rename: '{From}' → '{To}'. Its access rules move with it.",
                    from, to);

                await pipeline.IngestFolderMoveAsync(
                    ContentPath.FromTrusted(from), ContentPath.FromTrusted(to), cancellationToken);

                appeared.Remove(to);
                handled.Add(from);
                correlated = true;
                break;
            }
        }

        return correlated;
    }

    /// <summary>Page paths under <paramref name="folder"/>, relative to it.</summary>
    private static HashSet<string> RelativePages(IEnumerable<string> paths, string folder)
    {
        var prefix = folder + "/";

        return paths
            .Where(p => p.StartsWith(prefix, StringComparison.Ordinal))
            .Select(p => p[prefix.Length..])
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Confirms every file matches by content hash, not only by name.
    /// </summary>
    /// <remarks>
    /// Names alone would correlate two folders that happen to hold <c>readme.md</c> and nothing
    /// else. Hashing is affordable here because it only runs when a folder actually vanished, which
    /// is rare, and only over that folder's own files.
    /// </remarks>
    private async Task<bool> ContentMatchesAsync(
        IReadOnlyDictionary<string, string> known,
        string from,
        string to,
        HashSet<string> relativePaths,
        CancellationToken cancellationToken)
    {
        foreach (var relative in relativePaths)
        {
            if (!known.TryGetValue($"{from}/{relative}", out var expected))
            {
                return false;
            }

            // Somebody — an earlier pass, the watcher — already gave the destination its own page
            // rows. Rebasing onto them would collide on the unique path index, so this is left to
            // the ordinary added/removed handling instead.
            if (known.ContainsKey($"{to}/{relative}"))
            {
                return false;
            }

            var actual = await store.HashAsync(ContentPath.FromTrusted($"{to}/{relative}"), cancellationToken);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Removes folder rows whose directory is gone. Their ACLs are tombstoned rather than deleted,
    /// by the same rule that governs a deleted folder.
    /// </summary>
    private async Task PruneMissingFoldersAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var folders = await db.Folders.Select(f => f.Path).ToListAsync(cancellationToken);

        // Gone from disk, plus any assets/ row an earlier version left behind — the same treatment
        // either way, because neither belongs in the tree.
        var missing = folders
            .Where(p => p.Length > 0 &&
                        (!store.FolderExists(ContentPath.FromTrusted(p)) ||
                         PathPolicy.IsAssets(ContentPath.FromTrusted(p))))
            .ToList();

        if (missing.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;

        await db.AclNodes
            .Where(n => missing.Contains(n.FolderPath) && n.TombstonedAt == null)
            .ExecuteUpdateAsync(n => n.SetProperty(x => x.TombstonedAt, now), cancellationToken);

        // Deepest first, so a parent is never deleted before its children.
        foreach (var path in missing.OrderByDescending(p => p.Length))
        {
            await db.Folders.Where(f => f.Path == path).ExecuteDeleteAsync(cancellationToken);
        }
    }
}
