using Common.Mediator;
using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Domain.Content;
using Compendio.Domain.Entities;
using Compendio.Application.Pages;
using Compendio.Domain;
using Compendio.Domain.Localization;
using Compendio.Hosting.Configuration;
using Microsoft.Extensions.Options;

namespace Compendio.Application.Ai;

/// <summary>
/// Creates the sibling-language page, badged as machine-translated until a human has read it.
/// </summary>
/// <remarks>
/// <para>
/// The one AI action that writes a file, because the deliverable <em>is</em> a file — a proposal the
/// user would have to paste into a new page by hand is not the feature. What makes it safe is the
/// badge: <c>machineTranslated: true</c> in front matter, rendered as a visible "unreviewed" banner,
/// cleared only when a person saves the page from the editor.
/// </para>
/// <para>
/// A wrong Spanish HR policy is worse than no Spanish HR policy, so the badge is in front matter
/// rather than a database column: it survives export, a git mirror, and somebody copying the file.
/// </para>
/// </remarks>
public sealed record TranslatePageCommand(string Path, string TargetLanguage) : ICommand<PageDto>;

public sealed class TranslatePageHandler(
    AiGuard guard,
    IAiProvider provider,
    IContentStore store,
    IContentPipeline pipeline,
    IPathPolicy paths,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser,
    PageProjection projection,
    IOptions<CompendioOptions> options) : IRequestHandler<TranslatePageCommand, PageDto>
{
    public async Task<PageDto> Handle(TranslatePageCommand request, CancellationToken cancellationToken = default)
    {
        var configuration = await guard.RequireEnabledAsync(AiFeatures.Translate, cancellationToken);

        var target = SupportedLanguages.Normalize(request.TargetLanguage);
        if (target is null)
        {
            throw new ValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["targetLanguage"] = ["unsupported"],
            });
        }

        var source = paths.Require(request.Path, PathKind.Page);
        await guard.RequireContentAllowedAsync(configuration, source, cancellationToken);

        var file = await store.ReadAsync(source, cancellationToken) ?? throw CompendioException.NotFound(source);
        var document = MarkdownParser.Parse(file.Text);

        var destination = SiblingPath(source, target);

        // Writing the sibling is a create, so it needs write access where it lands — which may be a
        // different answer from read access on the source.
        await permissions.RequireWriteAsync(currentUser.Subject, destination, cancellationToken);

        if (store.Exists(destination))
        {
            throw CompendioException.Exists(destination);
        }

        var body = document.Body.Length <= options.Value.Ai.MaxInputCharacters
            ? document.Body
            : document.Body[..options.Value.Ai.MaxInputCharacters];

        // After the destination checks: a translation refused because the sibling already exists has
        // not reached the provider, so it must not spend anybody's budget.
        await guard.ChargeAsync(configuration, AiFeatures.Translate, body.Length, cancellationToken);

        var completion = await provider.CompleteAsync(
            PromptTemplates.Translate(body, SupportedLanguages.EnglishNameOf(target)), cancellationToken);

        var frontMatter = document.FrontMatter with
        {
            Lang = target,
            // Authoritative link back to the source, so the language switcher pairs them without
            // depending on the file-name convention.
            TranslationKey = document.FrontMatter.TranslationKey ?? source.Value,
            TranslationOf = source.Value,
            MachineTranslated = true,
            // Not inherited: a machine translation nobody has read must not be the thing two hundred
            // people are asked to acknowledge.
            RequiresAcknowledgment = null,
        };

        var composed = MarkdownParser.Compose(frontMatter, completion.Text, document.LineEnding);
        var bytes = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(composed);

        var page = await pipeline.SavePageAsync(
            destination, bytes, expectedHash: null, currentUser.UserId,
            VersionSource.Editor, note: $"ai.translate:{target}", cancellationToken);

        var written = await store.ReadAsync(destination, cancellationToken);
        return await projection.BuildAsync(page, written, includeContent: true, includeHtml: true, cancellationToken);
    }

    /// <summary>
    /// <c>IT/vpn.md</c> plus <c>es</c> becomes <c>IT/vpn.es.md</c>.
    /// </summary>
    /// <remarks>
    /// The sibling convention v0 already recognizes, so the pair is discoverable from the file names
    /// alone even by someone reading the folder in VS Code.
    /// </remarks>
    private static ContentPath SiblingPath(ContentPath source, string language)
    {
        var stem = source.NameWithoutExtension;

        // A source that is itself a translation (`vpn.en.md`) contributes its base name, not its
        // suffix — otherwise translating twice produces `vpn.en.es.md`.
        var dot = stem.LastIndexOf('.');
        if (dot > 0 && SupportedLanguages.IsSupported(stem[(dot + 1)..]))
        {
            stem = stem[..dot];
        }

        return source.Parent.Append($"{stem}.{language}{CompendioConstants.MarkdownExtension}");
    }
}
