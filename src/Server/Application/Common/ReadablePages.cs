using Compendio.Application.Abstractions;
using Compendio.Domain.Entities;
using Compendio.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace Compendio.Application.Common;

/// <summary>
/// The permission predicate for read surfaces that query <c>Pages</c> directly.
/// </summary>
/// <remarks>
/// <para>
/// Search has its own materialization inside <c>Infrastructure/Search/</c>; the lifecycle,
/// notification and acknowledgment surfaces read the page table instead, and they need the same
/// guarantee. This puts the folder set into the <c>WHERE</c> clause, so counts and paging are
/// computed over what the caller can actually reach.
/// </para>
/// <para>
/// Post-filtering a result page is the alternative and it is wrong for the reasons the search design
/// note gives: it breaks paging, leaks totals, and produces empty pages that themselves prove hidden
/// matches exist.
/// </para>
/// </remarks>
public sealed class ReadablePages(ICompendioDbContext db, IPermissionEvaluator permissions)
{
    /// <summary>
    /// Pages the subject can read, as a composable query.
    /// </summary>
    /// <remarks>
    /// Admins bypass the folder filter entirely rather than being handed a set containing every
    /// folder — the same shortcut <see cref="ISearchIndex"/> takes, and for the same reason.
    /// </remarks>
    public async Task<IQueryable<Page>> QueryAsync(PermissionSubject subject, CancellationToken cancellationToken = default)
    {
        var pages = db.Pages.AsNoTracking();

        if (subject.Role == UserRole.Admin)
        {
            return pages;
        }

        var readable = await permissions.ReadableFolderPathsAsync(subject, cancellationToken);
        if (readable.Count == 0)
        {
            return pages.Where(_ => false);
        }

        var folderIds = await db.Folders
            .AsNoTracking()
            .Where(f => readable.Contains(f.Path))
            .Select(f => f.Id)
            .ToListAsync(cancellationToken);

        return pages.Where(p => folderIds.Contains(p.FolderId));
    }

    /// <summary>
    /// Whether one already-stored path is readable, without throwing.
    /// </summary>
    /// <remarks>
    /// The notification inbox needs this: a row is written when something happens and read later,
    /// and access can change in between. Filtering at read time is what stops a stale row from
    /// becoming a way to learn that a page exists.
    /// </remarks>
    public async Task<bool> CanReadAsync(PermissionSubject subject, string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(path))
        {
            // An instance-level notice with no page behind it. Nothing to leak.
            return true;
        }

        var content = Domain.Content.ContentPath.FromTrusted(path);
        return await permissions.EffectiveAsync(subject, content, cancellationToken) >= PermissionLevel.Read;
    }
}
