using Compendio.Domain.Content;

namespace Compendio.Domain.Security;

/// <summary>An ACL entry, flattened for evaluation.</summary>
public readonly record struct AclEntrySnapshot(AclSubjectType SubjectType, Guid? SubjectId, PermissionLevel Level);

/// <summary>A folder's ACL, flattened for evaluation.</summary>
/// <param name="InheritParent">
/// <c>true</c> — the folder can only <em>add</em> access. <c>false</c> — inheritance is cut and the
/// folder is exactly its own entry list. There is no third state and there are no deny entries.
/// </param>
public sealed record AclNodeSnapshot(
    ContentPath Path,
    bool InheritParent,
    IReadOnlyList<AclEntrySnapshot> Entries,
    bool IsTombstoned = false);

/// <summary>Who is asking. Groups are pre-flattened; group nesting is not supported.</summary>
public sealed record PermissionSubject(Guid UserId, UserRole Role, IReadOnlySet<Guid> GroupIds)
{
    public static PermissionSubject Anonymous { get; } =
        new(Guid.Empty, UserRole.Reader, new HashSet<Guid>());
}

/// <summary>
/// The permission model, as a pure function of an ACL snapshot.
/// </summary>
/// <remarks>
/// Kept free of storage and caching so the permission matrix test can drive it directly with a
/// literal table. The caching evaluator in <c>Infrastructure/Security</c> is a memoizing wrapper
/// over exactly this, and there is no second implementation of the rules.
/// </remarks>
public static class PermissionRules
{
    /// <summary>
    /// Effective level for a subject at a path.
    /// </summary>
    /// <param name="subject">Who is asking.</param>
    /// <param name="folderPath">The folder. Pages and attachments evaluate at their folder.</param>
    /// <param name="aclNodes">ACL rows by folder path. Folders that inherit store nothing.</param>
    /// <param name="instanceDefault">
    /// <see cref="PermissionLevel.Read"/> on a normal install, <see cref="PermissionLevel.None"/>
    /// on a locked-down one. Applies at the root before any ACL is considered.
    /// </param>
    /// <param name="isInsideSecureScope">
    /// Whether the path sits inside a secure scope. Inside one, non-admins are capped at
    /// <see cref="PermissionLevel.Read"/> however generous the ACL is — the "only administrators
    /// can edit" rule, enforced here rather than in the UI.
    /// </param>
    public static PermissionLevel Effective(
        PermissionSubject subject,
        ContentPath folderPath,
        IReadOnlyDictionary<string, AclNodeSnapshot> aclNodes,
        PermissionLevel instanceDefault,
        bool isInsideSecureScope)
    {
        if (subject.Role == UserRole.Admin)
        {
            return PermissionLevel.Manage;
        }

        var level = instanceDefault;

        foreach (var node in folderPath.SelfAndAncestors())
        {
            if (!aclNodes.TryGetValue(node.Value, out var acl))
            {
                continue;
            }

            // inherit_parent = true  → the folder may only add access.
            // inherit_parent = false → the folder is exactly its own entry list.
            var baseLevel = acl.InheritParent ? level : PermissionLevel.None;
            var granted = HighestMatching(subject, acl);
            level = PermissionLevels.Max(baseLevel, granted);
        }

        if (isInsideSecureScope)
        {
            level = PermissionLevels.Min(level, PermissionLevel.Read);
        }

        // A global role is a ceiling, never a grant: a Reader given `manage` still only reads.
        return PermissionLevels.Min(level, subject.Role.Ceiling());
    }

    private static PermissionLevel HighestMatching(PermissionSubject subject, AclNodeSnapshot acl)
    {
        var highest = PermissionLevel.None;

        foreach (var entry in acl.Entries)
        {
            var matches = entry.SubjectType switch
            {
                AclSubjectType.Everyone => true,
                AclSubjectType.User => entry.SubjectId == subject.UserId,
                AclSubjectType.Group => entry.SubjectId is { } id && subject.GroupIds.Contains(id),
                _ => false,
            };

            if (matches)
            {
                highest = PermissionLevels.Max(highest, entry.Level);
            }
        }

        return highest;
    }

    /// <summary>
    /// Whether <paramref name="path"/> is inside one of the secure scopes. A scope covers itself
    /// and everything below it; nesting a scope inside a scope is rejected at creation time.
    /// </summary>
    public static bool IsInsideSecureScope(ContentPath path, IReadOnlyCollection<ContentPath> secureScopes)
    {
        foreach (var scope in secureScopes)
        {
            if (path.IsSelfOrUnder(scope))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The scope covering a path, if any. Used to pick the data key.</summary>
    public static ContentPath? ScopeFor(ContentPath path, IReadOnlyCollection<ContentPath> secureScopes)
    {
        ContentPath? deepest = null;

        foreach (var scope in secureScopes)
        {
            if (path.IsSelfOrUnder(scope) && (deepest is null || scope.Value.Length > deepest.Value.Value.Length))
            {
                deepest = scope;
            }
        }

        return deepest;
    }

    /// <summary>
    /// Every folder the subject can read, given the full folder list.
    /// </summary>
    /// <remarks>
    /// This is what materializes <c>readable_folders</c> for the search predicate. Computing it in
    /// one pass over a sorted list keeps it O(n) rather than O(n·depth), which matters because it
    /// is recomputed whenever the ACL version changes.
    /// </remarks>
    public static IReadOnlyList<ContentPath> ReadableFolders(
        PermissionSubject subject,
        IReadOnlyList<ContentPath> allFolders,
        IReadOnlyDictionary<string, AclNodeSnapshot> aclNodes,
        PermissionLevel instanceDefault,
        IReadOnlyCollection<ContentPath> secureScopes)
    {
        if (subject.Role == UserRole.Admin)
        {
            return allFolders;
        }

        var readable = new List<ContentPath>(allFolders.Count);

        foreach (var folder in allFolders)
        {
            var secure = IsInsideSecureScope(folder, secureScopes);
            if (Effective(subject, folder, aclNodes, instanceDefault, secure).CanRead())
            {
                readable.Add(folder);
            }
        }

        return readable;
    }
}
