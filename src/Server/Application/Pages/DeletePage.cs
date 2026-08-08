using Common.Mediator;
using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Domain.Content;

namespace Compendio.Application.Pages;

public sealed record DeletePageCommand(string Path) : ICommand<Unit>;

public sealed class DeletePageHandler(
    IContentPipeline pipeline,
    IContentStore store,
    IPathPolicy paths,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser) : IRequestHandler<DeletePageCommand, Unit>
{
    public async Task<Unit> Handle(DeletePageCommand request, CancellationToken cancellationToken = default)
    {
        var path = paths.Require(request.Path, PathKind.Page);
        await permissions.RequireWriteAsync(currentUser.Subject, path, cancellationToken);

        if (!store.Exists(path))
        {
            throw CompendioException.NotFound(path);
        }

        // The pipeline snapshots the content before removing it, then tombstones the page's
        // versions for the retention window. Deleting a page must be recoverable — that is what
        // saves an organization from a mis-synced backup client.
        await pipeline.DeletePageAsync(path, currentUser.UserId, cancellationToken);
        return Unit.Value;
    }
}
