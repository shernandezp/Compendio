using Compendio.Domain.Security;
using Microsoft.AspNetCore.Identity;

namespace Compendio.Infrastructure.Identity;

/// <summary>
/// A local account.
/// </summary>
/// <remarks>
/// <para>
/// Sessions are deliberately not a table: Identity's security stamp already handles revocation, and
/// a bespoke session table would re-implement it slightly worse.
/// </para>
/// <para>
/// <see cref="ExternalProvider"/> and <see cref="ExternalSubject"/> are unused in v0 and exist so
/// that LDAP/AD and OIDC accounts in v1 map onto this same row rather than a parallel table.
/// </para>
/// </remarks>
public sealed class CompendioUser : IdentityUser<Guid>
{
    /// <summary>A ceiling on what ACLs can grant, never a grant in itself.</summary>
    public UserRole Role { get; set; } = UserRole.Reader;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Wins over every other step of the language-resolution chain.</summary>
    public string? PreferredLanguage { get; set; }

    /// <summary>
    /// Deactivation rather than deletion: a deleted user leaves orphaned audit rows and ACL entries
    /// that nobody can interpret afterwards.
    /// </summary>
    public bool Active { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastSignInAt { get; set; }

    // ---- v1 seams. Unused in v0. ----------------------------------------------------------------
    public string? ExternalProvider { get; set; }

    public string? ExternalSubject { get; set; }
}

/// <summary>Identity's role table is unused — the global role is a column on the user.</summary>
public sealed class CompendioIdentityRole : IdentityRole<Guid>;
