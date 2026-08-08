using Common.Mediator;
using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Domain.Content;
using Compendio.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace Compendio.Application.Tree;

public sealed record GetTreeQuery : IQuery<TreeDto>;

/// <summary>
/// The navigation tree, filtered by the evaluator.
/// </summary>
/// <remarks>
/// A node the caller cannot read is <em>absent</em>, not greyed out. Folder names leak — an
/// organization's structure is often the sensitive part — and a visible-but-locked placeholder
/// invites a support ticket for every one of them. There is deliberately no configuration option
/// for the other behaviour.
/// </remarks>
public sealed class GetTreeHandler(
    ICompendioDbContext db,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser) : IRequestHandler<GetTreeQuery, TreeDto>
{
    public async Task<TreeDto> Handle(GetTreeQuery request, CancellationToken cancellationToken = default)
    {
        var subject = currentUser.Subject;
        var readable = await permissions.ReadableFolderPathsAsync(subject, cancellationToken);
        var isAdmin = subject.Role == UserRole.Admin;

        // The caller's level at the root: what the top-level "New page"/"New folder" affordances,
        // which create at the root, must gate on.
        var rootLevel = await permissions.EffectiveAsync(subject, ContentPath.Root, cancellationToken);

        var folders = await db.Folders
            .AsNoTracking()
            .OrderBy(f => f.Path)
            .Select(f => new { f.Id, f.Path, f.Name, f.IsSecure })
            .ToListAsync(cancellationToken);

        var pages = await db.Pages
            .AsNoTracking()
            .OrderBy(p => p.Title)
            .Select(p => new { p.Path, p.Title, p.Lang, p.IsSecure, p.FolderId })
            .ToListAsync(cancellationToken);

        var visibleFolders = folders
            .Where(f => isAdmin || readable.Contains(f.Path))
            .ToList();

        var levels = new Dictionary<string, PermissionLevel>(StringComparer.Ordinal);
        foreach (var folder in visibleFolders)
        {
            levels[folder.Path] = await permissions.EffectiveAsync(subject, ContentPath.FromTrusted(folder.Path), cancellationToken);
        }

        var pagesByFolder = pages
            .Where(p => visibleFolders.Any(f => f.Id == p.FolderId))
            .GroupBy(p => p.FolderId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var nodesByPath = new Dictionary<string, List<TreeNodeDto>>(StringComparer.Ordinal);

        // Deepest first, so a folder's children are already built when it is.
        foreach (var folder in visibleFolders.OrderByDescending(f => f.Path.Length))
        {
            var level = levels.GetValueOrDefault(folder.Path, PermissionLevel.None);

            var children = new List<TreeNodeDto>();
            if (nodesByPath.TryGetValue(folder.Path, out var subfolders))
            {
                children.AddRange(subfolders.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase));
            }

            if (pagesByFolder.TryGetValue(folder.Id, out var folderPages))
            {
                children.AddRange(folderPages.Select(p => new TreeNodeDto
                {
                    Path = p.Path,
                    Name = ContentPath.FromTrusted(p.Path).Name,
                    Title = p.Title,
                    IsFolder = false,
                    IsSecure = p.IsSecure,
                    Level = level,
                    Lang = p.Lang,
                }));
            }

            if (folder.Path.Length == 0)
            {
                // The root itself is not a node; its children are the top level.
                nodesByPath[string.Empty] = children;
                continue;
            }

            var node = new TreeNodeDto
            {
                Path = folder.Path,
                Name = folder.Name,
                Title = folder.Name,
                IsFolder = true,
                IsSecure = folder.IsSecure,
                Level = level,
                Children = children,
            };

            var parent = ContentPath.FromTrusted(folder.Path).Parent.Value;
            if (!nodesByPath.TryGetValue(parent, out var siblings))
            {
                siblings = [];
                nodesByPath[parent] = siblings;
            }

            siblings.Add(node);
        }

        var top = nodesByPath.GetValueOrDefault(string.Empty, []);

        // Ordering is stable but not localized: the client sorts with Intl.Collator in the resolved
        // locale, because Spanish alphabetization of ñ and accents is a client concern.
        var nodes = top.OrderByDescending(n => n.IsFolder).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        return new TreeDto(rootLevel, nodes);
    }
}
