using Common.Mediator;
using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Domain.Security;

namespace Compendio.Application.Search;

public sealed record SearchPagesQuery(string Query, int Page = 1, int PageSize = 20, string? In = null)
    : IQuery<PagedResult<SearchHitDto>>;

/// <summary>
/// The search endpoint, and the pattern every other search surface follows.
/// </summary>
/// <remarks>
/// The readable-folder set is computed here and handed to the index, which puts it in the SQL.
/// Counts use the same predicate, so "12 results" means twelve results <em>you can see</em>.
/// </remarks>
public sealed class SearchPagesHandler(
    ISearchIndex index,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser) : IRequestHandler<SearchPagesQuery, PagedResult<SearchHitDto>>
{
    public async Task<PagedResult<SearchHitDto>> Handle(SearchPagesQuery request, CancellationToken cancellationToken = default)
    {
        var pageSize = Math.Clamp(request.PageSize, 1, 100);
        var page = Math.Max(request.Page, 1);

        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return PagedResult<SearchHitDto>.Empty(page, pageSize);
        }

        var subject = currentUser.Subject;
        var results = await index.SearchAsync(new SearchRequest
        {
            Query = request.Query,
            ReadableFolderPaths = await permissions.ReadableFolderPathsAsync(subject, cancellationToken),
            BypassFolderFilter = subject.Role == UserRole.Admin,
            PreferredLanguage = currentUser.Language,
            Page = page,
            PageSize = pageSize,
            PathPrefix = request.In,
        }, cancellationToken);

        return new PagedResult<SearchHitDto>(
            results.Items.Select(Map).ToArray(),
            results.TotalCount,
            results.Page,
            results.PageSize);
    }

    internal static SearchHitDto Map(SearchHit hit) =>
        new(hit.Path, hit.Title, hit.Excerpt, hit.Lang, hit.Tags, hit.UpdatedAt);
}
