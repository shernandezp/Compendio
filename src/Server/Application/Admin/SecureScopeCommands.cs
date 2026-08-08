using Common.Mediator;
using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Domain.Content;
using Compendio.Domain.Entities;
using Compendio.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace Compendio.Application.Admin;

public sealed record ListSecureScopesQuery : IQuery<IReadOnlyList<SecureScopeDto>>;

public sealed class ListSecureScopesHandler(
    ICompendioDbContext db,
    IContentCrypto crypto) : IRequestHandler<ListSecureScopesQuery, IReadOnlyList<SecureScopeDto>>
{
    public async Task<IReadOnlyList<SecureScopeDto>> Handle(ListSecureScopesQuery request, CancellationToken cancellationToken = default)
    {
        var scopes = await db.SecureScopes
            .AsNoTracking()
            .Where(s => s.RetiredAt == null)
            .OrderBy(s => s.FolderPath)
            .ToListAsync(cancellationToken);

        var health = await crypto.ProbeAsync(cancellationToken);

        return scopes.Select(s => new SecureScopeDto(
            s.FolderPath, s.KeyId, s.CreatedAt, s.RotatedAt, s.IndexContent, s.AllowAi,
            health.GetValueOrDefault(s.FolderPath, SecureAvailability.Available).ToString(),
            s.EncryptionCount)).ToArray();
    }
}

/// <param name="IndexContent">
/// Opting a scope into full-text indexing copies its plaintext into <c>compendio.db</c>. The dialog
/// that sets this says so in those words; this flag is the acknowledgement, not the decision.
/// </param>
public sealed record CreateSecureScopeCommand(string Path, bool IndexContent = false, bool AllowAi = false)
    : ICommand<SecureScopeDto>;

/// <summary>
/// Marks a folder secure.
/// </summary>
/// <remarks>
/// Three things happen atomically, and all three are load-bearing: the folder's ACL is set to
/// restricted (a secure scope is always explicitly listed), a data key is created, and everything
/// already inside is queued for re-encryption. A scope inside a scope is rejected — one key per
/// scope, and no key hierarchies to reason about.
/// </remarks>
public sealed class CreateSecureScopeHandler(
    ICompendioDbContext db,
    IContentStore store,
    IContentCrypto crypto,
    IPageHistory history,
    IPathPolicy paths,
    ISecureScopeRegistry secureScopes,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser,
    IClock clock,
    ILogger<CreateSecureScopeHandler> logger) : IRequestHandler<CreateSecureScopeCommand, SecureScopeDto>
{
    public async Task<SecureScopeDto> Handle(CreateSecureScopeCommand request, CancellationToken cancellationToken = default)
    {
        if (currentUser.Role != UserRole.Admin)
        {
            throw CompendioException.SecureAdminRequired(ContentPath.FromTrusted(request.Path));
        }

        var path = paths.Require(request.Path, PathKind.Folder);

        if (path.IsRoot)
        {
            throw CompendioException.InvalidPath(PathRule.EscapesRoot);
        }

        if (await secureScopes.IsSecureAsync(path, cancellationToken))
        {
            throw CompendioException.BadRequest(ProblemCodes.SecureNested, path.Value);
        }

        var keyId = await crypto.CreateScopeKeyAsync(path, cancellationToken);
        secureScopes.Invalidate();

        await RestrictAclAsync(path, cancellationToken);
        await MarkTreeSecureAsync(path, cancellationToken);

        var scope = await db.SecureScopes.FirstAsync(s => s.KeyId == keyId, cancellationToken);
        scope.IndexContent = request.IndexContent;
        scope.AllowAi = request.AllowAi;

        db.AuditLog.Add(new AuditEntry
        {
            Id = Guid.CreateVersion7(),
            At = clock.UtcNow,
            ActorUserId = currentUser.UserId,
            Action = "secure.create",
            TargetType = "folder",
            TargetPath = path.Value,
            AfterJson = System.Text.Json.JsonSerializer.Serialize(new { request.IndexContent, request.AllowAi }),
        });

        await db.SaveChangesAsync(cancellationToken);
        await EncryptExistingAsync(path, cancellationToken);

        // History travels with the page, so it has to be encrypted too — otherwise every earlier
        // revision of the documents in this folder is still sitting in the database in the clear.
        await history.EncryptExistingAsync(path, cancellationToken);

        permissions.Invalidate();
        logger.LogInformation("Folder '{Path}' is now a secure scope.", path.Value);

        return new SecureScopeDto(path.Value, keyId, scope.CreatedAt, null, scope.IndexContent, scope.AllowAi,
            SecureAvailability.Available.ToString(), scope.EncryptionCount);
    }

    /// <summary>A secure scope always cuts inheritance, so its membership is explicit.</summary>
    private async Task RestrictAclAsync(ContentPath path, CancellationToken cancellationToken)
    {
        var node = await db.AclNodes.Include(n => n.Entries).FirstOrDefaultAsync(n => n.FolderPath == path.Value, cancellationToken);

        if (node is null)
        {
            node = new AclNode { Id = Guid.CreateVersion7(), FolderPath = path.Value };
            db.AclNodes.Add(node);
        }

        node.InheritParent = false;
        node.TombstonedAt = null;
        node.UpdatedAt = clock.UtcNow;
        node.UpdatedByUserId = currentUser.UserId;

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task MarkTreeSecureAsync(ContentPath path, CancellationToken cancellationToken)
    {
        var prefix = path.Value + "/";

        await db.Folders
            .Where(f => f.Path == path.Value || f.Path.StartsWith(prefix))
            .ExecuteUpdateAsync(f => f.SetProperty(x => x.IsSecure, true), cancellationToken);

        await db.Pages
            .Where(p => p.Path.StartsWith(prefix))
            .ExecuteUpdateAsync(p => p.SetProperty(x => x.IsSecure, true), cancellationToken);

        await db.Attachments
            .Where(a => a.Path.StartsWith(prefix))
            .ExecuteUpdateAsync(a => a.SetProperty(x => x.IsSecure, true), cancellationToken);
    }

    /// <summary>
    /// Rewrites everything already in the folder into envelopes, and removes it from the index.
    /// </summary>
    /// <remarks>
    /// Leaving the existing files in plaintext would make "this folder is encrypted" false for
    /// exactly the documents that were there when somebody decided it needed to be.
    /// </remarks>
    private async Task EncryptExistingAsync(ContentPath path, CancellationToken cancellationToken)
    {
        var entries = new List<ContentEntry>();
        await foreach (var entry in store.EnumerateAsync(path, cancellationToken))
        {
            if (!entry.IsFolder && !entry.IsEncryptedOnDisk)
            {
                entries.Add(entry);
            }
        }

        foreach (var entry in entries)
        {
            var file = await store.ReadAsync(entry.Path, cancellationToken);
            if (file is null)
            {
                continue;
            }

            await store.WriteAsync(entry.Path, file.Bytes, file.ContentHash, cancellationToken);

            db.IndexQueue.Add(new IndexQueueItem
            {
                Id = Guid.CreateVersion7(),
                Path = entry.Path.Value,
                Operation = IndexOperation.Upsert,
                EnqueuedAt = clock.UtcNow,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed record UpdateSecureScopeCommand(string Path, bool? IndexContent, bool? AllowAi) : ICommand<Unit>;

public sealed class UpdateSecureScopeHandler(
    ICompendioDbContext db,
    ISearchIndex index,
    IPathPolicy paths,
    ICurrentUser currentUser,
    IClock clock) : IRequestHandler<UpdateSecureScopeCommand, Unit>
{
    public async Task<Unit> Handle(UpdateSecureScopeCommand request, CancellationToken cancellationToken = default)
    {
        if (currentUser.Role != UserRole.Admin)
        {
            throw CompendioException.SecureAdminRequired(ContentPath.FromTrusted(request.Path));
        }

        var path = paths.Require(request.Path, PathKind.Folder);
        var scope = await db.SecureScopes.FirstOrDefaultAsync(
                        s => s.FolderPath == path.Value && s.RetiredAt == null, cancellationToken)
                    ?? throw CompendioException.NotFound(path);

        var wasIndexed = scope.IndexContent;

        if (request.IndexContent is not null)
        {
            scope.IndexContent = request.IndexContent.Value;
        }

        if (request.AllowAi is not null)
        {
            scope.AllowAi = request.AllowAi.Value;
        }

        db.AuditLog.Add(new AuditEntry
        {
            Id = Guid.CreateVersion7(),
            At = clock.UtcNow,
            ActorUserId = currentUser.UserId,
            Action = "secure.update",
            TargetType = "folder",
            TargetPath = path.Value,
            AfterJson = System.Text.Json.JsonSerializer.Serialize(new { scope.IndexContent, scope.AllowAi }),
        });

        await db.SaveChangesAsync(cancellationToken);

        // Turning indexing on or off has to move the stored text now, not at the next reindex —
        // otherwise "we turned that off" is not true until somebody remembers to run a command.
        var pages = await db.Pages
            .Where(p => p.Path.StartsWith(path.Value + "/"))
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);

        foreach (var pageId in pages)
        {
            if (scope.IndexContent)
            {
                await index.UpsertAsync(pageId, cancellationToken);
            }
            else if (wasIndexed)
            {
                await index.RemoveAsync(pageId, cancellationToken);
            }
        }

        return Unit.Value;
    }
}
