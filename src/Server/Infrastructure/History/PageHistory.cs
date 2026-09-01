using System.IO.Compression;
using System.Net;
using System.Text;
using Compendio.Application.Abstractions;
using Compendio.Domain.Content;
using Compendio.Domain.Entities;
using Compendio.Hosting.Configuration;
using Compendio.Infrastructure.Persistence;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Compendio.Infrastructure.History;

/// <summary>
/// Snapshots, diffs and retention.
/// </summary>
/// <remarks>
/// <para>
/// Snapshots are keyed by page identity rather than path, so history survives a rename. A restore
/// writes a <em>new</em> version rather than rewinding, so a mistaken restore is itself undoable —
/// which is the difference between a safety net and a second way to lose work.
/// </para>
/// <para>
/// Inside a secure scope the stored bytes are compressed and then encrypted with the scope key, and
/// diffs are computed in memory. Retrofitting that later would have been a data migration, which is
/// why <c>ContentCrypto</c> lands after history in the build order rather than before it.
/// </para>
/// </remarks>
public sealed class PageHistory(
    IDbContextFactory<CompendioDbContext> dbFactory,
    IContentCrypto crypto,
    ISecureScopeRegistry secureScopes,
    Application.Abstractions.IMarkdownRenderer renderer,
    IClock clock,
    IOptions<CompendioOptions> options,
    ILogger<PageHistory> logger) : IPageHistory
{
    private readonly HistoryOptions _history = options.Value.History;

    /// <summary>
    /// Attempts before giving up on allocating a sequence.
    /// </summary>
    /// <remarks>
    /// Generous because the contention is short-lived — the losing writer re-reads a number and
    /// inserts again — and because failing here means losing a version of somebody's page.
    /// </remarks>
    private const int SequenceAttempts = 5;

    /// <summary>
    /// Snapshots a page, retrying if another writer took the sequence first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Sequence</c> is allocated by reading the current maximum and adding one, against a unique
    /// index on <c>(PageId, Sequence)</c>. That is a read-then-write race, and it has more than one
    /// writer in ordinary use: a save, the watcher ingesting the file that save just wrote, and a
    /// reconciliation pass can all snapshot the same page within milliseconds of each other.
    /// </para>
    /// <para>
    /// Unhandled, the loser's insert violates the constraint and the user sees a 500 on a save that
    /// otherwise worked — intermittently, which is the worst kind. Retrying is the whole fix: the
    /// second read sees the winner's row and picks the next number. The duplicate-content check
    /// re-runs each time, so a writer that lost to somebody storing identical bytes stops rather
    /// than adding a second identical version.
    /// </para>
    /// </remarks>
    public async Task SnapshotAsync(
        Page page,
        byte[] content,
        VersionSource source,
        Guid? authorUserId,
        string? note,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await TrySnapshotAsync(page, content, source, authorUserId, note, at, cancellationToken);
                return;
            }
            catch (DbUpdateException e) when (IsUniqueViolation(e) && attempt < SequenceAttempts)
            {
                logger.LogDebug(
                    "Another writer took sequence for '{Path}'; retrying ({Attempt}/{Max}).",
                    page.Path, attempt, SequenceAttempts);
            }
        }
    }

    /// <summary>SQLite reports a violated unique index as error 19 (constraint).</summary>
    private static bool IsUniqueViolation(DbUpdateException e) =>
        e.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 };

    private async Task TrySnapshotAsync(
        Page page,
        byte[] content,
        VersionSource source,
        Guid? authorUserId,
        string? note,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var last = await db.PageVersions
            .Where(v => v.PageId == page.Id)
            .OrderByDescending(v => v.Sequence)
            .Select(v => new { v.Sequence, v.ContentHash })
            .FirstOrDefaultAsync(cancellationToken);

        var hash = Content.ContentStore.Hash(content);

        // The watcher fires several events per save. An identical snapshot is noise, not history —
        // except when the event itself is the history: a move, a delete, or a restore that brings
        // back exactly the bytes the delete recorded still has to say it happened.
        if (last is not null && string.Equals(last.ContentHash, hash, StringComparison.OrdinalIgnoreCase)
                             && source is not (VersionSource.Move or VersionSource.Delete or VersionSource.Restore))
        {
            return;
        }

        var compressed = Compress(content);
        var scope = await secureScopes.ScopeForAsync(ContentPath.FromTrusted(page.Path), cancellationToken);

        byte[] stored;
        Guid? keyId = null;

        if (scope is null)
        {
            stored = compressed;
        }
        else
        {
            var path = ContentPath.FromTrusted(page.Path);
            stored = await crypto.EncryptAsync(scope.Value, path, compressed, cancellationToken);
            keyId = Domain.Security.CryptoEnvelope.TryReadHeader(stored, out var header) ? header.KeyId : null;
        }

        db.PageVersions.Add(new PageVersion
        {
            Id = Guid.CreateVersion7(),
            PageId = page.Id,
            Sequence = (last?.Sequence ?? 0) + 1,
            // An external edit is recorded as an external edit and attributed to nobody. Crediting
            // it to the last signed-in user is a small lie that makes the audit trail worthless.
            AuthorUserId = source == VersionSource.External ? null : authorUserId,
            Source = source,
            CreatedAt = at,
            ContentHash = hash,
            ByteSize = content.LongLength,
            Note = note,
            Content = stored,
            IsEncrypted = scope is not null,
            KeyId = keyId,
            Path = page.Path,
        });

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<VersionSummary>> ListAsync(Guid pageId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var versions = await db.PageVersions
            .Where(v => v.PageId == pageId)
            .OrderByDescending(v => v.Sequence)
            .Select(v => new
            {
                v.Id, v.Sequence, v.CreatedAt, v.AuthorUserId, v.Source,
                v.ContentHash, v.ByteSize, v.Note, v.Path,
            })
            .ToListAsync(cancellationToken);

        var authorIds = versions.Where(v => v.AuthorUserId is not null).Select(v => v.AuthorUserId!.Value).Distinct().ToList();
        var authors = await db.Users
            .Where(u => authorIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken);

        return versions.Select(v => new VersionSummary(
                v.Id, v.Sequence, v.CreatedAt, v.AuthorUserId,
                v.AuthorUserId is { } id && authors.TryGetValue(id, out var name) ? name : null,
                v.Source, v.ContentHash, v.ByteSize, v.Note, v.Path))
            .ToArray();
    }

    public async Task<string?> ContentAsync(Guid versionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var version = await db.PageVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == versionId, cancellationToken);
        return version is null ? null : await DecodeAsync(version, cancellationToken);
    }

    public async Task<PageDiff?> DiffAsync(Guid fromVersionId, Guid toVersionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var from = await db.PageVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == fromVersionId, cancellationToken);
        var to = await db.PageVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == toVersionId, cancellationToken);

        if (from is null || to is null || from.PageId != to.PageId)
        {
            return null;
        }

        var left = await DecodeAsync(from, cancellationToken) ?? string.Empty;
        var right = await DecodeAsync(to, cancellationToken) ?? string.Empty;

        var summaries = await ListAsync(from.PageId, cancellationToken);
        var fromSummary = summaries.First(s => s.Id == from.Id);
        var toSummary = summaries.First(s => s.Id == to.Id);

        var source = BuildSourceDiff(left, right, out var added, out var removed);
        var rendered = BuildRenderedDiff(left, right, ContentPath.FromTrusted(to.Path));

        return new PageDiff(fromSummary, toSummary, source, rendered, added, removed);
    }

    public async Task TombstoneAsync(Guid pageId, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.PageVersions
            .Where(v => v.PageId == pageId && v.TombstonedAt == null)
            .ExecuteUpdateAsync(v => v.SetProperty(x => x.TombstonedAt, at), cancellationToken);
    }

    public async Task ReviveAsync(Guid pageId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.PageVersions
            .Where(v => v.PageId == pageId && v.TombstonedAt != null)
            .ExecuteUpdateAsync(v => v.SetProperty(x => x.TombstonedAt, (DateTimeOffset?)null), cancellationToken);
    }

    /// <summary>
    /// Keeps everything inside the retention window, then one version per day, never dropping below
    /// the floor. Expired tombstones are purged.
    /// </summary>
    public async Task<int> ApplyRetentionAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var now = clock.UtcNow;
        var cutoff = now.AddDays(-_history.RetentionDays);
        var deletedCutoff = now.AddDays(-_history.DeletedRetentionDays);

        var purged = await db.PageVersions
            .Where(v => v.TombstonedAt != null && v.TombstonedAt < deletedCutoff)
            .ExecuteDeleteAsync(cancellationToken);

        var pageIds = await db.PageVersions
            .Where(v => v.CreatedAt < cutoff && v.TombstonedAt == null)
            .Select(v => v.PageId)
            .Distinct()
            .ToListAsync(cancellationToken);

        foreach (var pageId in pageIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var versions = await db.PageVersions
                .Where(v => v.PageId == pageId)
                .OrderByDescending(v => v.Sequence)
                .Select(v => new { v.Id, v.Sequence, v.CreatedAt })
                .ToListAsync(cancellationToken);

            var keep = new HashSet<Guid>(versions.Take(_history.MinVersionsKept).Select(v => v.Id));
            var seenDays = new HashSet<DateOnly>();

            foreach (var version in versions)
            {
                if (version.CreatedAt >= cutoff)
                {
                    keep.Add(version.Id);
                    continue;
                }

                // Outside the window: the newest version of each day survives.
                if (seenDays.Add(DateOnly.FromDateTime(version.CreatedAt.UtcDateTime)))
                {
                    keep.Add(version.Id);
                }
            }

            var drop = versions.Where(v => !keep.Contains(v.Id)).Select(v => v.Id).ToList();
            if (drop.Count == 0)
            {
                continue;
            }

            purged += await db.PageVersions
                .Where(v => drop.Contains(v.Id))
                .ExecuteDeleteAsync(cancellationToken);
        }

        if (purged > 0)
        {
            logger.LogInformation("History retention removed {Count} version(s).", purged);
        }

        return purged;
    }

    /// <summary>
    /// Encrypts the versions already stored under a folder that has just become a secure scope.
    /// </summary>
    /// <remarks>
    /// The stored bytes are compressed and then encrypted, which is the same order
    /// <see cref="SnapshotAsync"/> uses — so this wraps what is already there rather than
    /// decompressing and re-doing the work, and a version encrypted here is indistinguishable from
    /// one written after the scope existed.
    /// </remarks>
    public async Task<int> EncryptExistingAsync(ContentPath scope, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var prefix = scope.Value + "/";
        var versions = await db.PageVersions
            .Where(v => !v.IsEncrypted && (v.Path == scope.Value || v.Path.StartsWith(prefix)))
            .ToListAsync(cancellationToken);

        if (versions.Count == 0)
        {
            return 0;
        }

        foreach (var version in versions)
        {
            var path = ContentPath.FromTrusted(version.Path);
            version.Content = await crypto.EncryptAsync(scope, path, version.Content, cancellationToken);
            version.IsEncrypted = true;
            version.KeyId = Domain.Security.CryptoEnvelope.TryReadHeader(version.Content, out var header)
                ? header.KeyId
                : null;
        }

        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Encrypted {Count} stored version(s) under the new secure scope '{Scope}'.", versions.Count, scope.Value);

        return versions.Count;
    }

    private async Task<string?> DecodeAsync(PageVersion version, CancellationToken cancellationToken)
    {
        var bytes = version.Content;

        if (version.IsEncrypted)
        {
            var path = ContentPath.FromTrusted(version.Path);
            var scope = await secureScopes.ScopeForAsync(path, cancellationToken);
            if (scope is null)
            {
                logger.LogWarning("Version {Id} is encrypted but its scope no longer exists.", version.Id);
                return null;
            }

            bytes = await crypto.DecryptAsync(scope.Value, path, bytes, cancellationToken);
        }

        return Encoding.UTF8.GetString(Decompress(bytes));
    }

    private static List<DiffLine> BuildSourceDiff(string left, string right, out int added, out int removed)
    {
        var builder = new InlineDiffBuilder(new Differ());
        var model = builder.BuildDiffModel(left, right, ignoreWhitespace: false);

        var lines = new List<DiffLine>(model.Lines.Count);
        added = 0;
        removed = 0;

        foreach (var line in model.Lines)
        {
            var kind = line.Type switch
            {
                ChangeType.Inserted => "added",
                ChangeType.Deleted => "removed",
                ChangeType.Modified => "modified",
                ChangeType.Imaginary => "unchanged",
                _ => "unchanged",
            };

            if (kind == "added")
            {
                added++;
            }
            else if (kind == "removed")
            {
                removed++;
            }

            var pieces = line.SubPieces.Count == 0
                ? Array.Empty<DiffSpan>()
                : line.SubPieces
                    .Where(p => p.Text is not null)
                    .Select(p => new DiffSpan(
                        p.Type switch
                        {
                            ChangeType.Inserted => "added",
                            ChangeType.Deleted => "removed",
                            _ => "unchanged",
                        },
                        p.Text!))
                    .ToArray();

            lines.Add(new DiffLine(kind, null, line.Position, line.Text ?? string.Empty, pieces));
        }

        return lines;
    }

    /// <summary>
    /// Block-level added/removed/changed over the rendered HTML, with inline word highlighting.
    /// </summary>
    /// <remarks>
    /// Not a full HTML tree diff: this compares the source blocks, renders each side, and marks
    /// whole blocks. A tree diff would be more precise and far more fragile, and the audience for
    /// this view wants "this paragraph changed", not a DOM patch.
    /// </remarks>
    private string BuildRenderedDiff(string left, string right, ContentPath path)
    {
        var leftBlocks = SplitBlocks(MarkdownParser.Parse(left).Body);
        var rightBlocks = SplitBlocks(MarkdownParser.Parse(right).Body);

        var model = new InlineDiffBuilder(new Differ())
            .BuildDiffModel(string.Join('\n', leftBlocks), string.Join('\n', rightBlocks), ignoreWhitespace: true);

        var html = new StringBuilder("<div class=\"compendio-rendered-diff\">");

        foreach (var line in model.Lines)
        {
            // Restore the line breaks SplitBlocks folded away. The sentinel matters: folding on a
            // space and expanding it back would put every *word* on its own line, turning a table
            // or a list into nonsense the moment it is rendered.
            var block = (line.Text ?? string.Empty).Replace(LineSentinel, '\n');
            if (block.Trim().Length == 0)
            {
                continue;
            }

            var cssClass = line.Type switch
            {
                ChangeType.Inserted => "diff-added",
                ChangeType.Deleted => "diff-removed",
                ChangeType.Modified => "diff-modified",
                _ => "diff-unchanged",
            };

            var rendered = renderer.Render(block, path, _ => null).Html;
            html.Append("<section class=\"").Append(cssClass).Append("\">").Append(rendered).Append("</section>");
        }

        html.Append("</div>");

        // Sanitized again on the way out: the block text came from stored page content.
        return renderer.Sanitize(html.ToString());
    }

    /// <summary>
    /// Stands in for the newlines inside a block, so the whole block is one "line" to the diff
    /// algorithm and survives the round trip back out.
    /// </summary>
    /// <remarks>
    /// U+001F (unit separator) is a control character. <see cref="PathPolicy"/> rejects control
    /// characters in paths, and no editor emits one into body text, so it cannot collide with real
    /// content — which a space very much can.
    /// </remarks>
    private const char LineSentinel = '\u001F';

    /// <summary>
    /// Splits Markdown into blocks on blank lines, encoding internal newlines so each block is one
    /// "line" for the diff algorithm.
    /// </summary>
    private static List<string> SplitBlocks(string markdown)
    {
        var blocks = new List<string>();
        var current = new StringBuilder();

        foreach (var line in MarkdownDocument.EnumerateLines(markdown))
        {
            if (line.Trim().Length == 0)
            {
                if (current.Length > 0)
                {
                    blocks.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            if (current.Length > 0)
            {
                current.Append(LineSentinel);
            }

            // A stray sentinel in the source would corrupt the round trip, so it is neutralized.
            current.Append(line.Replace(LineSentinel, ' '));
        }

        if (current.Length > 0)
        {
            blocks.Add(current.ToString());
        }

        return blocks;
    }

    /// <summary>Brotli. Markdown compresses very well and history is the largest table by far.</summary>
    private static byte[] Compress(byte[] content)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            brotli.Write(content);
        }

        return output.ToArray();
    }

    private static byte[] Decompress(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        brotli.CopyTo(output);
        return output.ToArray();
    }
}