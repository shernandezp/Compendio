using Common.Mediator;
using Compendio.Application.Abstractions;

namespace Compendio.Application.Lifecycle;

/// <param name="UserName">What the <c>owner</c> front-matter key stores.</param>
public sealed record PickableUserDto(Guid Id, string UserName, string DisplayName);

/// <summary>
/// Active accounts, for the owner picker.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not the admin user list. Setting a page's owner needs <c>write</c> on the page, not
/// the <c>Admin</c> role, and an editor who cannot see any usernames would have to type one from
/// memory — which is how a page ends up owned by <c>anna</c> when the account is <c>ana</c>.
/// </para>
/// <para>
/// It returns three fields and no more: no email, no role, no group membership, no sign-in dates.
/// Colleagues' names are already visible on every page's history and in the audit trail, so this
/// discloses nothing new; the admin list stays admin-only because the rest of it does.
/// </para>
/// </remarks>
public sealed record ListPickableUsersQuery : IQuery<IReadOnlyList<PickableUserDto>>;

public sealed class ListPickableUsersHandler(IUserDirectory users)
    : IRequestHandler<ListPickableUsersQuery, IReadOnlyList<PickableUserDto>>
{
    public async Task<IReadOnlyList<PickableUserDto>> Handle(
        ListPickableUsersQuery request,
        CancellationToken cancellationToken = default) =>
        (await users.ActiveUsersAsync(cancellationToken))
        .Select(u => new PickableUserDto(u.Id, u.UserName, u.DisplayName))
        .ToArray();
}
