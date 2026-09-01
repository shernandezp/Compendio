using System.Text;
using Common.Mediator;
using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Application.Pages;
using Compendio.Domain.Content;
using Compendio.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace Compendio.Application.Admin;

// Deleting a page tombstones its versions for the retention window instead of dropping them. The
// pipeline has always done that, and the guide has always said an administrator can bring a deleted
// page back — these two handlers are the part that was missing between the two.

public sealed record ListDeletedPagesQuery : IQuery<IReadOnlyList<DeletedPageDto>>;

/// <summary>
/// Pages whose file is gone and whose history is still held.
/// </summary>
/// <remarks>
/// A tombstoned version whose page row still exists is not a deleted page — it is a page that was
/// deleted and later restored, or re-created at the same path, and its old versions are on their way
/// out. Only ids with no live row are listed, so "deleted" here means "gone and recoverable".
/// </remarks>
public sealed class ListDeletedPagesHandler(
    ICompendioDbContext db,
    IPageHistory history,
    ICurrentUser currentUser) : IRequestHandler<ListDeletedPagesQuery, IReadOnlyList<DeletedPageDto>>
{
    public async Task<IReadOnlyList<DeletedPageDto>> Handle(ListDeletedPagesQuery request, CancellationToken cancellationToken = default)
    {
        RequireAdmin(currentUser);

        var tombstoned = await db.PageVersions
            .AsNoTracking()
            .Where(v => v.TombstonedAt != null && !db.Pages.Any(p => p.Id == v.PageId))
            .Select(v => new { v.PageId, v.Sequence, v.Path, v.CreatedAt, v.TombstonedAt })
            .ToListAsync(cancellationToken);

        var result = new List<DeletedPageDto>();

        foreach (var group in tombstoned.GroupBy(v => v.PageId))
        {
            var last = group.OrderByDescending(v => v.Sequence).First();
            var lastId = await db.PageVersions
                .AsNoTracking()
                .Where(v => v.PageId == last.PageId && v.Sequence == last.Sequence)
                .Select(v => v.Id)
                .FirstAsync(cancellationToken);

            // The title lives in the content, and there are few deleted pages, so decoding the last
            // version for it is affordable — and a list of file names would be a list nobody
            // recognizes their page in.
            var content = await history.ContentAsync(lastId, cancellationToken);
            var title = content is null
                ? ContentPath.FromTrusted(last.Path).NameWithoutExtension
                : MarkdownParser.Parse(content).ResolveTitle(ContentPath.FromTrusted(last.Path));

            result.Add(new DeletedPageDto(
                last.PageId,
                last.Path,
                title,
                group.Max(v => v.TombstonedAt!.Value),
                last.CreatedAt,
                group.Count()));
        }

        return result.OrderByDescending(d => d.DeletedAt).ToArray();
    }

    internal static void RequireAdmin(ICurrentUser currentUser)
    {
        if (currentUser.Role != UserRole.Admin)
        {
            throw CompendioException.Forbidden(ContentPath.Root);
        }
    }
}

/// <param name="PageId">The deleted page.</param>
/// <param name="TargetPath">
/// Where to put it. Null restores it where it was; a path is for when something else now lives
/// there, which <c>path.exists</c> reports.
/// </param>
public sealed record RestoreDeletedPageCommand(Guid PageId, string? TargetPath) : ICommand<PageDto>;

/// <summary>
/// Brings a deleted page back with its history.
/// </summary>
/// <remarks>
/// The last version is what comes back — for a page deleted from the UI that is the snapshot taken
/// at deletion, so nothing written before the delete is lost. The page keeps its id, so the versions
/// that were tombstoned become its history again rather than orphans; a restore reads in history as
/// a restore, after the delete, which is the record somebody investigating later actually wants.
/// </remarks>
public sealed class RestoreDeletedPageHandler(
    ICompendioDbContext db,
    IPageHistory history,
    IContentPipeline pipeline,
    IContentStore store,
    IPathPolicy paths,
    ICurrentUser currentUser,
    PageProjection projection) : IRequestHandler<RestoreDeletedPageCommand, PageDto>
{
    public async Task<PageDto> Handle(RestoreDeletedPageCommand request, CancellationToken cancellationToken = default)
    {
        ListDeletedPagesHandler.RequireAdmin(currentUser);

        if (await db.Pages.AnyAsync(p => p.Id == request.PageId, cancellationToken))
        {
            // Already back — restored twice, or never gone. Not a deleted page either way.
            throw new CompendioException(ProblemCodes.VersionNotFound, StatusCodes.Status404NotFound);
        }

        var last = await db.PageVersions
            .AsNoTracking()
            .Where(v => v.PageId == request.PageId && v.TombstonedAt != null)
            .OrderByDescending(v => v.Sequence)
            .Select(v => new { v.Id, v.Path })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new CompendioException(ProblemCodes.VersionNotFound, StatusCodes.Status404NotFound);

        var path = paths.Require(request.TargetPath is { Length: > 0 } target ? target : last.Path, PathKind.Page);

        var content = await history.ContentAsync(last.Id, cancellationToken)
                      ?? throw new CompendioException(ProblemCodes.VersionNotFound, StatusCodes.Status404NotFound);

        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
        var page = await pipeline.RestoreDeletedPageAsync(request.PageId, path, bytes, currentUser.UserId, cancellationToken);

        var file = await store.ReadAsync(path, cancellationToken);
        return await projection.BuildAsync(page, file, includeContent: false, includeHtml: false, cancellationToken);
    }
}
