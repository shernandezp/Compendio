using Compendio.Domain.Security;

namespace Compendio.Application.Abstractions;

/// <summary>Who is making this request.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    Guid UserId { get; }

    string? UserName { get; }

    UserRole Role { get; }

    /// <summary>Flattened group membership. Group nesting is not supported.</summary>
    IReadOnlySet<Guid> GroupIds { get; }

    /// <summary>The language resolved for this request. Errors come back in it.</summary>
    string Language { get; }

    /// <summary>The subject the permission evaluator takes.</summary>
    PermissionSubject Subject => new(UserId, Role, GroupIds);
}
