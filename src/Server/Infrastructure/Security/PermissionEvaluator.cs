using System.Collections.Concurrent;
using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Domain.Content;
using Compendio.Domain.Entities;
using Compendio.Domain.Security;
using Compendio.Hosting.Configuration;
using Compendio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Compendio.Infrastructure.Security;

/// <summary>
/// The caching wrapper around <see cref="PermissionRules"/>.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is caching and loading. The rules themselves are in the domain, as a pure
/// function, so the permission matrix test can drive them from a literal table without a database —
/// and so there is exactly one place where "who can read this" is decided.
/// </para>
/// <para>
/// The cache is keyed by a <see cref="Version"/> epoch bumped on any ACL, group, role or tree
/// change. Coarse invalidation is the right trade here: at SMB folder counts the recompute is a few
/// hundred rows, and a fine-grained scheme that is subtly wrong hands somebody access they should
/// not have.
/// </para>
/// </remarks>
public sealed class PermissionEvaluator(
    IDbContextFactory<CompendioDbContext> dbFactory,
    ISecureScopeRegistry secureScopes,
    IOptions<CompendioOptions> options,
    ILogger<PermissionEvaluator> logger) : IPermissionEvaluator
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<(long Version, Guid User), IReadOnlySet<string>> _readableCache = new();

    private Snapshot? _snapshot;
    private long _version;

    public long Version => Interlocked.Read(ref _version);

    public async Task<PermissionLevel> EffectiveAsync(PermissionSubject subject, ContentPath path, CancellationToken cancellationToken = default)
    {
        var snapshot = await LoadAsync(cancellationToken);
        var folder = FolderOf(path, snapshot);
        var secure = await secureScopes.IsSecureAsync(folder, cancellationToken);

        return PermissionRules.Effective(subject, folder, snapshot.AclNodes, snapshot.InstanceDefault, secure);
    }

    public async Task<IReadOnlySet<string>> ReadableFolderPathsAsync(PermissionSubject subject, CancellationToken cancellationToken = default)
    {
        var snapshot = await LoadAsync(cancellationToken);
        var key = (Version, subject.UserId);

        if (_readableCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var scopes = await secureScopes.ScopesAsync(cancellationToken);
        var readable = PermissionRules
            .ReadableFolders(subject, snapshot.Folders, snapshot.AclNodes, snapshot.InstanceDefault, scopes)
            .Select(p => p.Value)
            .ToHashSet(StringComparer.Ordinal);

        _readableCache[key] = readable;
        return readable;
    }

    public async Task<bool> IsSecureAsync(ContentPath path, CancellationToken cancellationToken = default)
    {
        var snapshot = await LoadAsync(cancellationToken);
        return await secureScopes.IsSecureAsync(FolderOf(path, snapshot), cancellationToken);
    }

    public async Task<ContentPath?> SecureScopeForAsync(ContentPath path, CancellationToken cancellationToken = default)
    {
        var snapshot = await LoadAsync(cancellationToken);
        return await secureScopes.ScopeForAsync(FolderOf(path, snapshot), cancellationToken);
    }

    /// <summary>
    /// A caller who cannot read gets <c>page.not_found</c>, never <c>403</c> — a 403 would confirm
    /// that the page exists, which is exactly what the tree and search avoid saying.
    /// </summary>
    public async Task RequireReadAsync(PermissionSubject subject, ContentPath path, CancellationToken cancellationToken = default)
    {
        if (!(await EffectiveAsync(subject, path, cancellationToken)).CanRead())
        {
            throw CompendioException.NotFound(path);
        }
    }

    public async Task RequireWriteAsync(PermissionSubject subject, ContentPath path, CancellationToken cancellationToken = default)
    {
        var level = await EffectiveAsync(subject, path, cancellationToken);

        if (!level.CanRead())
        {
            throw CompendioException.NotFound(path);
        }

        if (level.CanWrite())
        {
            return;
        }

        // Distinguishing these two is worth the extra branch: "you may look but not edit" and
        // "administrators only, because this folder is encrypted" are different situations and a
        // user who is told the wrong one files the wrong support ticket.
        throw await IsSecureAsync(path, cancellationToken) && subject.Role != UserRole.Admin
            ? CompendioException.SecureAdminRequired(path)
            : CompendioException.Forbidden(path);
    }

    public async Task RequireManageAsync(PermissionSubject subject, ContentPath path, CancellationToken cancellationToken = default)
    {
        var level = await EffectiveAsync(subject, path, cancellationToken);

        if (!level.CanRead())
        {
            throw CompendioException.NotFound(path);
        }

        if (!level.CanManage())
        {
            throw CompendioException.Forbidden(path);
        }
    }

    public void Invalidate()
    {
        _snapshot = null;
        _readableCache.Clear();
        var version = Interlocked.Increment(ref _version);
        logger.LogDebug("Permission snapshot invalidated; version is now {Version}.", version);
    }

    /// <summary>
    /// The folder a path evaluates at. ACLs attach to folders only, so a page or an attachment
    /// evaluates at the folder containing it.
    /// </summary>
    /// <remarks>
    /// Decided against the known folder set, not by looking for a dot. "Has an extension" is not the
    /// same question as "is a file": a folder called <c>Legal.2026</c> passes that test, so a
    /// restriction on it would have been read from its <em>parent</em> — a privilege escalation
    /// rather than a cosmetic bug. A path that is a known folder evaluates at itself; anything else
    /// evaluates at its nearest known ancestor, and never further up than that, because skipping a
    /// level would skip a restriction.
    /// </remarks>
    private static ContentPath FolderOf(ContentPath path, Snapshot snapshot)
    {
        if (path.IsRoot || snapshot.FolderPaths.Contains(path.Value))
        {
            return path;
        }

        var current = path.Parent;
        while (!current.IsRoot && !snapshot.FolderPaths.Contains(current.Value))
        {
            current = current.Parent;
        }

        return current;
    }

    private async Task<Snapshot> LoadAsync(CancellationToken cancellationToken)
    {
        var snapshot = _snapshot;
        if (snapshot is not null)
        {
            return snapshot;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_snapshot is not null)
            {
                return _snapshot;
            }

            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

            var nodes = await db.AclNodes
                .Include(n => n.Entries)
                .AsNoTracking()
                .ToListAsync(cancellationToken);

            var folders = await db.Folders
                .AsNoTracking()
                .Select(f => f.Path)
                .ToListAsync(cancellationToken);

            var acl = nodes.ToDictionary(
                n => n.FolderPath,
                n => new AclNodeSnapshot(
                    ContentPath.FromTrusted(n.FolderPath),
                    n.InheritParent,
                    n.Entries.Select(e => new AclEntrySnapshot(e.SubjectType, e.SubjectId, e.Level)).ToArray(),
                    n.TombstonedAt is not null),
                StringComparer.Ordinal);

            // The root always exists as an evaluable folder even before anything is indexed.
            var allFolders = folders.Select(ContentPath.FromTrusted).ToList();
            if (!allFolders.Any(f => f.IsRoot))
            {
                allFolders.Insert(0, ContentPath.Root);
            }

            _snapshot = new Snapshot(
                acl,
                allFolders,
                allFolders.Select(f => f.Value).ToHashSet(StringComparer.Ordinal),
                await ResolveInstanceDefaultAsync(db, cancellationToken));

            return _snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The instance default access level: the setup wizard's answer if there is one, otherwise
    /// configuration.
    /// </summary>
    /// <remarks>
    /// The wizard asks "who can read a new folder by default" and stores the answer, so reading only
    /// from configuration would have made that question decorative — an admin who chose "nobody"
    /// would have got "everyone".
    /// </remarks>
    private async Task<PermissionLevel> ResolveInstanceDefaultAsync(CompendioDbContext db, CancellationToken cancellationToken)
    {
        var stored = await db.Settings
            .AsNoTracking()
            .Where(s => s.Key == SettingKeys.InstanceDefaultAccess)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken);

        return Enum.TryParse<PermissionLevel>(stored, ignoreCase: true, out var parsed)
            ? parsed
            : options.Value.Instance.DefaultAccess;
    }

    private sealed record Snapshot(
        IReadOnlyDictionary<string, AclNodeSnapshot> AclNodes,
        IReadOnlyList<ContentPath> Folders,
        IReadOnlySet<string> FolderPaths,
        PermissionLevel InstanceDefault);
}
