using Common.Mediator;
using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Domain;
using Compendio.Domain.Content;
using Compendio.Domain.Entities;
using Compendio.Hosting.Configuration;
using Compendio.Infrastructure.Content;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Compendio.Application.Attachments;

/// <param name="Bytes">The whole upload. Attachments are capped, so buffering is bounded.</param>
public sealed record UploadAttachmentCommand(string PagePath, string FileName, byte[] Bytes) : ICommand<AttachmentDto>;

/// <summary>
/// Stores an attachment beside its page.
/// </summary>
/// <remarks>
/// Extension allowlist <em>and</em> content-type sniffing, both checked. An allowlist alone accepts
/// a renamed executable; sniffing alone accepts a real PNG named <c>.html</c>, which a browser will
/// treat as a document. And nothing here is ever served from a static file provider — the download
/// endpoint is authorized like every other read path.
/// </remarks>
public sealed class UploadAttachmentHandler(
    ICompendioDbContext db,
    IContentStore store,
    IPathPolicy paths,
    IPermissionEvaluator permissions,
    ISecureScopeRegistry secureScopes,
    ICurrentUser currentUser,
    IClock clock,
    IOptions<CompendioOptions> options) : IRequestHandler<UploadAttachmentCommand, AttachmentDto>
{
    private readonly AttachmentOptions _attachments = options.Value.Attachments;

    public async Task<AttachmentDto> Handle(UploadAttachmentCommand request, CancellationToken cancellationToken = default)
    {
        var pagePath = paths.Require(request.PagePath, PathKind.Page);
        await permissions.RequireWriteAsync(currentUser.Subject, pagePath, cancellationToken);

        var page = await db.Pages.FirstOrDefaultAsync(p => p.Path == pagePath.Value, cancellationToken)
                   ?? throw CompendioException.NotFound(pagePath);

        if (request.Bytes.LongLength > _attachments.MaxSizeBytes)
        {
            throw CompendioException.BadRequest(ProblemCodes.AttachmentTooLarge, Describe(_attachments.MaxSizeBytes));
        }

        var count = await db.Attachments.CountAsync(a => a.PageId == page.Id, cancellationToken);
        if (count >= _attachments.MaxPerPage)
        {
            throw CompendioException.BadRequest(ProblemCodes.AttachmentLimitReached, _attachments.MaxPerPage);
        }

        var safeName = Slug.Disambiguate(
            SafeFileName(request.FileName),
            candidate => store.Exists(pagePath.Parent.Append(CompendioConstants.AssetsFolderName).Append(candidate)));

        var extension = Path.GetExtension(safeName).ToLowerInvariant();

        if (!_attachments.AllowedTypes.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            throw CompendioException.BadRequest(ProblemCodes.AttachmentTypeNotAllowed, extension);
        }

        if (!MimeTypes.MatchesExtension(extension, request.Bytes))
        {
            throw CompendioException.BadRequest(ProblemCodes.AttachmentTypeNotAllowed, extension);
        }

        // SVG is an image format that browsers execute. It is the one that needs its contents read.
        if (extension == ".svg" && !MimeTypes.IsSafeSvg(request.Bytes))
        {
            throw CompendioException.BadRequest(ProblemCodes.AttachmentTypeNotAllowed, extension);
        }

        var assets = pagePath.Parent.Append(CompendioConstants.AssetsFolderName);
        var target = paths.Require(assets.Append(safeName).Value, PathKind.Attachment);

        await store.CreateFolderAsync(assets, cancellationToken);
        await store.WriteAsync(target, request.Bytes, expectedHash: null, cancellationToken);

        var attachment = new Attachment
        {
            Id = Guid.CreateVersion7(),
            PageId = page.Id,
            Path = target.Value,
            ContentType = MimeTypes.ForExtension(extension),
            ByteSize = request.Bytes.LongLength,
            IsSecure = await secureScopes.IsSecureAsync(target, cancellationToken),
            CreatedAt = clock.UtcNow,
        };

        db.Attachments.Add(attachment);
        await db.SaveChangesAsync(cancellationToken);

        return new AttachmentDto(attachment.Path, safeName, attachment.ContentType, attachment.ByteSize, attachment.CreatedAt);
    }

    /// <summary>
    /// Slugifies the stem but keeps the extension, so <c>Informe Anual.PDF</c> becomes
    /// <c>informe-anual.pdf</c> and stays openable.
    /// </summary>
    private static string SafeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        var extension = Path.GetExtension(name).ToLowerInvariant();
        var stem = Path.GetFileNameWithoutExtension(name);
        return Slug.Create(stem) + extension;
    }

    private static string Describe(long bytes) => $"{bytes / (1024 * 1024)} MB";
}

public sealed record DeleteAttachmentCommand(string Path) : ICommand<Unit>;

public sealed class DeleteAttachmentHandler(
    ICompendioDbContext db,
    IContentStore store,
    IPathPolicy paths,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser) : IRequestHandler<DeleteAttachmentCommand, Unit>
{
    public async Task<Unit> Handle(DeleteAttachmentCommand request, CancellationToken cancellationToken = default)
    {
        var path = paths.Require(request.Path, PathKind.Attachment);
        await permissions.RequireWriteAsync(currentUser.Subject, path, cancellationToken);

        await store.DeleteAsync(path, cancellationToken);
        await db.Attachments.Where(a => a.Path == path.Value).ExecuteDeleteAsync(cancellationToken);

        return Unit.Value;
    }
}

public sealed record GetAttachmentQuery(string Path) : IQuery<AttachmentContent>;

/// <param name="Inline">
/// Images render inline; everything else is <c>Content-Disposition: attachment</c>, so an uploaded
/// HTML file cannot execute in the wiki's own origin.
/// </param>
public sealed record AttachmentContent(byte[] Bytes, string ContentType, string FileName, bool Inline, bool IsSecure);

public sealed class GetAttachmentHandler(
    IContentStore store,
    IPathPolicy paths,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser) : IRequestHandler<GetAttachmentQuery, AttachmentContent>
{
    public async Task<AttachmentContent> Handle(GetAttachmentQuery request, CancellationToken cancellationToken = default)
    {
        var path = paths.Require(request.Path, PathKind.Attachment);

        // The authorized endpoint that replaces static file serving. A static file provider over
        // the content folder would bypass this entire layer in one line of somebody's future PR,
        // which is why there is also a startup assertion that none is mapped.
        await permissions.RequireReadAsync(currentUser.Subject, path, cancellationToken);

        var file = await store.ReadAsync(path, cancellationToken) ?? throw CompendioException.NotFound(path);
        var extension = path.Extension;

        return new AttachmentContent(
            file.Bytes,
            MimeTypes.ForExtension(extension),
            path.Name,
            MimeTypes.IsInlineImage(extension),
            file.WasEncrypted);
    }
}
