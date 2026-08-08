using System.Security.Claims;
using Compendio.Application.Abstractions;
using Compendio.Domain.Security;
using Compendio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Compendio.Api.Common;

/// <summary>
/// <see cref="ICurrentUser"/> over the request's principal.
/// </summary>
/// <remarks>
/// Group membership is loaded once per request and memoized. It is on the hot path for every
/// permission check, and putting it in a claim instead would mean a stale membership survives until
/// the cookie is reissued — which is exactly the kind of "why can she still see that folder"
/// question the audit log exists to avoid needing.
/// </remarks>
public sealed class HttpCurrentUser(IHttpContextAccessor accessor, IDbContextFactory<CompendioDbContext> dbFactory) : ICurrentUser
{
    private IReadOnlySet<Guid>? _groups;

    private HttpContext? Context => accessor.HttpContext;

    public bool IsAuthenticated => Context?.User.Identity?.IsAuthenticated == true;

    public Guid UserId =>
        Guid.TryParse(Context?.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : Guid.Empty;

    public string? UserName => Context?.User.Identity?.Name;

    public UserRole Role =>
        Enum.TryParse<UserRole>(Context?.User.FindFirst(CompendioClaims.Role)?.Value, out var role)
            ? role
            : UserRole.Reader;

    public IReadOnlySet<Guid> GroupIds => _groups ??= LoadGroups();

    public string Language => Context?.Language() ?? Domain.Localization.SupportedLanguages.Fallback;

    private IReadOnlySet<Guid> LoadGroups()
    {
        var userId = UserId;
        if (userId == Guid.Empty)
        {
            return new HashSet<Guid>();
        }

        using var db = dbFactory.CreateDbContext();
        return db.GroupMembers
            .Where(m => m.UserId == userId)
            .Select(m => m.GroupId)
            .ToHashSet();
    }
}
