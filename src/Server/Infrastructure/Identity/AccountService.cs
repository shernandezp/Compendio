using System.Security.Claims;
using Compendio.Api.Common;
using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Domain.Entities;
using Compendio.Domain.Security;
using Compendio.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Compendio.Infrastructure.Identity;

/// <summary>
/// Accounts and groups, over ASP.NET Core Identity.
/// </summary>
/// <remarks>
/// Password hashing is Identity's own <c>PasswordHasher</c> — PBKDF2-HMAC-SHA256 with the iteration
/// count configured to current OWASP guidance. No Argon2 package: a native dependency for this
/// would fight single-file publishing for a marginal gain, and the iteration count is one line of
/// configuration.
/// </remarks>
public sealed class AccountService(
    UserManager<CompendioUser> users,
    SignInManager<CompendioUser> signIn,
    CompendioDbContext db,
    IPermissionEvaluator permissions,
    IClock clock,
    ILogger<AccountService> logger) : IAccountService
{
    public Task<bool> AnyUserExistsAsync(CancellationToken cancellationToken = default) =>
        db.Users.AnyAsync(cancellationToken);

    public async Task<Application.Abstractions.SignInResult> SignInAsync(
        string userName,
        string password,
        bool persistent,
        CancellationToken cancellationToken = default)
    {
        var user = await users.FindByNameAsync(userName);

        // Deliberately one failure code for every reason. Distinguishing "no such user" from "wrong
        // password" turns the login form into a user-name oracle.
        if (user is null || !user.Active)
        {
            return new Application.Abstractions.SignInResult(false, null, ProblemCodes.AuthFailed);
        }

        var result = await signIn.PasswordSignInAsync(user, password, persistent, lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            return new Application.Abstractions.SignInResult(false, null, ProblemCodes.AuthFailed);
        }

        user.LastSignInAt = clock.UtcNow;
        await users.UpdateAsync(user);

        return new Application.Abstractions.SignInResult(true, await MapAsync(user, cancellationToken), null);
    }

    public Task SignOutAsync(CancellationToken cancellationToken = default) => signIn.SignOutAsync();

    public async Task<UserDto> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken = default)
    {
        var user = new CompendioUser
        {
            Id = Guid.CreateVersion7(),
            UserName = request.UserName,
            Email = request.Email,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? request.UserName : request.DisplayName,
            Role = request.Role,
            PreferredLanguage = request.PreferredLanguage,
            Active = true,
            CreatedAt = clock.UtcNow,
        };

        var result = await users.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            throw new ValidationException(result.Errors
                .GroupBy(e => MapIdentityError(e.Code))
                .ToDictionary(g => g.Key, g => g.Select(e => e.Code).ToArray(), StringComparer.Ordinal));
        }

        permissions.Invalidate();
        return await MapAsync(user, cancellationToken);
    }

    public async Task<UserDto?> GetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        return user is null ? null : await MapAsync(user, cancellationToken);
    }

    public async Task<IReadOnlyList<UserDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var all = await db.Users.OrderBy(u => u.DisplayName).ToListAsync(cancellationToken);
        var memberships = await db.GroupMembers.ToListAsync(cancellationToken);

        return all.Select(u => Map(u, memberships.Where(m => m.UserId == u.Id).Select(m => m.GroupId).ToArray())).ToArray();
    }

    public async Task<UserDto> UpdateAsync(
        Guid userId,
        string? displayName,
        string? email,
        UserRole? role,
        bool? active,
        string? preferredLanguage,
        CancellationToken cancellationToken = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken)
                   ?? throw new CompendioException(ProblemCodes.PageNotFound, StatusCodes.Status404NotFound);

        var losesAdmin = user.Role == UserRole.Admin &&
                         ((role is not null && role != UserRole.Admin) || active == false);

        if (losesAdmin)
        {
            await RequireAnotherAdminAsync(userId, cancellationToken);
        }

        if (displayName is not null)
        {
            user.DisplayName = displayName;
        }

        if (email is not null)
        {
            user.Email = email;
        }

        if (role is not null)
        {
            user.Role = role.Value;
        }

        if (active is not null)
        {
            user.Active = active.Value;
        }

        if (preferredLanguage is not null)
        {
            user.PreferredLanguage = preferredLanguage.Length == 0 ? null : preferredLanguage;
        }

        await db.SaveChangesAsync(cancellationToken);

        // Deactivation and role change must take effect now, not when the cookie next rolls over.
        await users.UpdateSecurityStampAsync(user);
        permissions.Invalidate();

        return await MapAsync(user, cancellationToken);
    }

    public async Task ChangePasswordAsync(
        Guid userId,
        string? currentPassword,
        string newPassword,
        bool requireCurrent,
        CancellationToken cancellationToken = default)
    {
        var user = await users.FindByIdAsync(userId.ToString())
                   ?? throw new CompendioException(ProblemCodes.PageNotFound, StatusCodes.Status404NotFound);

        // A password cannot be changed to the one already in use.
        if (await users.CheckPasswordAsync(user, newPassword))
        {
            throw CompendioException.BadRequest(ProblemCodes.AuthPasswordReused);
        }

        IdentityResult result;
        if (requireCurrent)
        {
            result = await users.ChangePasswordAsync(user, currentPassword ?? string.Empty, newPassword);
        }
        else
        {
            var token = await users.GeneratePasswordResetTokenAsync(user);
            result = await users.ResetPasswordAsync(user, token, newPassword);
        }

        if (!result.Succeeded)
        {
            throw new ValidationException(result.Errors
                .GroupBy(e => MapIdentityError(e.Code))
                .ToDictionary(g => g.Key, g => g.Select(e => e.Code).ToArray(), StringComparer.Ordinal));
        }

        logger.LogInformation("Password changed for user {UserId}.", userId);
    }

    public async Task DeleteAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return;
        }

        if (user.Role == UserRole.Admin)
        {
            await RequireAnotherAdminAsync(userId, cancellationToken);
        }

        // Deactivated rather than removed: a deleted row leaves audit entries and ACL entries that
        // nobody can interpret afterwards, which defeats the point of having them.
        user.Active = false;
        user.UserName = $"{user.UserName}.deleted.{clock.UtcNow:yyyyMMddHHmmss}";
        user.NormalizedUserName = user.UserName.ToUpperInvariant();

        await db.SaveChangesAsync(cancellationToken);
        await users.UpdateSecurityStampAsync(user);
        permissions.Invalidate();
    }

    public async Task<IReadOnlyList<GroupDto>> ListGroupsAsync(CancellationToken cancellationToken = default)
    {
        var groups = await db.Groups.Include(g => g.Members).OrderBy(g => g.Name).ToListAsync(cancellationToken);
        return groups.Select(MapGroup).ToArray();
    }

    public async Task<GroupDto> CreateGroupAsync(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ValidationException(new Dictionary<string, string[]> { ["name"] = ["required"] });
        }

        var group = new Group { Id = Guid.CreateVersion7(), Name = name.Trim(), Active = true };
        db.Groups.Add(group);
        await db.SaveChangesAsync(cancellationToken);

        permissions.Invalidate();
        return MapGroup(group);
    }

    public async Task<GroupDto> UpdateGroupAsync(
        Guid groupId,
        string? name,
        bool? active,
        IReadOnlyList<Guid>? memberIds,
        CancellationToken cancellationToken = default)
    {
        var group = await db.Groups.Include(g => g.Members).FirstOrDefaultAsync(g => g.Id == groupId, cancellationToken)
                    ?? throw new CompendioException(ProblemCodes.PageNotFound, StatusCodes.Status404NotFound);

        if (name is not null)
        {
            group.Name = name.Trim();
        }

        if (active is not null)
        {
            group.Active = active.Value;
        }

        if (memberIds is not null)
        {
            db.GroupMembers.RemoveRange(group.Members);
            foreach (var userId in memberIds.Distinct())
            {
                db.GroupMembers.Add(new GroupMember { Id = Guid.CreateVersion7(), GroupId = groupId, UserId = userId });
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        // Group membership changes what people can see, so the evaluator's epoch must move.
        permissions.Invalidate();

        var reloaded = await db.Groups.Include(g => g.Members).FirstAsync(g => g.Id == groupId, cancellationToken);
        return MapGroup(reloaded);
    }

    public async Task DeleteGroupAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        await db.AclEntries
            .Where(e => e.SubjectType == AclSubjectType.Group && e.SubjectId == groupId)
            .ExecuteDeleteAsync(cancellationToken);

        await db.Groups.Where(g => g.Id == groupId).ExecuteDeleteAsync(cancellationToken);
        permissions.Invalidate();
    }

    /// <summary>
    /// There must always be one active administrator, or nobody can manage the instance — and with
    /// no email there is no self-service way back in. The only recovery is
    /// <c>compendio reset-admin-password</c> at the console.
    /// </summary>
    private async Task RequireAnotherAdminAsync(Guid excluding, CancellationToken cancellationToken)
    {
        var others = await db.Users.CountAsync(
            u => u.Id != excluding && u.Role == UserRole.Admin && u.Active, cancellationToken);

        if (others == 0)
        {
            throw CompendioException.LastAdmin();
        }
    }

    private async Task<UserDto> MapAsync(CompendioUser user, CancellationToken cancellationToken)
    {
        var groups = await db.GroupMembers
            .Where(m => m.UserId == user.Id)
            .Select(m => m.GroupId)
            .ToArrayAsync(cancellationToken);

        return Map(user, groups);
    }

    private static UserDto Map(CompendioUser user, IReadOnlyList<Guid> groupIds) =>
        new(user.Id, user.UserName ?? string.Empty, user.DisplayName, user.Email, user.Role,
            user.Active, user.PreferredLanguage, user.CreatedAt, user.LastSignInAt, groupIds);

    private static GroupDto MapGroup(Group group) =>
        new(group.Id, group.Name, group.Active, group.Members.Count, group.Members.Select(m => m.UserId).ToArray());

    /// <summary>Maps Identity's error codes onto the field the client should highlight.</summary>
    private static string MapIdentityError(string code) => code switch
    {
        "DuplicateUserName" or "InvalidUserName" => "userName",
        "DuplicateEmail" or "InvalidEmail" => "email",
        _ when code.StartsWith("Password", StringComparison.Ordinal) => "password",
        _ => "form",
    };
}

/// <summary>
/// Puts the role, display name and language preference in the auth cookie.
/// </summary>
/// <remarks>
/// Saves a database round trip on every request for values that only change when the security stamp
/// is refreshed anyway — which <see cref="AccountService"/> does on every role or activation change.
/// </remarks>
public sealed class CompendioClaimsFactory(
    UserManager<CompendioUser> users,
    IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<CompendioUser>(users, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(CompendioUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);

        identity.AddClaim(new Claim(CompendioClaims.Role, user.Role.ToString()));
        identity.AddClaim(new Claim(CompendioClaims.DisplayName, user.DisplayName));

        if (!string.IsNullOrEmpty(user.PreferredLanguage))
        {
            identity.AddClaim(new Claim(CompendioClaims.PreferredLanguage, user.PreferredLanguage));
        }

        return identity;
    }
}
