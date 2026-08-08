using System.Text;
using Common.Mediator;
using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Domain.Content;
using Microsoft.EntityFrameworkCore;

namespace Compendio.Application.History;

public sealed record ListVersionsQuery(string Path) : IQuery<IReadOnlyList<VersionSummary>>;

public sealed class ListVersionsHandler(
    ICompendioDbContext db,
    IPageHistory history,
    IPathPolicy paths,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser) : IRequestHandler<ListVersionsQuery, IReadOnlyList<VersionSummary>>
{
    public async Task<IReadOnlyList<VersionSummary>> Handle(ListVersionsQuery request, CancellationToken cancellationToken = default)
    {
        var path = paths.Require(request.Path, PathKind.Page);
        await permissions.RequireReadAsync(currentUser.Subject, path, cancellationToken);

        var pageId = await db.Pages.Where(p => p.Path == path.Value).Select(p => p.Id).FirstOrDefaultAsync(cancellationToken);
        return pageId == Guid.Empty ? [] : await history.ListAsync(pageId, cancellationToken);
    }
}

public sealed record GetVersionQuery(Guid VersionId) : IQuery<VersionContentDto>;

public sealed record VersionContentDto(Guid Id, string Path, string Content, DateTimeOffset CreatedAt);

public sealed class GetVersionHandler(
    ICompendioDbContext db,
    IPageHistory history,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser) : IRequestHandler<GetVersionQuery, VersionContentDto>
{
    public async Task<VersionContentDto> Handle(GetVersionQuery request, CancellationToken cancellationToken = default)
    {
        var version = await db.PageVersions.AsNoTracking()
            .Where(v => v.Id == request.VersionId)
            .Select(v => new { v.Id, v.Path, v.CreatedAt })
            .FirstOrDefaultAsync(cancellationToken);

        if (version is null)
        {
            throw new CompendioException(ProblemCodes.VersionNotFound, StatusCodes.Status404NotFound);
        }

        // Access is decided by the page's *current* folder, not by where the page used to live.
        await permissions.RequireReadAsync(currentUser.Subject, ContentPath.FromTrusted(version.Path), cancellationToken);

        var content = await history.ContentAsync(request.VersionId, cancellationToken)
                      ?? throw new CompendioException(ProblemCodes.VersionNotFound, StatusCodes.Status404NotFound);

        return new VersionContentDto(version.Id, version.Path, content, version.CreatedAt);
    }
}

public sealed record GetDiffQuery(string Path, Guid From, Guid To) : IQuery<PageDiff>;

public sealed class GetDiffHandler(
    ICompendioDbContext db,
    IPageHistory history,
    IPathPolicy paths,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser) : IRequestHandler<GetDiffQuery, PageDiff>
{
    public async Task<PageDiff> Handle(GetDiffQuery request, CancellationToken cancellationToken = default)
    {
        var path = paths.Require(request.Path, PathKind.Page);
        await permissions.RequireReadAsync(currentUser.Subject, path, cancellationToken);

        // The permission check is on `path`, so both versions have to belong to the page that lives
        // there. Without this the caller picks the path they can read and the version ids of one
        // they cannot, and the diff hands back its content.
        await VersionOwnership.RequireBelongToPageAsync(db, path, [request.From, request.To], cancellationToken);

        return await history.DiffAsync(request.From, request.To, cancellationToken)
               ?? throw new CompendioException(ProblemCodes.VersionNotFound, StatusCodes.Status404NotFound);
    }
}

/// <summary>
/// Binds a version id to the page whose path was authorized.
/// </summary>
/// <remarks>
/// Every history endpoint authorizes a <em>path</em> and then acts on a <em>version id</em>. Those
/// are two different keys, and nothing else in the request ties them together — so this does, in one
/// place, for every endpoint that takes both. A version that belongs to another page is reported as
/// missing rather than as forbidden, for the same reason an unreadable page is a 404.
/// </remarks>
internal static class VersionOwnership
{
    public static async Task<Guid> RequireBelongToPageAsync(
        ICompendioDbContext db,
        ContentPath path,
        IReadOnlyCollection<Guid> versionIds,
        CancellationToken cancellationToken)
    {
        var pageId = await db.Pages
            .Where(p => p.Path == path.Value)
            .Select(p => p.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (pageId == Guid.Empty)
        {
            throw CompendioException.NotFound(path);
        }

        var owned = await db.PageVersions
            .Where(v => versionIds.Contains(v.Id) && v.PageId == pageId)
            .CountAsync(cancellationToken);

        if (owned != versionIds.Distinct().Count())
        {
            throw new CompendioException(ProblemCodes.VersionNotFound, StatusCodes.Status404NotFound);
        }

        return pageId;
    }
}

public sealed record RestoreVersionCommand(string Path, Guid VersionId) : ICommand<PageDto>;

/// <summary>
/// Restores a version by writing a new one.
/// </summary>
/// <remarks>
/// Never a rewind. A restore that discarded the versions after it would make a mistaken restore
/// unrecoverable, which is the one thing history exists to prevent.
/// </remarks>
public sealed class RestoreVersionHandler(
    ICompendioDbContext db,
    IPageHistory history,
    IContentPipeline pipeline,
    IContentStore store,
    IPathPolicy paths,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser,
    Pages.PageProjection projection) : IRequestHandler<RestoreVersionCommand, PageDto>
{
    public async Task<PageDto> Handle(RestoreVersionCommand request, CancellationToken cancellationToken = default)
    {
        var path = paths.Require(request.Path, PathKind.Page);
        await permissions.RequireWriteAsync(currentUser.Subject, path, cancellationToken);

        // The version has to be this page's own history. Otherwise a restore is a way to copy the
        // content of a page the caller cannot read into one they can — with write access to their
        // own page being the only thing they needed.
        await VersionOwnership.RequireBelongToPageAsync(db, path, [request.VersionId], cancellationToken);

        var content = await history.ContentAsync(request.VersionId, cancellationToken)
                      ?? throw new CompendioException(ProblemCodes.VersionNotFound, StatusCodes.Status404NotFound);

        var current = await store.ReadAsync(path, cancellationToken) ?? throw CompendioException.NotFound(path);
        var sequence = await db.PageVersions
            .Where(v => v.Id == request.VersionId)
            .Select(v => v.Sequence)
            .FirstOrDefaultAsync(cancellationToken);

        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);

        // Recorded as a restore, not as an ordinary edit: history that does not say "this was a
        // restore of v3" is history somebody has to reconstruct from timestamps.
        var page = await pipeline.SavePageAsync(
            path, bytes, current.ContentHash, currentUser.UserId,
            Domain.Entities.VersionSource.Restore, note: $"restore:v{sequence}", cancellationToken);

        var file = await store.ReadAsync(path, cancellationToken);
        return await projection.BuildAsync(page, file, includeContent: true, includeHtml: true, cancellationToken);
    }
}
