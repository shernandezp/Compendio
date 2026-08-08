using Common.Mediator;
using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Domain.Content;
using Compendio.Domain.Entities;
using Compendio.Domain.Security;
using Compendio.Hosting.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Compendio.Application.Admin;

public sealed record GetAclQuery(string Path) : IQuery<AclDto>;

public sealed class GetAclHandler(
    ICompendioDbContext db,
    IUserDirectory users,
    IPathPolicy paths,
    IPermissionEvaluator permissions,
    ISecureScopeRegistry secureScopes,
    ICurrentUser currentUser) : IRequestHandler<GetAclQuery, AclDto>
{
    public async Task<AclDto> Handle(GetAclQuery request, CancellationToken cancellationToken = default)
    {
        var path = paths.Require(request.Path, PathKind.Folder);
        await permissions.RequireManageAsync(currentUser.Subject, path, cancellationToken);

        var node = await db.AclNodes
            .Include(n => n.Entries)
            .AsNoTracking()
            .FirstOrDefaultAsync(n => n.FolderPath == path.Value, cancellationToken);

        var names = await users.SubjectNamesAsync(cancellationToken);

        // What the parent already grants, so the UI can say "currently: Everyone can read" out loud
        // rather than making an admin trace it.
        var inherited = new List<AclEntryDto>();
        foreach (var ancestor in path.SelfAndAncestors().Where(a => a.Value != path.Value))
        {
            var ancestorNode = await db.AclNodes
                .Include(n => n.Entries)
                .AsNoTracking()
                .FirstOrDefaultAsync(n => n.FolderPath == ancestor.Value, cancellationToken);

            if (ancestorNode is not null)
            {
                inherited.AddRange(ancestorNode.Entries.Select(e => Map(e, names)));
            }
        }

        return new AclDto(
            path.Value,
            node?.InheritParent ?? true,
            node?.Entries.Select(e => Map(e, names)).ToArray() ?? [],
            inherited,
            await secureScopes.IsSecureAsync(path, cancellationToken),
            node?.UpdatedAt);
    }

    internal static AclEntryDto Map(AclEntry entry, IReadOnlyDictionary<Guid, string> names) =>
        new(entry.SubjectType,
            entry.SubjectId,
            entry.SubjectType == AclSubjectType.Everyone
                ? "Everyone"
                : entry.SubjectId is { } id ? names.GetValueOrDefault(id, "(deleted)") : "(deleted)",
            entry.Level);
}

public sealed record SetAclEntry(AclSubjectType SubjectType, Guid? SubjectId, PermissionLevel Level);

/// <param name="InheritParent">
/// The whole model in one boolean: inherit and only add, or cut inheritance and list exactly who
/// gets in. There is no third state and there are no deny entries — every restriction anyone
/// actually wants is expressible as the second option, which is also what the UI says.
/// </param>
public sealed record SetAclCommand(string Path, bool InheritParent, IReadOnlyList<SetAclEntry> Entries) : ICommand<AclDto>;

public sealed class SetAclHandler(
    ICompendioDbContext db,
    IUserDirectory users,
    IPathPolicy paths,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser,
    IClock clock,
    ISender sender) : IRequestHandler<SetAclCommand, AclDto>
{
    public async Task<AclDto> Handle(SetAclCommand request, CancellationToken cancellationToken = default)
    {
        var path = paths.Require(request.Path, PathKind.Folder);
        await permissions.RequireManageAsync(currentUser.Subject, path, cancellationToken);

        var granterLevel = await permissions.EffectiveAsync(currentUser.Subject, path, cancellationToken);

        var node = await db.AclNodes
            .Include(n => n.Entries)
            .FirstOrDefaultAsync(n => n.FolderPath == path.Value, cancellationToken);

        var before = node is null
            ? null
            : System.Text.Json.JsonSerializer.Serialize(new
            {
                node.InheritParent,
                Entries = node.Entries.Select(e => new { e.SubjectType, e.SubjectId, e.Level }),
            });

        if (node is null)
        {
            node = new AclNode { Id = Guid.CreateVersion7(), FolderPath = path.Value };
            db.AclNodes.Add(node);
        }
        else
        {
            db.AclEntries.RemoveRange(node.Entries);
        }

        node.InheritParent = request.InheritParent;
        node.TombstonedAt = null;
        node.UpdatedAt = clock.UtcNow;
        node.UpdatedByUserId = currentUser.UserId;

        var userIds = await users.UserIdsAsync(cancellationToken);
        var groupIds = await users.GroupIdsAsync(cancellationToken);

        foreach (var entry in request.Entries)
        {
            var valid = entry.SubjectType switch
            {
                AclSubjectType.Everyone => true,
                AclSubjectType.User => entry.SubjectId is { } id && userIds.Contains(id),
                AclSubjectType.Group => entry.SubjectId is { } id && groupIds.Contains(id),
                _ => false,
            };

            if (!valid)
            {
                throw CompendioException.BadRequest(ProblemCodes.AclInvalidSubject);
            }

            // You cannot grant more than you have. Delegated administration stops at the granter's
            // own level, which keeps "who could possibly have done this" answerable.
            var level = PermissionLevels.Min(entry.Level, granterLevel);

            db.AclEntries.Add(new AclEntry
            {
                Id = Guid.CreateVersion7(),
                AclNodeId = node.Id,
                SubjectType = entry.SubjectType,
                SubjectId = entry.SubjectType == AclSubjectType.Everyone ? null : entry.SubjectId,
                Level = level,
            });
        }

        db.AuditLog.Add(new AuditEntry
        {
            Id = Guid.CreateVersion7(),
            At = clock.UtcNow,
            ActorUserId = currentUser.UserId,
            Action = "acl.set",
            TargetType = "folder",
            TargetPath = path.Value,
            BeforeJson = before,
            AfterJson = System.Text.Json.JsonSerializer.Serialize(new { request.InheritParent, request.Entries }),
        });

        await db.SaveChangesAsync(cancellationToken);
        permissions.Invalidate();

        return await sender.Send(new GetAclQuery(path.Value), cancellationToken);
    }
}

/// <summary>
/// "What can this person do here, and why."
/// </summary>
/// <remarks>
/// Worth building in the MVP: it turns the single most common support question into self-service,
/// and it exercises the evaluator from the UI, so a bug in it is visible rather than theoretical.
/// </remarks>
public sealed record EffectiveAccessQuery(string Path, Guid UserId) : IQuery<EffectiveAccessDto>;

public sealed class EffectiveAccessHandler(
    ICompendioDbContext db,
    IUserDirectory users,
    IPathPolicy paths,
    IPermissionEvaluator permissions,
    ISecureScopeRegistry secureScopes,
    ICurrentUser currentUser,
    IOptions<CompendioOptions> options) : IRequestHandler<EffectiveAccessQuery, EffectiveAccessDto>
{
    public async Task<EffectiveAccessDto> Handle(EffectiveAccessQuery request, CancellationToken cancellationToken = default)
    {
        var path = paths.Require(request.Path, PathKind.Folder);
        await permissions.RequireManageAsync(currentUser.Subject, path, cancellationToken);

        var resolved = await users.SubjectAsync(request.UserId, cancellationToken)
                       ?? throw CompendioException.BadRequest(ProblemCodes.AclInvalidSubject);

        var (subject, displayName) = resolved;
        var level = await permissions.EffectiveAsync(subject, path, cancellationToken);

        return new EffectiveAccessDto(subject.UserId, displayName, level,
            await ExplainAsync(subject, path, level, subject.GroupIds, cancellationToken));
    }

    /// <summary>Names the rule that decided it, in one sentence.</summary>
    private async Task<string> ExplainAsync(
        PermissionSubject subject,
        ContentPath path,
        PermissionLevel level,
        IReadOnlySet<Guid> groups,
        CancellationToken cancellationToken)
    {
        if (subject.Role == UserRole.Admin)
        {
            return "role.admin";
        }

        if (await secureScopes.IsSecureAsync(path, cancellationToken) && level == PermissionLevel.Read)
        {
            return "secure.readOnly";
        }

        if (level == subject.Role.Ceiling() && level < PermissionLevel.Manage)
        {
            return $"role.ceiling.{subject.Role}";
        }

        foreach (var ancestor in path.SelfAndAncestors().Reverse())
        {
            var node = await db.AclNodes
                .Include(n => n.Entries)
                .AsNoTracking()
                .FirstOrDefaultAsync(n => n.FolderPath == ancestor.Value, cancellationToken);

            if (node is null)
            {
                continue;
            }

            foreach (var entry in node.Entries.Where(e => e.Level == level))
            {
                if (entry.SubjectType == AclSubjectType.User && entry.SubjectId == subject.UserId)
                {
                    return $"acl.user:{ancestor.Value}";
                }

                if (entry.SubjectType == AclSubjectType.Group && entry.SubjectId is { } gid && groups.Contains(gid))
                {
                    return $"acl.group:{ancestor.Value}";
                }

                if (entry.SubjectType == AclSubjectType.Everyone)
                {
                    return $"acl.everyone:{ancestor.Value}";
                }
            }
        }

        return level == options.Value.Instance.DefaultAccess ? "instance.default" : "inherited";
    }
}
