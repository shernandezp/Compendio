using Compendio.Domain.Security;

namespace Compendio.Domain.Entities;

public sealed class Group
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool Active { get; set; } = true;

    public ICollection<GroupMember> Members { get; set; } = [];
}

public sealed class GroupMember
{
    public Guid Id { get; set; }

    public Guid GroupId { get; set; }

    public Guid UserId { get; set; }

    public Group? Group { get; set; }
}

/// <summary>
/// A folder's ACL. Folders with no row simply inherit, so the common case stores nothing.
/// </summary>
/// <remarks>
/// ACLs live here and never in front matter: front matter is writable from disk, so a
/// <c>git revert</c> or a file-sync client could otherwise silently reopen a restricted folder.
/// </remarks>
public sealed class AclNode
{
    public Guid Id { get; set; }

    public string FolderPath { get; set; } = string.Empty;

    /// <summary>
    /// <c>true</c> — the folder may only add access. <c>false</c> — restricted: inheritance is cut
    /// and the folder is exactly its own entry list.
    /// </summary>
    public bool InheritParent { get; set; } = true;

    /// <summary>
    /// Set when the path disappears without a correlated create. The row is kept for the retention
    /// window rather than deleted, so a folder removed and restored by a backup client does not
    /// come back inheriting — that is, wide open.
    /// </summary>
    public DateTimeOffset? TombstonedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedByUserId { get; set; }

    public ICollection<AclEntry> Entries { get; set; } = [];
}

/// <summary>One grant. There are no deny entries, by design.</summary>
public sealed class AclEntry
{
    public Guid Id { get; set; }

    public Guid AclNodeId { get; set; }

    public AclSubjectType SubjectType { get; set; }

    /// <summary>Null for <see cref="AclSubjectType.Everyone"/>.</summary>
    public Guid? SubjectId { get; set; }

    public PermissionLevel Level { get; set; }

    public AclNode? AclNode { get; set; }
}

/// <summary>
/// An encrypted folder. Encryption is a property of a folder, inherited downward — a page's
/// attachments and history have to travel with it.
/// </summary>
public sealed class SecureScope
{
    public Guid Id { get; set; }

    public string FolderPath { get; set; } = string.Empty;

    /// <summary>Identifies the data key. Appears in every envelope this scope wrote.</summary>
    public Guid KeyId { get; set; }

    /// <summary>The data key, AES-256-GCM under the master key. Useless without <c>keys/</c>.</summary>
    public byte[] WrappedDek { get; set; } = [];

    public byte[] Nonce { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? RotatedAt { get; set; }

    /// <summary>Set on a retired key so historical snapshots stay readable until rewritten.</summary>
    public DateTimeOffset? RetiredAt { get; set; }

    /// <summary>
    /// Opt-in, per scope, behind an explicit warning: indexing copies plaintext into
    /// <c>compendio.db</c>, where anyone with the database file can read it.
    /// </summary>
    public bool IndexContent { get; set; }

    /// <summary>Consumed by v1. Secure scopes are excluded from AI context unless set.</summary>
    public bool AllowAi { get; set; }

    /// <summary>
    /// Files encrypted under this key. <c>doctor</c> flags a key that has encrypted an implausible
    /// number of files, which is the cheap check against nonce exhaustion.
    /// </summary>
    public long EncryptionCount { get; set; }
}
