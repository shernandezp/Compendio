using Common.Mediator;
using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Application.Lifecycle;
using Compendio.Domain.Content;

namespace Compendio.Application.Pages;

/// <summary>
/// Changes a page's title, which lives in front matter.
/// </summary>
/// <param name="Title">The new title. Trimmed; required.</param>
/// <remarks>
/// The title and the file name are decoupled by design — the accented, human title sits in front
/// matter and the file name is an ASCII slug — so changing a title touches the <c>title:</c> key
/// only and never renames the file or moves the page. That is why this is a metadata edit rather
/// than a move: the URL, the backlinks and any bookmark all keep working.
/// </remarks>
public sealed record SetPageTitleCommand(string Path, string Title) : ICommand<PageDto>;

public sealed class SetPageTitleHandler(
    PageMetadataWriter writer,
    IPathPolicy paths,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser,
    IContentStore store,
    PageProjection projection) : IRequestHandler<SetPageTitleCommand, PageDto>
{
    public async Task<PageDto> Handle(SetPageTitleCommand request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            throw new ValidationException(new Dictionary<string, string[]> { ["title"] = ["required"] });
        }

        var path = paths.Require(request.Path, PathKind.Page);
        await permissions.RequireWriteAsync(currentUser.Subject, path, cancellationToken);

        // Front matter only: the body is copied verbatim, so the diff is the one changed line and
        // the page's canonical state is left untouched.
        var page = await writer.ApplyAsync(
            path,
            front => front with { Title = request.Title.Trim() },
            currentUser.UserId,
            note: "title",
            cancellationToken);

        var file = await store.ReadAsync(path, cancellationToken);
        return await projection.BuildAsync(page, file, includeContent: true, includeHtml: true, cancellationToken);
    }
}
