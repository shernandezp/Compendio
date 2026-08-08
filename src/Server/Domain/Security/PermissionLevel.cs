namespace Compendio.Domain.Security;

/// <summary>
/// Ordered so <c>max()</c> and <c>min()</c> are meaningful. The numeric gaps are deliberate: a
/// <c>comment</c> level can be inserted between <see cref="Read"/> and <see cref="Write"/> when
/// comments arrive from the backlog, without migrating stored values.
/// </summary>
public enum PermissionLevel
{
    /// <summary>Nothing. The node is invisible — absent from tree, search, backlinks, counts.</summary>
    None = 0,

    /// <summary>View pages and attachments, search, see history.</summary>
    Read = 10,

    /// <summary>Create, edit, move, delete in the subtree; restore versions.</summary>
    Write = 20,

    /// <summary>Everything in <see cref="Write"/>, plus edit the subtree's ACL and delete the folder.</summary>
    Manage = 30,
}

/// <summary>Global role. A ceiling on what ACLs can give, never a grant in itself.</summary>
public enum UserRole
{
    Reader = 0,
    Editor = 1,
    Admin = 2,
}

/// <summary>Who an ACL entry is about.</summary>
public enum AclSubjectType
{
    User = 0,
    Group = 1,

    /// <summary>Every <em>authenticated</em> user. There is no anonymous access.</summary>
    Everyone = 2,
}

public static class PermissionLevels
{
    public static PermissionLevel Max(PermissionLevel a, PermissionLevel b) => a >= b ? a : b;

    public static PermissionLevel Min(PermissionLevel a, PermissionLevel b) => a <= b ? a : b;

    /// <summary>The ceiling a global role imposes on whatever the ACLs computed.</summary>
    public static PermissionLevel Ceiling(this UserRole role) => role switch
    {
        UserRole.Reader => PermissionLevel.Read,
        UserRole.Editor => PermissionLevel.Write,
        UserRole.Admin => PermissionLevel.Manage,
        _ => PermissionLevel.None,
    };

    public static bool CanRead(this PermissionLevel level) => level >= PermissionLevel.Read;

    public static bool CanWrite(this PermissionLevel level) => level >= PermissionLevel.Write;

    public static bool CanManage(this PermissionLevel level) => level >= PermissionLevel.Manage;
}
