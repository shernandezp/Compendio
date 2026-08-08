using Compendio.Domain.Security;

namespace Compendio.Application.Abstractions;

/// <summary>
/// Read-only lookups over accounts and groups.
/// </summary>
/// <remarks>
/// Exists so the application layer can put a display name next to an id without seeing an Identity
/// type. <see cref="ICompendioDbContext"/> deliberately does not expose the user table: it is
/// owned by ASP.NET Core Identity, and reaching into it from a handler would tie the layer to a
/// framework it otherwise has no opinion about.
/// </remarks>
public interface IUserDirectory
{
    /// <summary>Display names for people <em>and</em> groups, keyed by id.</summary>
    Task<IReadOnlyDictionary<Guid, string>> SubjectNamesAsync(CancellationToken cancellationToken = default);

    Task<string?> DisplayNameAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<IReadOnlySet<Guid>> UserIdsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlySet<Guid>> GroupIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>The evaluator's view of a user: role plus flattened group membership.</summary>
    Task<(PermissionSubject Subject, string DisplayName)?> SubjectAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a page's <c>owner</c> front-matter value — a username — to an active account.
    /// </summary>
    /// <remarks>
    /// Case-insensitive, and null when nothing matches. An owner that resolves to nobody is reported
    /// as unassigned rather than treated as an error: the front matter is a human's text and eating
    /// or rewriting it would break the promise that the file is the source of truth.
    /// </remarks>
    Task<Guid?> ResolveOwnerAsync(string? userName, CancellationToken cancellationToken = default);

    /// <summary>Every active account, for the owner picker and for acknowledgment reports.</summary>
    Task<IReadOnlyList<DirectoryUser>> ActiveUsersAsync(CancellationToken cancellationToken = default);

    /// <summary>Admin accounts, for instance-level notifications nobody else should receive.</summary>
    Task<IReadOnlyList<Guid>> ActiveAdminIdsAsync(CancellationToken cancellationToken = default);
}

public sealed record DirectoryUser(Guid Id, string UserName, string DisplayName);
