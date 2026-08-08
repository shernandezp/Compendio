using Compendio.Domain.Content;

namespace Compendio.Application.Abstractions;

public sealed record SearchHit(
    string Path,
    string Title,
    string Excerpt,
    string? Lang,
    IReadOnlyList<string> Tags,
    DateTimeOffset UpdatedAt,
    double Rank);

public sealed record SearchResults(IReadOnlyList<SearchHit> Items, int TotalCount, int Page, int PageSize);

public sealed record SearchRequest
{
    public required string Query { get; init; }

    public required IReadOnlySet<string> ReadableFolderPaths { get; init; }

    /// <summary>Admins skip the folder predicate entirely.</summary>
    public bool BypassFolderFilter { get; init; }

    /// <summary>Ranks the user's language first without hiding the other one.</summary>
    public string? PreferredLanguage { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 20;

    /// <summary>Restricts to a path prefix. Backs <c>space:</c> and <c>in:</c>.</summary>
    public string? PathPrefix { get; init; }
}

public sealed record IndexStatus(bool IsReady, int QueueDepth, int PageCount, string State, int? PercentComplete);

/// <summary>
/// The FTS5 index.
/// </summary>
/// <remarks>
/// <para>
/// A cache, never a source of truth: <c>compendio reindex</c> rebuilds it from the content folder,
/// and deleting the tables costs a rebuild and nothing else.
/// </para>
/// <para>
/// The permission predicate is part of every query in here, never applied to the results
/// afterwards. Post-filtering breaks paging, leaks totals, and produces empty pages that themselves
/// prove hidden matches exist.
/// </para>
/// </remarks>
public interface ISearchIndex
{
    Task<SearchResults> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default);

    /// <summary>Quick switcher and <c>[[link]]</c> autocomplete. Same predicate as search.</summary>
    Task<IReadOnlyList<SearchHit>> SuggestAsync(SearchRequest request, CancellationToken cancellationToken = default);

    /// <summary>Tag names with per-user counts — recomputed per user, never cached globally.</summary>
    Task<IReadOnlyList<(string Tag, int Count)>> TagCountsAsync(
        IReadOnlySet<string> readableFolderPaths,
        bool bypassFolderFilter,
        CancellationToken cancellationToken = default);

    /// <summary>Pages linking to <paramref name="target"/>, filtered by the same predicate.</summary>
    Task<IReadOnlyList<SearchHit>> BacklinksAsync(
        ContentPath target,
        IReadOnlySet<string> readableFolderPaths,
        bool bypassFolderFilter,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SearchHit>> RecentlyUpdatedAsync(
        IReadOnlySet<string> readableFolderPaths,
        bool bypassFolderFilter,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Upserts one page's extracted text and its FTS rows, in one transaction.</summary>
    Task UpsertAsync(Guid pageId, CancellationToken cancellationToken = default);

    Task RemoveAsync(Guid pageId, CancellationToken cancellationToken = default);

    Task<IndexStatus> StatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Drops and rebuilds from the database's page rows, in batches.</summary>
    Task RebuildAsync(IProgress<int>? progress = null, bool dropSecure = false, CancellationToken cancellationToken = default);
}
