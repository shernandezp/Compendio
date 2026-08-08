using System.Text;
using Common.Mediator;
using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Domain.Content;
using Compendio.Domain.Entities;

namespace Compendio.Application.Pages;

/// <param name="ExpectedHash">
/// The hash the caller last read. Required, and a mismatch is a <c>409</c> carrying both versions —
/// never a silent overwrite.
/// </param>
/// <param name="Normalized">
/// Set by the editor when this save is the one-time rewrite to canonical Markdown. It makes the
/// resulting noisy diff attributable and explains itself in history, instead of looking like
/// somebody reformatted the whole page for no reason.
/// </param>
/// <param name="MaterialRevision">
/// The editor's explicit answer to "does everyone need to read this again?", and the only thing that
/// re-opens an acknowledgment. Default off, so an ordinary edit changes nothing.
/// </param>
/// <remarks>
/// A heuristic over the diff was the alternative and was rejected: re-asking two hundred people to
/// re-read a typo fix is how the acknowledgment feature gets switched off, and the author already
/// knows which kind of change they just made.
/// </remarks>
public sealed record SavePageCommand(
    string Path,
    string Content,
    string ExpectedHash,
    bool Normalized = false,
    string? Note = null,
    bool MaterialRevision = false) : ICommand<PageDto>;

public sealed class SavePageValidator
{
    public static void Validate(SavePageCommand command)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(command.Path))
        {
            errors["path"] = ["required"];
        }

        if (command.Content is null)
        {
            errors["content"] = ["required"];
        }

        if (string.IsNullOrWhiteSpace(command.ExpectedHash))
        {
            errors["expectedHash"] = ["required"];
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }
    }
}

public sealed class SavePageHandler(
    IContentPipeline pipeline,
    IPathPolicy paths,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser,
    IContentStore store,
    Acknowledgments.AcknowledgmentRounds acknowledgmentRounds,
    PageProjection projection) : IRequestHandler<SavePageCommand, PageDto>
{
    /// <summary>The front-matter key an editor save always removes.</summary>
    internal const string MachineTranslationKey = "machineTranslated";


    public async Task<PageDto> Handle(SavePageCommand request, CancellationToken cancellationToken = default)
    {
        SavePageValidator.Validate(request);

        var path = paths.Require(request.Path, PathKind.Page);
        await permissions.RequireWriteAsync(currentUser.Subject, path, cancellationToken);

        // A human saving a machine-translated page is what clears its "unreviewed" badge, and the
        // server enforces that rather than trusting the client to have dropped the key. A line
        // removal, not a re-emit: re-serializing the block would replace remark's formatting with
        // ours and produce a diff across front matter the user had just written in canonical form.
        var content = FrontMatterLines.RemoveKey(request.Content, MachineTranslationKey);

        // UTF-8 without a BOM. remark in the client is the only Markdown serializer in the product;
        // the server stores the bytes it is handed after validating front matter, size and path.
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);

        var page = await pipeline.SavePageAsync(
            path, bytes, request.ExpectedHash, currentUser.UserId,
            request.Normalized ? VersionSource.Normalization : VersionSource.Editor,
            request.Note, cancellationToken);

        // After the version exists, because a round has to point at one. This is the only caller
        // that knows whether the editor called the change material.
        await acknowledgmentRounds.SynchronizeAsync(page, request.MaterialRevision, currentUser.UserId, cancellationToken);

        var file = await store.ReadAsync(path, cancellationToken);
        return await projection.BuildAsync(page, file, includeContent: true, includeHtml: true, cancellationToken);
    }
}
