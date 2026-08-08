using Common.Mediator;
using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Domain.Content;

namespace Compendio.Application.Folders;

public sealed record CreateFolderCommand(string ParentPath, string Name) : ICommand<TreeNodeDto>;

public sealed class CreateFolderHandler(
    IContentPipeline pipeline,
    IContentStore store,
    IPathPolicy paths,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser) : IRequestHandler<CreateFolderCommand, TreeNodeDto>
{
    public async Task<TreeNodeDto> Handle(CreateFolderCommand request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ValidationException(new Dictionary<string, string[]> { ["name"] = ["required"] });
        }

        var parent = paths.Require(request.ParentPath, PathKind.Folder);
        await permissions.RequireWriteAsync(currentUser.Subject, parent, cancellationToken);

        var folderName = Slug.Disambiguate(Slug.Create(request.Name), candidate => store.FolderExists(parent.Append(candidate)));
        var path = paths.Require(parent.Append(folderName).Value, PathKind.Folder);

        var folder = await pipeline.EnsureFolderAsync(path, cancellationToken);
        var level = await permissions.EffectiveAsync(currentUser.Subject, path, cancellationToken);

        return new TreeNodeDto
        {
            Path = folder.Path,
            Name = folder.Name,
            Title = folder.Name,
            IsFolder = true,
            IsSecure = folder.IsSecure,
            Level = level,
        };
    }
}

public sealed record MoveFolderCommand(string Path, string TargetPath) : ICommand<Unit>;

public sealed class MoveFolderHandler(
    IContentPipeline pipeline,
    IContentStore store,
    IPathPolicy paths,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser) : IRequestHandler<MoveFolderCommand, Unit>
{
    public async Task<Unit> Handle(MoveFolderCommand request, CancellationToken cancellationToken = default)
    {
        var from = paths.Require(request.Path, PathKind.Folder);
        var to = paths.Require(request.TargetPath, PathKind.Folder);

        if (from.IsRoot)
        {
            throw CompendioException.InvalidPath(PathRule.EscapesRoot);
        }

        if (to.IsSelfOrUnder(from))
        {
            // Moving a folder into itself produces an unreachable subtree and a very confusing
            // tree query afterwards.
            throw CompendioException.InvalidPath(PathRule.EscapesRoot);
        }

        // Manage at the source, because moving a folder is as destructive as deleting it from
        // where it was; write at the destination is enough to put it there.
        await permissions.RequireManageAsync(currentUser.Subject, from, cancellationToken);
        await permissions.RequireWriteAsync(currentUser.Subject, to.Parent, cancellationToken);

        if (store.FolderExists(to))
        {
            throw CompendioException.Exists(to);
        }

        await pipeline.MoveFolderAsync(from, to, currentUser.UserId, cancellationToken);
        return Unit.Value;
    }
}

public sealed record DeleteFolderCommand(string Path) : ICommand<Unit>;

public sealed class DeleteFolderHandler(
    IContentPipeline pipeline,
    IPathPolicy paths,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser) : IRequestHandler<DeleteFolderCommand, Unit>
{
    public async Task<Unit> Handle(DeleteFolderCommand request, CancellationToken cancellationToken = default)
    {
        var path = paths.Require(request.Path, PathKind.Folder);

        if (path.IsRoot)
        {
            throw CompendioException.InvalidPath(PathRule.EscapesRoot);
        }

        // Deleting the folder itself takes `manage`, per the level definitions — `write` is for the
        // contents.
        await permissions.RequireManageAsync(currentUser.Subject, path, cancellationToken);

        await pipeline.DeleteFolderAsync(path, currentUser.UserId, cancellationToken);
        return Unit.Value;
    }
}
