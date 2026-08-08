using Common.Mediator;
using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Domain.Content;
using Microsoft.EntityFrameworkCore;

namespace Compendio.Application.Pages;

/// <param name="Raw">Skip rendering and return the Markdown only. What the editor asks for.</param>
public sealed record GetPageQuery(string Path, bool Raw = false) : IQuery<PageDto>;

public sealed class GetPageHandler(
    ICompendioDbContext db,
    IContentStore store,
    IPathPolicy paths,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser,
    PageProjection projection) : IRequestHandler<GetPageQuery, PageDto>
{
    public async Task<PageDto> Handle(GetPageQuery request, CancellationToken cancellationToken = default)
    {
        var path = paths.Require(request.Path, PathKind.Page);

        // Read permission first, and a failure is a 404: a 403 would confirm the page exists.
        await permissions.RequireReadAsync(currentUser.Subject, path, cancellationToken);

        var page = await db.Pages.AsNoTracking().FirstOrDefaultAsync(p => p.Path == path.Value, cancellationToken)
                   ?? throw CompendioException.NotFound(path);

        var file = await store.ReadAsync(path, cancellationToken)
                   ?? throw CompendioException.NotFound(path);

        return await projection.BuildAsync(page, file, includeContent: true, includeHtml: !request.Raw, cancellationToken);
    }
}
