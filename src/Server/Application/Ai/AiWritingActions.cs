using Common.Mediator;
using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Domain.Content;
using Compendio.Hosting.Configuration;
using Microsoft.Extensions.Options;

namespace Compendio.Application.Ai;

// The four actions that transform text the caller already has. They share a shape — guard, read,
// prompt, return a proposal — so they share a file; splitting four near-identical twenty-line
// handlers across four files would hide the fact that they are the same thing four times.
//
// None of them writes to disk. Every result comes back as a proposal the user accepts or discards
// in the editor, because content the user has not seen must never become a saved page.

/// <param name="Text">
/// A selection, when the user has one. Absent, the whole page is used — which is why the path is
/// still required.
/// </param>
public sealed record ImproveWritingCommand(string Path, string? Text) : ICommand<AiProposalDto>;

/// <param name="Proposal">Markdown to show the user. Never written anywhere by this handler.</param>
public sealed record AiProposalDto(string Proposal, string Model, string EndpointLabel);

public sealed class ImproveWritingHandler(AiTextActions actions) : IRequestHandler<ImproveWritingCommand, AiProposalDto>
{
    public Task<AiProposalDto> Handle(ImproveWritingCommand request, CancellationToken cancellationToken = default) =>
        actions.RunAsync(AiFeatures.Improve, request.Path, request.Text, PromptTemplates.Improve, cancellationToken);
}

public sealed record SummarizePageCommand(string Path, string? Text) : ICommand<AiProposalDto>;

public sealed class SummarizePageHandler(AiTextActions actions) : IRequestHandler<SummarizePageCommand, AiProposalDto>
{
    public Task<AiProposalDto> Handle(SummarizePageCommand request, CancellationToken cancellationToken = default) =>
        actions.RunAsync(AiFeatures.Summarize, request.Path, request.Text, PromptTemplates.Summarize, cancellationToken);
}

public sealed record FreshnessHintsCommand(string Path) : ICommand<AiProposalDto>;

public sealed class FreshnessHintsHandler(AiTextActions actions, IClock clock)
    : IRequestHandler<FreshnessHintsCommand, AiProposalDto>
{
    public Task<AiProposalDto> Handle(FreshnessHintsCommand request, CancellationToken cancellationToken = default) =>
        actions.RunAsync(AiFeatures.Freshness, request.Path, selection: null,
            body => PromptTemplates.Freshness(body, clock.UtcNow), cancellationToken);
}

/// <param name="TemplateId">Optional page template whose headings the draft should follow.</param>
public sealed record DraftFromBulletsCommand(string FolderPath, string Bullets, string? TemplateId) : ICommand<AiProposalDto>;

public sealed class DraftFromBulletsHandler(
    AiGuard guard,
    IAiProvider provider,
    IPathPolicy paths,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser,
    ISender sender,
    IOptions<CompendioOptions> options) : IRequestHandler<DraftFromBulletsCommand, AiProposalDto>
{
    public async Task<AiProposalDto> Handle(DraftFromBulletsCommand request, CancellationToken cancellationToken = default)
    {
        var configuration = await guard.RequireEnabledAsync(AiFeatures.Draft, cancellationToken);

        // Drafting produces content for a folder rather than from a page, so the check is write
        // access there — the user is about to create something.
        var folder = paths.Require(request.FolderPath, PathKind.Folder);
        await permissions.RequireWriteAsync(currentUser.Subject, folder, cancellationToken);

        if (!await guard.IsContentAllowedAsync(configuration, folder, cancellationToken))
        {
            throw new CompendioException(ProblemCodes.AiNotAllowedHere, StatusCodes.Status403Forbidden, folder.Value);
        }

        // Reuses the same catalogue the editor's template picker reads, bundled entries and
        // `_templates/` overrides alike — so a draft follows the organization's own SOP shape rather
        // than a second, hard-coded idea of one.
        var template = request.TemplateId is { Length: > 0 } id
            ? (await sender.Send(new Admin.GetTemplatesQuery(), cancellationToken))
                .FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))?.Content
            : null;

        var bullets = Truncate(request.Bullets, options.Value.Ai.MaxInputCharacters);

        // Charged here, not at the top: a draft refused for want of write access has cost nothing.
        await guard.ChargeAsync(configuration, AiFeatures.Draft, bullets.Length, cancellationToken);

        var completion = await provider.CompleteAsync(PromptTemplates.Draft(bullets, template), cancellationToken);

        return new AiProposalDto(completion.Text, completion.Model, configuration.EndpointLabel);
    }

    internal static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}

/// <summary>
/// The shared body of the actions that read one page and propose a rewrite of it.
/// </summary>
/// <remarks>
/// Exists so the guard call, the read, the truncation and the "never write to disk" rule are stated
/// once. A handler that skipped the guard would be a permissions bypass, and the cheapest way to
/// stop one being written is to leave nothing for it to skip.
/// </remarks>
public sealed class AiTextActions(
    AiGuard guard,
    IAiProvider provider,
    IContentStore store,
    IPathPolicy paths,
    IOptions<CompendioOptions> options)
{
    public async Task<AiProposalDto> RunAsync(
        string feature,
        string path,
        string? selection,
        Func<string, AiPrompt> buildPrompt,
        CancellationToken cancellationToken)
    {
        var configuration = await guard.RequireEnabledAsync(feature, cancellationToken);

        var content = paths.Require(path, PathKind.Page);
        await guard.RequireContentAllowedAsync(configuration, content, cancellationToken);

        var text = selection;

        if (string.IsNullOrWhiteSpace(text))
        {
            var file = await store.ReadAsync(content, cancellationToken) ?? throw CompendioException.NotFound(content);

            // The body only. Front matter is metadata, and a model rewriting a translationKey or a
            // review date would corrupt the lifecycle features silently.
            text = MarkdownParser.Parse(file.Text).Body;
        }

        var material = DraftFromBulletsHandler.Truncate(text, options.Value.Ai.MaxInputCharacters);

        // The last thing before the request leaves. Everything above can still refuse for free.
        await guard.ChargeAsync(configuration, feature, material.Length, cancellationToken);

        var prompt = buildPrompt(material);
        var completion = await provider.CompleteAsync(prompt, cancellationToken);

        return new AiProposalDto(completion.Text, completion.Model, configuration.EndpointLabel);
    }
}
