using Compendio.Application.Abstractions;
using Compendio.Domain.Security;
using Compendio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Compendio.Infrastructure.Identity;

/// <inheritdoc />
public sealed class UserDirectory(CompendioDbContext db) : IUserDirectory
{
    public async Task<IReadOnlyDictionary<Guid, string>> SubjectNamesAsync(CancellationToken cancellationToken = default)
    {
        var names = await db.Users
            .AsNoTracking()
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken);

        // Groups share the id space from the ACL's point of view, so one lookup serves both.
        foreach (var group in await db.Groups.AsNoTracking().ToListAsync(cancellationToken))
        {
            names[group.Id] = group.Name;
        }

        return names;
    }

    public Task<string?> DisplayNameAsync(Guid userId, CancellationToken cancellationToken = default) =>
        db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync(cancellationToken)!;

    public async Task<IReadOnlySet<Guid>> UserIdsAsync(CancellationToken cancellationToken = default) =>
        await db.Users.AsNoTracking().Select(u => u.Id).ToHashSetAsync(cancellationToken);

    public async Task<IReadOnlySet<Guid>> GroupIdsAsync(CancellationToken cancellationToken = default) =>
        await db.Groups.AsNoTracking().Select(g => g.Id).ToHashSetAsync(cancellationToken);

    public async Task<(PermissionSubject Subject, string DisplayName)?> SubjectAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return null;
        }

        var groups = await db.GroupMembers
            .AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => m.GroupId)
            .ToHashSetAsync(cancellationToken);

        return (new PermissionSubject(user.Id, user.Role, groups), user.DisplayName);
    }

    public async Task<Guid?> ResolveOwnerAsync(string? userName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return null;
        }

        // NormalizedUserName is Identity's own upper-cased form and is indexed, so this is the
        // case-insensitive comparison the store is built for rather than a scan with ToUpper().
        var normalized = userName.Trim().ToUpperInvariant();

        return await db.Users.AsNoTracking()
            .Where(u => u.Active && u.NormalizedUserName == normalized)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DirectoryUser>> ActiveUsersAsync(CancellationToken cancellationToken = default) =>
        await db.Users.AsNoTracking()
            .Where(u => u.Active)
            .OrderBy(u => u.DisplayName)
            .Select(u => new DirectoryUser(u.Id, u.UserName!, u.DisplayName))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<Guid>> ActiveAdminIdsAsync(CancellationToken cancellationToken = default) =>
        await db.Users.AsNoTracking()
            .Where(u => u.Active && u.Role == UserRole.Admin)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);
}
