using Compendio.Application.Common;
using Compendio.Domain.Security;

namespace Compendio.Application.Abstractions;

public sealed record CreateUserRequest(
    string UserName,
    string Password,
    string DisplayName,
    string? Email,
    UserRole Role,
    string? PreferredLanguage);

public sealed record SignInResult(bool Succeeded, UserDto? User, string? FailureCode);

/// <summary>
/// Accounts, behind an interface so the application layer never sees Identity types.
/// </summary>
/// <remarks>
/// The last-admin rule lives here rather than in the endpoints, because it has to hold on every
/// path that could break it — deactivate, demote, delete, and the setup wizard — and a rule spread
/// across four endpoints is a rule with a hole in it.
/// </remarks>
public interface IAccountService
{
    Task<bool> AnyUserExistsAsync(CancellationToken cancellationToken = default);

    Task<SignInResult> SignInAsync(string userName, string password, bool persistent, CancellationToken cancellationToken = default);

    Task SignOutAsync(CancellationToken cancellationToken = default);

    Task<UserDto> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken = default);

    Task<UserDto?> GetAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UserDto>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Throws <c>acl.last_admin</c> if this would leave no active administrator.</summary>
    Task<UserDto> UpdateAsync(
        Guid userId,
        string? displayName,
        string? email,
        UserRole? role,
        bool? active,
        string? preferredLanguage,
        CancellationToken cancellationToken = default);

    /// <summary>Throws <c>auth.password_reused</c> when the new password is the current one.</summary>
    Task ChangePasswordAsync(Guid userId, string? currentPassword, string newPassword, bool requireCurrent, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GroupDto>> ListGroupsAsync(CancellationToken cancellationToken = default);

    Task<GroupDto> CreateGroupAsync(string name, CancellationToken cancellationToken = default);

    Task<GroupDto> UpdateGroupAsync(Guid groupId, string? name, bool? active, IReadOnlyList<Guid>? memberIds, CancellationToken cancellationToken = default);

    Task DeleteGroupAsync(Guid groupId, CancellationToken cancellationToken = default);
}
