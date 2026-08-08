using Compendio.Application.Abstractions;
using Compendio.Domain.Content;
using Compendio.Domain.Security;
using Compendio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Compendio.Infrastructure.Security;

/// <summary>
/// Which folders are secure scopes, cached until something changes them.
/// </summary>
/// <remarks>
/// A singleton with a snapshot rather than a per-request query, because it is consulted on every
/// single read and write — the content store asks before touching the disk, and the evaluator asks
/// before returning a level.
/// </remarks>
public sealed class SecureScopeRegistry(
    IDbContextFactory<CompendioDbContext> dbFactory,
    ILogger<SecureScopeRegistry> logger) : ISecureScopeRegistry
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<ContentPath>? _snapshot;

    public async Task<IReadOnlyList<ContentPath>> ScopesAsync(CancellationToken cancellationToken = default)
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
            var paths = await db.SecureScopes
                .Where(s => s.RetiredAt == null)
                .Select(s => s.FolderPath)
                .Distinct()
                .ToListAsync(cancellationToken);

            _snapshot = paths.Select(ContentPath.FromTrusted).ToArray();
            logger.LogDebug("Secure scope snapshot refreshed: {Count} scope(s).", _snapshot.Count);
            return _snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ContentPath?> ScopeForAsync(ContentPath path, CancellationToken cancellationToken = default)
    {
        var scopes = await ScopesAsync(cancellationToken);
        return scopes.Count == 0 ? null : PermissionRules.ScopeFor(path, scopes);
    }

    public async Task<bool> IsSecureAsync(ContentPath path, CancellationToken cancellationToken = default)
    {
        var scopes = await ScopesAsync(cancellationToken);
        return scopes.Count != 0 && PermissionRules.IsInsideSecureScope(path, scopes);
    }

    public void Invalidate() => _snapshot = null;
}
