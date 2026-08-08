using Compendio.Application.Abstractions;

namespace Compendio.Application.Lifecycle;

/// <summary>
/// Resolves many pages' <c>owner</c> strings in one pass.
/// </summary>
/// <remarks>
/// A report of fifty stale pages resolving each owner with its own round trip is a hundred queries
/// for a screen. One snapshot of the active accounts answers all of them, and an SMB instance has
/// tens to hundreds of accounts — small enough that the snapshot is cheaper than the joins would be.
/// </remarks>
public sealed class OwnerResolver(IUserDirectory users)
{
    public async Task<OwnerSnapshot> SnapshotAsync(CancellationToken cancellationToken = default) =>
        new(await users.ActiveUsersAsync(cancellationToken));
}

/// <summary>A point-in-time view of who exists, keyed by username, case-insensitively.</summary>
public sealed class OwnerSnapshot
{
    private readonly Dictionary<string, DirectoryUser> _byUserName;

    public OwnerSnapshot(IReadOnlyList<DirectoryUser> users)
    {
        _byUserName = new Dictionary<string, DirectoryUser>(StringComparer.OrdinalIgnoreCase);

        foreach (var user in users)
        {
            // Last one wins; usernames are unique in the store, so there is no collision to resolve.
            _byUserName[user.UserName] = user;
        }

        All = users;
    }

    public IReadOnlyList<DirectoryUser> All { get; }

    /// <summary>Null when the page names nobody, or names somebody who is not an active account.</summary>
    public DirectoryUser? Resolve(string? owner) =>
        !string.IsNullOrWhiteSpace(owner) && _byUserName.TryGetValue(owner.Trim(), out var user) ? user : null;
}
