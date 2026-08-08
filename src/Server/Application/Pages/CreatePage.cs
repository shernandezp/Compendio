using System.Text;
using Common.Mediator;
using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Domain;
using Compendio.Domain.Content;
using Compendio.Domain.Entities;
using Compendio.Domain.Localization;
using Compendio.Hosting.Configuration;
using Microsoft.Extensions.Options;

namespace Compendio.Application.Pages;

/// <param name="FolderPath">Where it goes. The empty string is the root.</param>
/// <param name="Content">Canonical Markdown from the editor, or null to start from a template.</param>
public sealed record CreatePageCommand(
    string FolderPath,
    string Title,
    string? Content = null,
    string? TemplateId = null,
    string? Lang = null,
    string? TranslationKey = null) : ICommand<PageDto>;

public sealed class CreatePageHandler(
    IContentPipeline pipeline,
    IContentStore store,
    IPathPolicy paths,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser,
    PageProjection projection,
    IOptions<CompendioOptions> options) : IRequestHandler<CreatePageCommand, PageDto>
{
    public async Task<PageDto> Handle(CreatePageCommand request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            throw new ValidationException(new Dictionary<string, string[]> { ["title"] = ["required"] });
        }

        var folder = paths.Require(request.FolderPath, PathKind.Folder);
        await permissions.RequireWriteAsync(currentUser.Subject, folder, cancellationToken);

        // The accented title lives in front matter; the file name is ASCII-slugified so the content
        // survives an SMB share, a zip round-trip and a git client on another platform.
        var fileName = Slug.Disambiguate(
            Slug.CreateFileName(request.Title),
            candidate => store.Exists(folder.Append(candidate)));

        var path = paths.Require(folder.Append(fileName).Value, PathKind.Page);

        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(BuildContent(request));

        var page = await pipeline.SavePageAsync(path, bytes, expectedHash: null, currentUser.UserId,
            VersionSource.Editor, note: null, cancellationToken);

        var file = await store.ReadAsync(path, cancellationToken);
        return await projection.BuildAsync(page, file, includeContent: true, includeHtml: true, cancellationToken);
    }

    /// <summary>
    /// The new page's bytes: the caller's body if there is one, always under front matter that
    /// carries the title.
    /// </summary>
    /// <remarks>
    /// The accented title lives in front matter — that is the whole reason the file name is allowed
    /// to be an ASCII slug. The editor sends canonical body text and the title separately, so
    /// without this a page created as "Política de teletrabajo" would be stored with no
    /// <c>title:</c> key at all and read back as "Politica De Teletrabajo", reconstructed from its
    /// own file name.
    /// <para>
    /// Front matter the caller already supplied wins, and any keys it carries are preserved: this
    /// fills a gap, it does not overwrite an answer.
    /// </para>
    /// </remarks>
    private string BuildContent(CreatePageCommand request)
    {
        var document = MarkdownParser.Parse(request.Content ?? string.Empty);

        if (!string.IsNullOrWhiteSpace(document.FrontMatter.Title))
        {
            return request.Content!;
        }

        var frontMatter = document.FrontMatter with
        {
            Title = request.Title,
            Lang = request.Lang ?? document.FrontMatter.Lang ?? SupportedLanguages.ResolveOrFallback(
                currentUser.Language, options.Value.Instance.DefaultLanguage),
            TranslationKey = request.TranslationKey ?? document.FrontMatter.TranslationKey,
        };

        return MarkdownParser.Compose(frontMatter, document.Body, document.LineEnding);
    }
}
