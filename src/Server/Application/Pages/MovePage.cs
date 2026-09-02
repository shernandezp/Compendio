using Common.Mediator;
using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Domain.Content;

namespace Compendio.Application.Pages;

public sealed record MovePageCommand(string Path, string TargetPath) : ICommand<PageDto>;

public sealed class MovePageHandler(
    IContentPipeline pipeline,
    IContentStore store,
    IPathPolicy paths,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser,
    PageProjection projection) : IRequestHandler<MovePageCommand, PageDto>
{
    public async Task<PageDto> Handle(MovePageCommand request, CancellationToken cancellationToken = default)
    {
        var from = paths.Require(request.Path, PathKind.Page);
        var to = paths.Require(request.TargetPath, PathKind.Page);

        // Both ends are checked. Write at the source alone would let somebody relocate a page into
        // a folder they cannot write to; write at the target alone would let them remove it from
        // one they cannot.
        await permissions.RequireWriteAsync(currentUser.Subject, from, cancellationToken);
        await permissions.RequireWriteAsync(currentUser.Subject, to, cancellationToken);

        if (!store.Exists(from))
        {
            throw CompendioException.NotFound(from);
        }

        // A rename that only changes case finds itself at the destination on a case-insensitive
        // disk; the store distinguishes that from a real collision, so the check is left to it.
        if (!to.IsCaseVariantOf(from) && store.Exists(to))
        {
            throw CompendioException.Exists(to);
        }

        var page = await pipeline.MovePageAsync(from, to, currentUser.UserId, cancellationToken);
        var file = await store.ReadAsync(to, cancellationToken);

        return await projection.BuildAsync(page, file, includeContent: false, includeHtml: false, cancellationToken);
    }
}
