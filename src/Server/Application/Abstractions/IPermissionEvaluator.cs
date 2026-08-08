using Compendio.Domain.Content;
using Compendio.Domain.Security;

namespace Compendio.Application.Abstractions;

/// <summary>
/// The one evaluator. Every read path calls it.
/// </summary>
/// <remarks>
/// A PR that adds a read surface and does not call in here — and does not add a row to the leak
/// suite — is incomplete. That is the whole enforcement story: there is no second implementation
/// and the UI never decides, it renders what the API already filtered.
/// </remarks>
public interface IPermissionEvaluator
{
    /// <summary>
    /// Cache epoch. Bumped on any ACL, group-membership, role, or folder-tree change. Coarse and
    /// correct beats fine-grained and wrong: the recompute is cheap at SMB folder counts.
    /// </summary>
    long Version { get; }

    /// <summary>Effective level at a path. Pages and attachments evaluate at their folder.</summary>
    Task<PermissionLevel> EffectiveAsync(PermissionSubject subject, ContentPath path, CancellationToken cancellationToken = default);

    /// <summary>Every folder the subject can read. Materializes the search predicate.</summary>
    Task<IReadOnlySet<string>> ReadableFolderPathsAsync(PermissionSubject subject, CancellationToken cancellationToken = default);

    /// <summary>Whether the path sits inside a secure scope.</summary>
    Task<bool> IsSecureAsync(ContentPath path, CancellationToken cancellationToken = default);

    /// <summary>The secure scope covering the path, if any.</summary>
    Task<ContentPath?> SecureScopeForAsync(ContentPath path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Throws <c>page.not_found</c> when the subject cannot read — never <c>403</c>, because a 403
    /// confirms the page exists.
    /// </summary>
    Task RequireReadAsync(PermissionSubject subject, ContentPath path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Throws <c>page.not_found</c> when unreadable, <c>page.forbidden</c> when readable but not
    /// writable, and <c>secure.admin_required</c> for a non-admin inside a secure scope.
    /// </summary>
    Task RequireWriteAsync(PermissionSubject subject, ContentPath path, CancellationToken cancellationToken = default);

    Task RequireManageAsync(PermissionSubject subject, ContentPath path, CancellationToken cancellationToken = default);

    /// <summary>Invalidates the cache. Called by anything that changes the inputs.</summary>
    void Invalidate();
}
