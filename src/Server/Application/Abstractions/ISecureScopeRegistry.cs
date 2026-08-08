using Compendio.Domain.Content;

namespace Compendio.Application.Abstractions;

/// <summary>
/// Which folders are secure scopes, cached.
/// </summary>
/// <remarks>
/// Split out from <see cref="IPermissionEvaluator"/> so that the content store — which has to know
/// whether it is writing an envelope — does not depend on the evaluator, which depends on the
/// store's tree. Both read from here instead.
/// </remarks>
public interface ISecureScopeRegistry
{
    /// <summary>All secure scope folder paths.</summary>
    Task<IReadOnlyList<ContentPath>> ScopesAsync(CancellationToken cancellationToken = default);

    /// <summary>The scope covering this path, or null.</summary>
    Task<ContentPath?> ScopeForAsync(ContentPath path, CancellationToken cancellationToken = default);

    Task<bool> IsSecureAsync(ContentPath path, CancellationToken cancellationToken = default);

    void Invalidate();
}
