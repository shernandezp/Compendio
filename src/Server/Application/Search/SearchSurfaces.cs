using Common.Mediator;
using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Domain.Content;
using Compendio.Domain.Security;

namespace Compendio.Application.Search;

// Every query in this file is a search index in disguise, and every one of them has leaked in some
// shipped wiki. They live together so the shared predicate is visible in one screen: readable
// folders from the evaluator, admin bypass, nothing post-filtered. Adding a surface here means
// adding a row to the leak suite.

/// <summary>Quick switcher and <c>[[link]]</c> autocomplete.</summary>
public sealed record SuggestQuery(string Query, int Limit = 10) : IQuery<IReadOnlyList<SearchHitDto>>;

public sealed class SuggestHandler(
    ISearchIndex index,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser) : IRequestHandler<SuggestQuery, IReadOnlyList<SearchHitDto>>
{
    public async Task<IReadOnlyList<SearchHitDto>> Handle(SuggestQuery request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return [];
        }

        var subject = currentUser.Subject;
        var hits = await index.SuggestAsync(new SearchRequest
        {
            Query = request.Query,
            ReadableFolderPaths = await permissions.ReadableFolderPathsAsync(subject, cancellationToken),
            BypassFolderFilter = subject.Role == UserRole.Admin,
            PreferredLanguage = currentUser.Language,
            PageSize = Math.Clamp(request.Limit, 1, 25),
        }, cancellationToken);

        return hits.Select(SearchPagesHandler.Map).ToArray();
    }
}

/// <summary>Tag names and counts. Recomputed per user — a global count is a leak.</summary>
public sealed record GetTagsQuery : IQuery<IReadOnlyList<TagCountDto>>;

public sealed record TagCountDto(string Tag, int Count);

public sealed class GetTagsHandler(
    ISearchIndex index,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser) : IRequestHandler<GetTagsQuery, IReadOnlyList<TagCountDto>>
{
    public async Task<IReadOnlyList<TagCountDto>> Handle(GetTagsQuery request, CancellationToken cancellationToken = default)
    {
        var subject = currentUser.Subject;
        var counts = await index.TagCountsAsync(
            await permissions.ReadableFolderPathsAsync(subject, cancellationToken),
            subject.Role == UserRole.Admin,
            cancellationToken);

        return counts.Select(c => new TagCountDto(c.Tag, c.Count)).ToArray();
    }
}

/// <summary>Backlinks. A link from a page the reader cannot see does not exist to them.</summary>
public sealed record GetBacklinksQuery(string Path) : IQuery<IReadOnlyList<SearchHitDto>>;

public sealed class GetBacklinksHandler(
    ISearchIndex index,
    IPathPolicy paths,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser) : IRequestHandler<GetBacklinksQuery, IReadOnlyList<SearchHitDto>>
{
    public async Task<IReadOnlyList<SearchHitDto>> Handle(GetBacklinksQuery request, CancellationToken cancellationToken = default)
    {
        var path = paths.Require(request.Path, PathKind.Page);
        var subject = currentUser.Subject;

        // The target page itself must be readable, or the panel becomes a way to ask "does this
        // path exist" and get an answer.
        await permissions.RequireReadAsync(subject, path, cancellationToken);

        var hits = await index.BacklinksAsync(
            path,
            await permissions.ReadableFolderPathsAsync(subject, cancellationToken),
            subject.Role == UserRole.Admin,
            cancellationToken);

        return hits.Select(SearchPagesHandler.Map).ToArray();
    }
}

/// <summary>"Recently updated" for the home screen.</summary>
public sealed record RecentlyUpdatedQuery(int Limit = 10) : IQuery<IReadOnlyList<SearchHitDto>>;

public sealed class RecentlyUpdatedHandler(
    ISearchIndex index,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser) : IRequestHandler<RecentlyUpdatedQuery, IReadOnlyList<SearchHitDto>>
{
    public async Task<IReadOnlyList<SearchHitDto>> Handle(RecentlyUpdatedQuery request, CancellationToken cancellationToken = default)
    {
        var subject = currentUser.Subject;
        var hits = await index.RecentlyUpdatedAsync(
            await permissions.ReadableFolderPathsAsync(subject, cancellationToken),
            subject.Role == UserRole.Admin,
            Math.Clamp(request.Limit, 1, 50),
            cancellationToken);

        return hits.Select(SearchPagesHandler.Map).ToArray();
    }
}
