using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Domain;
using Compendio.Domain.Content;
using Compendio.Domain.Entities;
using Compendio.Infrastructure.Content;
using Compendio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Compendio.Engine;

/// <summary>
/// Applies every content change, in one order.
/// </summary>
/// <remarks>
/// The order is: disk → page and folder rows → history snapshot → ACL path maintenance → index
/// queue. It matters. Writing the index before the page row means a search hit pointing at a page
/// that is not there yet; snapshotting before the disk write means history containing a version
/// that never existed.
/// </remarks>
public sealed class ContentPipeline(
    IDbContextFactory<CompendioDbContext> dbFactory,
    IContentStore store,
    IPathPolicy paths,
    ISecureScopeRegistry secureScopes,
    IPermissionEvaluator permissions,
    IPageHistory history,
    ChangeNotifier changeNotifier,
    IClock clock,
    ILogger<ContentPipeline> logger) : IContentPipeline
{
    public async Task<Page> SavePageAsync(
        ContentPath path,
        byte[] content,
        string? expectedHash,
        Guid? actorUserId,
        VersionSource source = VersionSource.Editor,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        var file = await store.WriteAsync(path, content, expectedHash, cancellationToken);

        return await SyncAsync(path, file, actorUserId, source, note, markCanonical: true, cancellationToken);
    }

    public async Task<Page> RecordSavedAsync(
        ContentPath path,
        Guid? actorUserId,
        string? note = null,
        CancellationToken cancellationToken = default)
    {
        var file = await store.ReadAsync(path, cancellationToken) ?? throw CompendioException.NotFound(path);

        // markCanonical stays false: a checkbox substitution does not make a page canonical, and
        // claiming it did would skip the one-time normalization the next real save owes it.
        return await SyncAsync(path, file, actorUserId, VersionSource.Editor, note,
            markCanonical: false, cancellationToken);
    }

    public async Task DeletePageAsync(ContentPath path, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        var existing = await store.ReadAsync(path, cancellationToken);
        await store.DeleteAsync(path, cancellationToken);
        await RemovePageRowAsync(path, existing?.Bytes, actorUserId, cancellationToken);
    }

    public async Task<Page> MovePageAsync(ContentPath from, ContentPath to, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        // Announced before the move so the watcher recognizes both halves as ours. Without this it
        // correlates the delete and the create into a second, redundant move a moment later.
        var moving = await store.ReadAsync(from, cancellationToken);
        if (moving is not null)
        {
            store.ExpectOwnWrite(to, moving.ContentHash);
            store.ExpectOwnWrite(from, moving.ContentHash);
        }

        await store.MoveAsync(from, to, cancellationToken);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var page = await db.Pages.FirstOrDefaultAsync(p => p.Path == from.Value, cancellationToken);

        if (page is null)
        {
            // Nothing tracked at the old path — treat the destination as a fresh ingest so the row
            // exists rather than silently going missing.
            await IngestChangeAsync(to, cancellationToken);
            return await RequirePageAsync(to, cancellationToken);
        }

        var folder = await EnsureFolderRowAsync(db, to.Parent, cancellationToken);
        page.Path = to.Value;
        page.Slug = to.NameWithoutExtension;
        page.FolderId = folder.Id;
        page.IsSecure = await secureScopes.IsSecureAsync(to, cancellationToken);
        page.UpdatedAt = clock.UtcNow;
        page.UpdatedByUserId = actorUserId;
        page.LastEditWasExternal = false;

        await db.SaveChangesAsync(cancellationToken);

        var file = await store.ReadAsync(to, cancellationToken);
        if (file is not null)
        {
            // The snapshot carries the page's identity, so history follows the move rather than
            // ending at the old path and starting fresh at the new one.
            await history.SnapshotAsync(page, file.Bytes, VersionSource.Move, actorUserId,
                $"{from.Value} → {to.Value}", clock.UtcNow, cancellationToken);
        }

        await EnqueueAsync(db, to, page.Id, IndexOperation.Move, from, cancellationToken);
        return page;
    }

    public async Task<Page> RestoreDeletedPageAsync(
        Guid pageId,
        ContentPath path,
        byte[] content,
        Guid? actorUserId,
        CancellationToken cancellationToken = default)
    {
        if (store.Exists(path))
        {
            throw CompendioException.Exists(path);
        }

        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            if (await db.Pages.AnyAsync(p => p.Id == pageId || p.Path == path.Value, cancellationToken))
            {
                throw CompendioException.Exists(path);
            }

            // The row goes in first, under the old id, so the save below finds it by path and
            // updates it rather than minting a new identity. Everything else on it is overwritten by
            // that save; only the id and the path have to be right here.
            var folder = await EnsureFolderRowAsync(db, path.Parent, cancellationToken);
            db.Pages.Add(new Page
            {
                Id = pageId,
                Path = path.Value,
                FolderId = folder.Id,
                Slug = path.NameWithoutExtension,
                Title = path.NameWithoutExtension,
                ContentHash = string.Empty,
                UpdatedAt = clock.UtcNow,
            });

            await db.SaveChangesAsync(cancellationToken);
        }

        await history.ReviveAsync(pageId, cancellationToken);

        // Recorded as a restore, so history says what happened: "restored" after "deleted", not an
        // unexplained edit.
        var page = await SavePageAsync(path, content, expectedHash: null, actorUserId,
            VersionSource.Restore, note: "restore:deleted", cancellationToken);

        await AuditAsync("page.restore_deleted", "page", path.Value, actorUserId, before: null, after: path.Value, cancellationToken);
        return page;
    }

    public async Task<Folder> EnsureFolderAsync(ContentPath path, CancellationToken cancellationToken = default)
    {
        await store.CreateFolderAsync(path, cancellationToken);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var folder = await EnsureFolderRowAsync(db, path, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        permissions.Invalidate();
        return folder;
    }

    public async Task DeleteFolderAsync(ContentPath path, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            var pages = await db.Pages
                .Where(p => p.Path.StartsWith(path.Value + "/"))
                .Select(p => p.Path)
                .ToListAsync(cancellationToken);

            foreach (var pagePath in pages)
            {
                await RemovePageRowAsync(ContentPath.FromTrusted(pagePath), content: null, actorUserId, cancellationToken);
            }
        }

        await store.DeleteFolderAsync(path, recursive: true, cancellationToken);
        await TombstoneAclsAsync(path, cancellationToken);

        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            var doomed = await db.Folders
                .Where(f => f.Path == path.Value || f.Path.StartsWith(path.Value + "/"))
                .Select(f => f.Path)
                .ToListAsync(cancellationToken);

            // Deepest first. Folders.ParentId is ON DELETE RESTRICT, and SQLite enforces that per
            // row inside a single DELETE — so removing a parent while its child row still exists
            // fails the whole statement.
            foreach (var folderPath in doomed.OrderByDescending(p => p.Length))
            {
                await db.Folders.Where(f => f.Path == folderPath).ExecuteDeleteAsync(cancellationToken);
            }
        }

        permissions.Invalidate();
    }

    public async Task MoveFolderAsync(ContentPath from, ContentPath to, Guid? actorUserId, CancellationToken cancellationToken = default)
    {
        await store.MoveFolderAsync(from, to, cancellationToken);
        await RebaseFolderAsync(from, to, actorUserId, "folder.move", cancellationToken);
    }

    /// <summary>
    /// A folder that moved on disk without us moving it — the database side only.
    /// </summary>
    /// <remarks>
    /// Identical bookkeeping to <see cref="MoveFolderAsync"/> and deliberately the same code: the
    /// access rules have to travel with the folder whether the move came from the UI or from
    /// somebody renaming it in Explorer, and two implementations of "move a folder's rows" is how
    /// one of them ends up forgetting the ACL.
    /// </remarks>
    public Task IngestFolderMoveAsync(ContentPath from, ContentPath to, CancellationToken cancellationToken = default) =>
        RebaseFolderAsync(from, to, actorUserId: null, "folder.move.external", cancellationToken);

    private async Task RebaseFolderAsync(
        ContentPath from,
        ContentPath to,
        Guid? actorUserId,
        string auditAction,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // Every row this rebases is keyed by a unique path, so a destination that already has rows
        // would fail the move half-way through. That only happens when something already ingested
        // the new location as a new folder, and the reconciliation pass is what repairs that.
        // Compared ordinally in memory: SQLite's LIKE, which StartsWith becomes, ignores ASCII case,
        // and a rename from "ops" to "Ops" would otherwise find its own pages at the destination and
        // refuse to move them.
        var prefix = to.Value + "/";
        var occupied = await db.Folders.AnyAsync(f => f.Path == to.Value, cancellationToken)
                       || (await db.Pages.Where(p => p.Path.StartsWith(prefix)).Select(p => p.Path).ToListAsync(cancellationToken))
                           .Any(p => p.StartsWith(prefix, StringComparison.Ordinal));

        if (occupied)
        {
            logger.LogWarning(
                "Not moving '{From}' onto '{To}': the destination already has rows. Reconciliation will sort it out.",
                from.Value, to.Value);
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The ACL move and the tree move are one transaction. Split, a crash between them leaves a
        // restricted folder at a path whose ACL row no longer matches — that is, wide open.
        var folders = await db.Folders
            .Where(f => f.Path == from.Value || f.Path.StartsWith(from.Value + "/"))
            .ToListAsync(cancellationToken);

        foreach (var folder in folders)
        {
            var rebased = ContentPath.FromTrusted(folder.Path).Rebase(from, to);
            folder.Path = rebased.Value;
            folder.Name = rebased.IsRoot ? string.Empty : rebased.Name;
        }

        var pages = await db.Pages
            .Where(p => p.Path.StartsWith(from.Value + "/"))
            .ToListAsync(cancellationToken);

        foreach (var page in pages)
        {
            var oldPath = ContentPath.FromTrusted(page.Path);
            var rebased = oldPath.Rebase(from, to);
            page.Path = rebased.Value;
            page.IsSecure = await secureScopes.IsSecureAsync(rebased, cancellationToken);
            await EnqueueAsync(db, rebased, page.Id, IndexOperation.Move, oldPath, cancellationToken);
        }

        var acls = await db.AclNodes
            .Where(n => n.FolderPath == from.Value || n.FolderPath.StartsWith(from.Value + "/"))
            .ToListAsync(cancellationToken);

        foreach (var acl in acls)
        {
            acl.FolderPath = ContentPath.FromTrusted(acl.FolderPath).Rebase(from, to).Value;
            acl.UpdatedAt = clock.UtcNow;
            acl.UpdatedByUserId = actorUserId;
        }

        var scopes = await db.SecureScopes
            .Where(s => s.FolderPath == from.Value || s.FolderPath.StartsWith(from.Value + "/"))
            .ToListAsync(cancellationToken);

        foreach (var scope in scopes)
        {
            scope.FolderPath = ContentPath.FromTrusted(scope.FolderPath).Rebase(from, to).Value;
        }

        await EnsureFolderRowAsync(db, to.Parent, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        secureScopes.Invalidate();
        permissions.Invalidate();

        await AuditAsync(auditAction, "folder", to.Value, actorUserId, from.Value, to.Value, cancellationToken);
    }

    // ---- File-system side -----------------------------------------------------------------------

    public async Task IngestChangeAsync(ContentPath path, CancellationToken cancellationToken = default)
    {
        if (PathPolicy.IsIgnored(path))
        {
            return;
        }

        // A file inside assets/ is an attachment whatever its extension. Without this check a
        // Markdown file dropped in there becomes a page in the tree, which is not what anybody
        // putting it beside an image meant.
        if (path.Extension != CompendioConstants.MarkdownExtension || IsInsideAssets(path))
        {
            await IngestAttachmentAsync(path, cancellationToken);
            return;
        }

        var file = await store.ReadAsync(path, cancellationToken);
        if (file is null)
        {
            await IngestDeleteAsync(path, cancellationToken);
            return;
        }

        if (store.WasOwnWrite(path, file.ContentHash))
        {
            // Our own save coming back round. Ignoring it is what stops the index-rebuild loop.
            return;
        }

        file = await EncryptIfDroppedInSecureScopeAsync(path, file, cancellationToken);

        await SyncAsync(path, file, actorUserId: null, VersionSource.External, note: null,
            markCanonical: false, cancellationToken);
    }

    /// <summary>
    /// A plaintext page that appeared inside a secure scope is an ingest: encrypt it, remove the
    /// plaintext.
    /// </summary>
    /// <remarks>
    /// Somebody copying a Markdown file into an encrypted folder over the share, or restoring one
    /// from a plain backup, has put a secret on disk in the clear. Recording it as a secure page and
    /// leaving the file as it was would show the lock icon over an unencrypted file. The store's
    /// write does both halves — the envelope, and the best-effort shred of the plaintext — so this
    /// only has to notice. A key that is unavailable is logged and left alone rather than failing the
    /// pass: the page is still tracked, still marked secure, and so still kept out of the index.
    /// </remarks>
    private async Task<ContentFile> EncryptIfDroppedInSecureScopeAsync(ContentPath path, ContentFile file, CancellationToken cancellationToken)
    {
        if (file.WasEncrypted || !await secureScopes.IsSecureAsync(path, cancellationToken))
        {
            return file;
        }

        try
        {
            var encrypted = await store.WriteAsync(path, file.Bytes, file.ContentHash, cancellationToken);
            logger.LogInformation("Encrypted '{Path}': a plaintext file appeared inside a secure scope.", path.Value);
            return encrypted with { ModifiedAt = file.ModifiedAt };
        }
        catch (CompendioException e)
        {
            logger.LogWarning("Could not encrypt '{Path}', which appeared in plaintext inside a secure scope: {Code}.", path.Value, e.Code);
            return file;
        }
    }

    public async Task IngestDeleteAsync(ContentPath path, CancellationToken cancellationToken = default)
    {
        if (PathPolicy.IsIgnored(path))
        {
            return;
        }

        await RemovePageRowAsync(path, content: null, actorUserId: null, cancellationToken);
    }

    public async Task IngestMoveAsync(ContentPath from, ContentPath to, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var page = await db.Pages.FirstOrDefaultAsync(p => p.Path == from.Value, cancellationToken);

        if (page is null)
        {
            await IngestChangeAsync(to, cancellationToken);
            return;
        }

        var folder = await EnsureFolderRowAsync(db, to.Parent, cancellationToken);
        page.Path = to.Value;
        page.Slug = to.NameWithoutExtension;
        page.FolderId = folder.Id;
        page.IsSecure = await secureScopes.IsSecureAsync(to, cancellationToken);
        page.UpdatedAt = clock.UtcNow;
        page.LastEditWasExternal = true;
        page.UpdatedByUserId = null;

        await EnqueueAsync(db, to, page.Id, IndexOperation.Move, from, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Correlated an external move: {From} → {To}.", from.Value, to.Value);
    }

    // ---- Shared -----------------------------------------------------------------------------------

    /// <summary>Creates or updates the page row, snapshots history, and queues the index update.</summary>
    private async Task<Page> SyncAsync(
        ContentPath path,
        ContentFile file,
        Guid? actorUserId,
        VersionSource source,
        string? note,
        bool markCanonical,
        CancellationToken cancellationToken)
    {
        var document = MarkdownParser.Parse(file.Text);
        var frontMatter = document.FrontMatter;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var folder = await EnsureFolderRowAsync(db, path.Parent, cancellationToken);

        var page = await db.Pages.FirstOrDefaultAsync(p => p.Path == path.Value, cancellationToken);
        var isNew = page is null;

        page ??= new Page { Id = Guid.CreateVersion7(), Path = path.Value };

        page.FolderId = folder.Id;
        page.Slug = path.NameWithoutExtension;
        page.Title = document.ResolveTitle(path);
        page.Lang = frontMatter.Lang;
        page.TranslationKey = frontMatter.TranslationKey ?? InferTranslationKey(path);
        page.Tags = string.Join(' ', frontMatter.NormalizedTags());
        page.Owner = frontMatter.Owner;
        page.ContentHash = file.ContentHash;
        page.ByteSize = file.ByteSize;
        page.IsSecure = file.WasEncrypted || await secureScopes.IsSecureAsync(path, cancellationToken);
        page.UpdatedAt = source == VersionSource.External ? file.ModifiedAt : clock.UtcNow;
        page.UpdatedByUserId = source == VersionSource.External ? null : actorUserId;
        page.LastEditWasExternal = source == VersionSource.External;
        page.ReviewIntervalDays = frontMatter.ReviewIntervalDays;
        page.NextReviewDate = frontMatter.NextReviewDate;
        page.RequiresAcknowledgment = frontMatter.RequiresAcknowledgment ?? false;

        if (markCanonical)
        {
            page.IsCanonical = true;
        }

        if (isNew)
        {
            db.Pages.Add(page);
        }

        await db.SaveChangesAsync(cancellationToken);

        await history.SnapshotAsync(page, file.Bytes, source, actorUserId, note, page.UpdatedAt, cancellationToken);
        await EnqueueAsync(db, path, page.Id, IndexOperation.Upsert, from: null, cancellationToken);

        await changeNotifier.NotifyAsync(db, page, source, cancellationToken);

        return page;
    }

    private async Task RemovePageRowAsync(ContentPath path, byte[]? content, Guid? actorUserId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var page = await db.Pages.FirstOrDefaultAsync(p => p.Path == path.Value, cancellationToken);

        if (page is null)
        {
            return;
        }

        if (content is not null)
        {
            await history.SnapshotAsync(page, content, VersionSource.Delete, actorUserId, null, clock.UtcNow, cancellationToken);
        }

        // Tombstoned, not dropped. A page deleted by a mis-synced backup client has to be
        // recoverable, and this is the row that makes that possible.
        await history.TombstoneAsync(page.Id, clock.UtcNow, cancellationToken);

        await db.PageTexts.Where(t => t.PageId == page.Id).ExecuteDeleteAsync(cancellationToken);
        await db.Attachments.Where(a => a.PageId == page.Id).ExecuteDeleteAsync(cancellationToken);
        db.Pages.Remove(page);

        await EnqueueAsync(db, path, page.Id, IndexOperation.Delete, from: null, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool IsInsideAssets(ContentPath path) => PathPolicy.IsAssets(path);

    private async Task IngestAttachmentAsync(ContentPath path, CancellationToken cancellationToken)
    {
        if (!path.Parent.Name.Equals(CompendioConstants.AssetsFolderName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await EncryptAttachmentIfDroppedInSecureScopeAsync(path, cancellationToken);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // assets/ sits in the page's folder and is shared by every page in it, so an externally
        // dropped file has no single owner. When exactly one page lives there the answer is obvious
        // and worth recording; when there are several, guessing would attribute somebody's diagram
        // to an unrelated page. Skipping the row costs nothing — the attachment endpoint serves by
        // path and authorizes by folder, so the file is readable either way.
        var folderPath = path.Parent.Parent.Value;
        var folderId = await db.Folders
            .Where(f => f.Path == folderPath)
            .Select(f => (Guid?)f.Id)
            .FirstOrDefaultAsync(cancellationToken);

        var candidates = folderId is { } id
            ? await db.Pages.Where(p => p.FolderId == id).Select(p => p.Id).Take(2).ToListAsync(cancellationToken)
            : [];

        if (candidates.Count != 1)
        {
            logger.LogDebug(
                "Not recording '{Path}' against a page: '{Folder}' holds {Count} page(s).",
                path.Value, folderPath, candidates.Count);
            return;
        }

        var pageId = candidates[0];
        var existing = await db.Attachments.FirstOrDefaultAsync(a => a.Path == path.Value, cancellationToken);
        if (!paths.TryResolve(path, out var absolute) ||
            !(File.Exists(absolute) || File.Exists(absolute + CompendioConstants.EncryptedExtension)))
        {
            if (existing is not null)
            {
                db.Attachments.Remove(existing);
                await db.SaveChangesAsync(cancellationToken);
            }

            return;
        }

        // The envelope, when there is one: the plaintext was shredded above.
        var encryptedAbsolute = absolute + CompendioConstants.EncryptedExtension;
        var info = new FileInfo(File.Exists(absolute) ? absolute : encryptedAbsolute);
        if (existing is null)
        {
            db.Attachments.Add(new Attachment
            {
                Id = Guid.CreateVersion7(),
                PageId = pageId,
                Path = path.Value,
                ContentType = MimeTypes.ForExtension(path.Extension),
                ByteSize = info.Length,
                IsSecure = await secureScopes.IsSecureAsync(path, cancellationToken),
                CreatedAt = clock.UtcNow,
            });
        }
        else
        {
            existing.ByteSize = info.Length;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The attachment half of the secure-scope ingest rule: a plaintext file inside an encrypted
    /// folder's <c>assets/</c> is encrypted and the plaintext removed.
    /// </summary>
    /// <remarks>
    /// Read directly rather than through the store's page read, which enforces the page size budget
    /// — an attachment has its own, larger one, checked on upload and not re-checked here because
    /// the file is already on disk either way.
    /// </remarks>
    private async Task EncryptAttachmentIfDroppedInSecureScopeAsync(ContentPath path, CancellationToken cancellationToken)
    {
        if (!paths.TryResolve(path, out var absolute) || !File.Exists(absolute) ||
            !await secureScopes.IsSecureAsync(path, cancellationToken))
        {
            return;
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(absolute, cancellationToken);
            await store.WriteAsync(path, bytes, ContentStore.Hash(bytes), cancellationToken);
            logger.LogInformation("Encrypted attachment '{Path}': a plaintext file appeared inside a secure scope.", path.Value);
        }
        catch (Exception e) when (e is CompendioException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not encrypt attachment '{Path}', which appeared in plaintext inside a secure scope: {Reason}.",
                path.Value, e.GetType().Name);
        }
    }

    /// <summary>
    /// Tombstones a disappeared folder's ACL rather than deleting it.
    /// </summary>
    /// <remarks>
    /// Dropping ACLs immediately would mean a folder deleted and re-synced by a backup tool comes
    /// back inheriting — that is, readable by everyone. The tombstone is revived if the same path
    /// reappears inside the retention window.
    /// </remarks>
    private async Task TombstoneAclsAsync(ContentPath path, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = clock.UtcNow;

        await db.AclNodes
            .Where(n => (n.FolderPath == path.Value || n.FolderPath.StartsWith(path.Value + "/")) && n.TombstonedAt == null)
            .ExecuteUpdateAsync(n => n.SetProperty(x => x.TombstonedAt, now), cancellationToken);

        permissions.Invalidate();
    }

    private async Task<Folder> EnsureFolderRowAsync(CompendioDbContext db, ContentPath path, CancellationToken cancellationToken)
    {
        var existing = await db.Folders.FirstOrDefaultAsync(f => f.Path == path.Value, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        Guid? parentId = null;
        if (!path.IsRoot)
        {
            var parent = await EnsureFolderRowAsync(db, path.Parent, cancellationToken);
            parentId = parent.Id;
        }

        var folder = new Folder
        {
            Id = Guid.CreateVersion7(),
            Path = path.Value,
            ParentId = parentId,
            Name = path.IsRoot ? string.Empty : path.Name,
            IsSecure = await secureScopes.IsSecureAsync(path, cancellationToken),
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
        };

        db.Folders.Add(folder);
        await db.SaveChangesAsync(cancellationToken);

        // A folder that reappears at a tombstoned path gets its restriction back rather than
        // inheriting. This is criterion 9.
        await ReviveTombstonedAclAsync(db, path, cancellationToken);

        permissions.Invalidate();
        return folder;
    }

    private async Task ReviveTombstonedAclAsync(CompendioDbContext db, ContentPath path, CancellationToken cancellationToken)
    {
        var revived = await db.AclNodes
            .Where(n => n.FolderPath == path.Value && n.TombstonedAt != null)
            .ExecuteUpdateAsync(n => n.SetProperty(x => x.TombstonedAt, (DateTimeOffset?)null), cancellationToken);

        if (revived > 0)
        {
            logger.LogInformation("Revived the tombstoned access rules for '{Path}'.", path.Value);
        }
    }

    /// <summary>
    /// Durable queue rather than a direct index write: a crash mid-batch resumes rather than
    /// silently leaving stale rows behind.
    /// </summary>
    private static async Task EnqueueAsync(
        CompendioDbContext db,
        ContentPath path,
        Guid pageId,
        IndexOperation operation,
        ContentPath? from,
        CancellationToken cancellationToken)
    {
        db.IndexQueue.Add(new IndexQueueItem
        {
            Id = Guid.CreateVersion7(),
            Path = path.Value,
            // Captured now, because a delete drains after the page row is gone.
            PageId = pageId,
            FromPath = from?.Value,
            Operation = operation,
            EnqueuedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<Page> RequirePageAsync(ContentPath path, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Pages.FirstOrDefaultAsync(p => p.Path == path.Value, cancellationToken)
               ?? throw CompendioException.NotFound(path);
    }

    private async Task AuditAsync(
        string action,
        string targetType,
        string targetPath,
        Guid? actorUserId,
        string? before,
        string? after,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.AuditLog.Add(new AuditEntry
        {
            Id = Guid.CreateVersion7(),
            At = clock.UtcNow,
            ActorUserId = actorUserId,
            Action = action,
            TargetType = targetType,
            TargetPath = targetPath,
            BeforeJson = before,
            AfterJson = after,
        });

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Falls back to the <c>name.&lt;lang&gt;.md</c> sibling convention when front matter carries no
    /// <c>translationKey</c>. Front matter always wins when both are present.
    /// </summary>
    private static string? InferTranslationKey(ContentPath path)
    {
        var stem = path.NameWithoutExtension;
        var dot = stem.LastIndexOf('.');

        if (dot <= 0)
        {
            return null;
        }

        var suffix = stem[(dot + 1)..];
        return Domain.Localization.SupportedLanguages.IsSupported(suffix)
            ? path.Parent.Append(stem[..dot]).Value
            : null;
    }
}
