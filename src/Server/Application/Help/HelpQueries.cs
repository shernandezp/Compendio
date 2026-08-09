using Common.Mediator;
using Compendio.Application.Abstractions;
using Compendio.Domain.Content;
using Compendio.Domain.Security;

namespace Compendio.Application.Help;

/// <param name="IsAdmin">Drives the section heading the client renders, not the filtering.</param>
public sealed record HelpTopicDto(string Slug, string Title, bool IsAdmin, bool IsFallback);

/// <param name="Html">Sanitized by the same renderer the wiki pages use.</param>
public sealed record HelpPageDto(
    string Slug,
    string Title,
    string Html,
    string Language,
    bool IsFallback,
    bool IsAdmin,
    IReadOnlyList<HeadingAnchor> Headings);

public sealed record GetHelpTopicsQuery : IQuery<IReadOnlyList<HelpTopicDto>>;

public sealed record GetHelpPageQuery(string Slug) : IQuery<HelpPageDto?>;

public sealed class GetHelpTopicsHandler(IHelpLibrary library, ICurrentUser currentUser)
    : IRequestHandler<GetHelpTopicsQuery, IReadOnlyList<HelpTopicDto>>
{
    public Task<IReadOnlyList<HelpTopicDto>> Handle(
        GetHelpTopicsQuery request,
        CancellationToken cancellationToken = default)
    {
        var isAdmin = currentUser.Role == UserRole.Admin;

        IReadOnlyList<HelpTopicDto> topics = library.List(currentUser.Language)
            .Select(document => (Document: document, Topic: HelpCatalog.Find(document.Slug)!))
            .Where(x => isAdmin || x.Topic.Audience == HelpAudience.Everyone)
            .Select(x => new HelpTopicDto(
                x.Document.Slug,
                x.Document.Title,
                x.Topic.Audience == HelpAudience.Admin,
                x.Document.IsFallback))
            .ToArray();

        return Task.FromResult(topics);
    }
}

/// <summary>
/// One help topic, rendered.
/// </summary>
/// <remarks>
/// An administrator topic requested by a non-administrator returns null, and the endpoint turns
/// that into the same 404 as an unknown slug. The content is not confidential — it describes
/// features rather than data — but a reader who follows a stale link should get "no such topic"
/// rather than a page of instructions for a screen they cannot open.
/// </remarks>
public sealed class GetHelpPageHandler(
    IHelpLibrary library,
    IMarkdownRenderer renderer,
    ICurrentUser currentUser) : IRequestHandler<GetHelpPageQuery, HelpPageDto?>
{
    public Task<HelpPageDto?> Handle(GetHelpPageQuery request, CancellationToken cancellationToken = default)
    {
        var topic = HelpCatalog.Find(request.Slug);
        if (topic is null)
        {
            return Task.FromResult<HelpPageDto?>(null);
        }

        if (topic.Audience == HelpAudience.Admin && currentUser.Role != UserRole.Admin)
        {
            return Task.FromResult<HelpPageDto?>(null);
        }

        var document = library.Find(topic.Slug, currentUser.Language);
        if (document is null)
        {
            return Task.FromResult<HelpPageDto?>(null);
        }

        // The guide is ours, so there are no [[wiki links]] to resolve — an unresolved target is a
        // typo in our own text, not a page the reader cannot see.
        var rendered = renderer.Render(
            document.Markdown,
            ContentPath.FromTrusted($"help/{document.Slug}.md"),
            _ => null);

        return Task.FromResult<HelpPageDto?>(new HelpPageDto(
            document.Slug,
            document.Title,
            rendered.Html,
            document.Language,
            document.IsFallback,
            topic.Audience == HelpAudience.Admin,
            rendered.Headings));
    }
}
