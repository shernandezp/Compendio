using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Compendio.Application.Abstractions;
using Compendio.Domain;
using Compendio.Domain.Content;
using Compendio.Domain.Entities;
using Compendio.Hosting.Configuration;
using Compendio.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Compendio.Infrastructure.Search;

/// <summary>
/// FTS5 search, permission-filtered in the SQL.
/// </summary>
/// <remarks>
/// <para>
/// Every read surface in the product routes through this class, and every query it builds carries
/// the same folder predicate. That is deliberate: the quick switcher, link autocomplete, backlinks,
/// tag counts and "recently updated" are all search indexes in disguise, and every one of them has
/// leaked in some shipped wiki.
/// </para>
/// <para>
/// The predicate is a <c>json_each</c> join over the readable-folder list rather than an
/// interpolated <c>IN (…)</c>. One parameter, no SQL-length cliff, and the planner gets a real
/// table to work with.
/// </para>
/// </remarks>
public sealed class SearchIndex(
    IDbContextFactory<CompendioDbContext> dbFactory,
    IContentStore contentStore,
    ITextExtractor extractor,
    IOptions<CompendioOptions> options,
    IClock clock,
    ILogger<SearchIndex> logger) : ISearchIndex
{
    /// <summary>
    /// Sentinels the snippet is built with, so the excerpt can be HTML-escaped <em>after</em>
    /// SQLite has marked it up. <c>snippet()</c> returns page content, and page content contains
    /// <c>&lt;script&gt;</c> sometimes.
    /// </summary>
    private const string MarkOpen = "";
    private const string MarkClose = "";

    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    private readonly SearchOptions _search = options.Value.Search;

    public async Task<SearchResults> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        var parsed = new SearchQueryParser(_search.Synonyms).Parse(request.Query);

        if (parsed.IsEmpty)
        {
            return new SearchResults([], 0, request.Page, request.PageSize);
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenAsync(db, cancellationToken);

        var where = new StringBuilder();
        var parameters = new List<SqliteParameter>();
        BuildPredicate(where, parameters, parsed, request.ReadableFolderPaths, request.BypassFolderFilter, request.PathPrefix);

        var from = parsed.HasText
            ? $"""
               FROM {SearchSchema.Table}
               JOIN PageText t ON t.RowId = {SearchSchema.Table}.rowid
               JOIN Pages p ON p.Id = t.PageId
               JOIN Folders f ON f.Id = p.FolderId
               """
            : """
              FROM Pages p
              JOIN Folders f ON f.Id = p.FolderId
              LEFT JOIN PageText t ON t.PageId = p.Id
              """;

        var total = await ScalarIntAsync(connection, $"SELECT COUNT(*) {from} {where}", parameters, cancellationToken);

        var excerpt = parsed.HasText
            ? $"snippet({SearchSchema.Table}, 2, '{MarkOpen}', '{MarkClose}', '…', 12)"
            : "substr(COALESCE(t.Body, ''), 1, 200)";

        var order = parsed.HasText ? "Score ASC" : "p.UpdatedAt DESC";

        var sql = $"""
                   SELECT p.Path, p.Title, {excerpt} AS Excerpt, p.Lang, p.Tags, p.UpdatedAt, {ScoreExpression(parsed)} AS Score
                   {from}
                   {where}
                   ORDER BY {order}
                   LIMIT $limit OFFSET $offset
                   """;

        if (parsed.HasText)
        {
            AddRankingParameters(parameters, request.PreferredLanguage);
        }

        parameters.Add(new SqliteParameter("$limit", request.PageSize));
        parameters.Add(new SqliteParameter("$offset", (request.Page - 1) * request.PageSize));

        var items = await ReadHitsAsync(connection, sql, parameters, cancellationToken);
        return new SearchResults(items, total, request.Page, request.PageSize);
    }

    public async Task<IReadOnlyList<SearchHit>> SuggestAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        var results = await SearchAsync(request with { Page = 1, PageSize = Math.Min(request.PageSize, 20) }, cancellationToken);
        return results.Items;
    }

    /// <summary>
    /// Counts are recomputed per user rather than cached globally: a globally cached tag count tells
    /// a reader how many pages exist behind a folder they cannot open.
    /// </summary>
    public async Task<IReadOnlyList<(string Tag, int Count)>> TagCountsAsync(
        IReadOnlySet<string> readableFolderPaths,
        bool bypassFolderFilter,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenAsync(db, cancellationToken);

        var where = new StringBuilder();
        var parameters = new List<SqliteParameter>();
        BuildPredicate(where, parameters, new ParsedQuery(), readableFolderPaths, bypassFolderFilter, pathPrefix: null);

        var sql = $"""
                   SELECT p.Tags
                   FROM Pages p
                   JOIN Folders f ON f.Id = p.FolderId
                   {where}
                     AND p.Tags <> ''
                   """;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters.ToArray());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            foreach (var tag in reader.GetString(0).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                counts[tag] = counts.GetValueOrDefault(tag) + 1;
            }
        }

        return counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => (kv.Key, kv.Value))
            .ToArray();
    }

    /// <summary>A backlink from a page the reader cannot see is invisible, like the page itself.</summary>
    public async Task<IReadOnlyList<SearchHit>> BacklinksAsync(
        ContentPath target,
        IReadOnlySet<string> readableFolderPaths,
        bool bypassFolderFilter,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenAsync(db, cancellationToken);

        var where = new StringBuilder();
        var parameters = new List<SqliteParameter>();
        BuildPredicate(where, parameters, new ParsedQuery(), readableFolderPaths, bypassFolderFilter, pathPrefix: null);

        where.Append("""
                       AND EXISTS (
                           SELECT 1 FROM PageLinks l
                           WHERE l.PageId = p.Id
                             AND (l.Target = $target OR l.Target = $targetNoExt OR l.Target = $targetName)
                       )
                     """);

        parameters.Add(new SqliteParameter("$target", target.Value));
        parameters.Add(new SqliteParameter("$targetNoExt",
            target.Value.EndsWith(CompendioConstants.MarkdownExtension, StringComparison.OrdinalIgnoreCase)
                ? target.Value[..^CompendioConstants.MarkdownExtension.Length]
                : target.Value));
        parameters.Add(new SqliteParameter("$targetName", target.NameWithoutExtension));

        var sql = $"""
                   SELECT p.Path, p.Title, '' AS Excerpt, p.Lang, p.Tags, p.UpdatedAt, 0.0 AS Score
                   FROM Pages p
                   JOIN Folders f ON f.Id = p.FolderId
                   {where}
                   ORDER BY p.Title
                   LIMIT 200
                   """;

        return await ReadHitsAsync(connection, sql, parameters, cancellationToken);
    }

    public async Task<IReadOnlyList<SearchHit>> RecentlyUpdatedAsync(
        IReadOnlySet<string> readableFolderPaths,
        bool bypassFolderFilter,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenAsync(db, cancellationToken);

        var where = new StringBuilder();
        var parameters = new List<SqliteParameter>();
        BuildPredicate(where, parameters, new ParsedQuery(), readableFolderPaths, bypassFolderFilter, pathPrefix: null);
        parameters.Add(new SqliteParameter("$limit", limit));

        var sql = $"""
                   SELECT p.Path, p.Title, '' AS Excerpt, p.Lang, p.Tags, p.UpdatedAt, 0.0 AS Score
                   FROM Pages p
                   JOIN Folders f ON f.Id = p.FolderId
                   {where}
                   ORDER BY p.UpdatedAt DESC
                   LIMIT $limit
                   """;

        return await ReadHitsAsync(connection, sql, parameters, cancellationToken);
    }

    /// <summary>
    /// Re-extracts one page and writes its text, its FTS rows and its outbound links in one
    /// transaction. The FTS side is maintained by triggers on <c>PageText</c>.
    /// </summary>
    public async Task UpsertAsync(Guid pageId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var page = await db.Pages.Include(p => p.Text).FirstOrDefaultAsync(p => p.Id == pageId, cancellationToken);
        if (page is null)
        {
            return;
        }

        if (page.IsSecure && !await IsScopeIndexedAsync(db, page.Path, cancellationToken))
        {
            // Indexing a secure page would copy its plaintext into compendio.db. Excluded unless an
            // admin opted the scope in, having been told exactly that.
            await RemoveAsync(pageId, cancellationToken);
            return;
        }

        var path = ContentPath.FromTrusted(page.Path);
        var file = await contentStore.ReadAsync(path, cancellationToken);
        if (file is null)
        {
            await RemoveAsync(pageId, cancellationToken);
            return;
        }

        var extracted = extractor.Extract(file.Text, path);

        if (page.Text is null)
        {
            db.PageTexts.Add(new PageText
            {
                PageId = pageId,
                Title = extracted.Title,
                Headings = extracted.Headings,
                Body = extracted.Body,
                Tags = extracted.Tags,
                Path = extracted.Path,
            });
        }
        else
        {
            page.Text.Title = extracted.Title;
            page.Text.Headings = extracted.Headings;
            page.Text.Body = extracted.Body;
            page.Text.Tags = extracted.Tags;
            page.Text.Path = extracted.Path;
        }

        await db.SaveChangesAsync(cancellationToken);
        await ReplaceLinksAsync(db, pageId, extracted.OutboundLinks, cancellationToken);
    }

    public async Task RemoveAsync(Guid pageId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // The AFTER DELETE trigger removes the FTS rows; deleting the text is what actually
        // removes the copy of the content from the database.
        await db.PageTexts.Where(t => t.PageId == pageId).ExecuteDeleteAsync(cancellationToken);

        var connection = await OpenAsync(db, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM PageLinks WHERE PageId = $id";
        command.Parameters.Add(new SqliteParameter("$id", pageId.ToString()));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IndexStatus> StatusAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var queueDepth = await db.IndexQueue.CountAsync(cancellationToken);
        var pageCount = await db.Pages.CountAsync(cancellationToken);
        var indexed = await db.PageTexts.CountAsync(cancellationToken);

        // Readiness is measured against the pages that are *meant* to be indexed. A secure scope
        // that has not opted in deliberately has no PageText rows, so comparing against every page
        // would leave an instance with one encrypted folder reporting "stale" for ever — and
        // `/ready` is what a container healthcheck and a monitoring probe read.
        var expected = await db.Pages.CountAsync(p => !p.IsSecure, cancellationToken);

        var state = queueDepth > 0 ? "rebuilding" : indexed >= expected ? "ready" : "stale";
        var percent = expected == 0 ? 100 : (int)(Math.Min(indexed, expected) * 100L / expected);

        return new IndexStatus(state == "ready", queueDepth, pageCount, state, percent);
    }

    /// <summary>
    /// Rebuilds from the page rows and the files behind them.
    /// </summary>
    /// <remarks>
    /// Online and in batches, because on a real corpus this takes minutes and taking search offline
    /// for it would be worse than serving slightly stale results while it runs.
    /// </remarks>
    public async Task RebuildAsync(IProgress<int>? progress = null, bool dropSecure = false, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var connection = await OpenAsync(db, cancellationToken);

        await SearchSchema.EnsureAsync(connection, cancellationToken);

        if (dropSecure)
        {
            await SearchSchema.DropSecureAsync(connection, cancellationToken);
        }

        var ids = await db.Pages.Select(p => p.Id).ToListAsync(cancellationToken);
        logger.LogInformation("Rebuilding the search index over {Count} page(s).", ids.Count);

        for (var i = 0; i < ids.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await UpsertAsync(ids[i], cancellationToken);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Skipped a page during reindex: {Message}", e.Message);
            }

            if (progress is not null && ids.Count > 0 && (i % 25 == 0 || i == ids.Count - 1))
            {
                progress.Report((int)((i + 1) * 100L / ids.Count));
            }
        }

        await SearchSchema.RebuildAsync(connection, cancellationToken);
    }

    // ---- Query construction ---------------------------------------------------------------------

    /// <summary>
    /// The <c>WHERE</c> clause. Every surface uses this and nothing bypasses it.
    /// </summary>
    private void BuildPredicate(
        StringBuilder where,
        List<SqliteParameter> parameters,
        ParsedQuery parsed,
        IReadOnlySet<string> readableFolderPaths,
        bool bypassFolderFilter,
        string? pathPrefix)
    {
        where.Append("WHERE 1 = 1");

        if (parsed.HasText)
        {
            where.Append($" AND {SearchSchema.Table} MATCH $match");
            parameters.Add(new SqliteParameter("$match", parsed.Match));
        }

        // The permission predicate. Not applied afterwards, ever: post-filtering a result page
        // breaks paging, leaks totals, and produces empty pages that themselves prove that hidden
        // matches exist.
        if (bypassFolderFilter)
        {
            where.Append(" AND 1 = 1");
        }
        else
        {
            where.Append(" AND f.Path IN (SELECT value FROM json_each($readable))");
            parameters.Add(new SqliteParameter("$readable", JsonSerializer.Serialize(readableFolderPaths)));
        }

        // Secure scopes are excluded unless an admin opted the scope into indexing. The indexer
        // already refuses to write their text; this is the second lock on the same door.
        //
        // A scope covers itself *and everything below it*, so the test is a prefix test. Comparing
        // the page's own folder against the scope list would have hidden every page in a subfolder
        // of an opted-in scope — failing closed, but still failing.
        where.Append("""
                       AND (p.IsSecure = 0 OR EXISTS (
                               SELECT 1 FROM SecureScopes s
                               WHERE s.IndexContent = 1 AND s.RetiredAt IS NULL
                                 AND (f.Path = s.FolderPath OR f.Path LIKE s.FolderPath || '/%')))
                     """);

        var prefix = pathPrefix ?? parsed.PathPrefix;
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            where.Append(" AND (p.Path = $prefix OR p.Path LIKE $prefixLike ESCAPE '\\')");
            parameters.Add(new SqliteParameter("$prefix", prefix));
            parameters.Add(new SqliteParameter("$prefixLike", EscapeLike(prefix) + "/%"));
        }

        if (parsed.Tag is { Length: > 0 } tag)
        {
            where.Append(" AND (' ' || p.Tags || ' ') LIKE $tag ESCAPE '\\'");
            parameters.Add(new SqliteParameter("$tag", "% " + EscapeLike(tag) + " %"));
        }

        if (parsed.Owner is { Length: > 0 } owner)
        {
            where.Append(" AND p.Owner = $owner COLLATE NOCASE");
            parameters.Add(new SqliteParameter("$owner", owner));
        }

        if (parsed.Lang is { Length: > 0 } lang)
        {
            where.Append(" AND p.Lang = $langFilter COLLATE NOCASE");
            parameters.Add(new SqliteParameter("$langFilter", lang));
        }

        if (parsed.UpdatedAfter is { } after)
        {
            where.Append(" AND p.UpdatedAt > $after");
            parameters.Add(new SqliteParameter("$after", after.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture)));
        }

        if (parsed.UpdatedBefore is { } before)
        {
            where.Append(" AND p.UpdatedAt < $before");
            parameters.Add(new SqliteParameter("$before", before.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture)));
        }
    }

    /// <summary>
    /// BM25 with configurable column weights, divided by the boosts.
    /// </summary>
    /// <remarks>
    /// <c>bm25()</c> returns a negative score where more negative is better, so a boost is a
    /// division rather than a multiplication. The language boost is what puts the Spanish version of
    /// a bilingual pair first for a Spanish reader — without hiding the English one, which is the
    /// difference between ranking and filtering.
    /// </remarks>
    private string ScoreExpression(ParsedQuery parsed)
    {
        if (!parsed.HasText)
        {
            return "0.0";
        }

        return $"""
                bm25({SearchSchema.Table}, $wTitle, $wHeadings, $wBody, $wTags, $wPath)
                    / (CASE WHEN p.UpdatedAt >= $recencyCutoff THEN $recencyBoost ELSE 1.0 END)
                    / (CASE WHEN $preferredLang <> '' AND p.Lang = $preferredLang THEN $langBoost ELSE 1.0 END)
                """;
    }

    private void AddRankingParameters(List<SqliteParameter> parameters, string? preferredLanguage)
    {
        parameters.Add(new SqliteParameter("$wTitle", _search.Weights.Title));
        parameters.Add(new SqliteParameter("$wHeadings", _search.Weights.Headings));
        parameters.Add(new SqliteParameter("$wBody", _search.Weights.Body));
        parameters.Add(new SqliteParameter("$wTags", _search.Weights.Tags));
        parameters.Add(new SqliteParameter("$wPath", _search.Weights.Path));
        parameters.Add(new SqliteParameter("$recencyBoost", _search.RecencyBoost));
        parameters.Add(new SqliteParameter("$langBoost", _search.LanguageBoost));
        parameters.Add(new SqliteParameter("$preferredLang", preferredLanguage ?? string.Empty));
        parameters.Add(new SqliteParameter(
            "$recencyCutoff",
            clock.UtcNow.AddDays(-_search.RecencyBoostDays).ToString(TimestampFormat, CultureInfo.InvariantCulture)));
    }

    private static async Task<IReadOnlyList<SearchHit>> ReadHitsAsync(
        SqliteConnection connection,
        string sql,
        List<SqliteParameter> parameters,
        CancellationToken cancellationToken)
    {
        var hits = new List<SearchHit>();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters.ToArray());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            hits.Add(new SearchHit(
                reader.GetString(0),
                reader.GetString(1),
                Highlight(reader.IsDBNull(2) ? string.Empty : reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? [] : reader.GetString(4).Split(' ', StringSplitOptions.RemoveEmptyEntries),
                DateTimeOffset.ParseExact(reader.GetString(5), TimestampFormat, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
                reader.IsDBNull(6) ? 0 : reader.GetDouble(6)));
        }

        return hits;
    }

    /// <summary>
    /// HTML-escapes the snippet, then restores the highlight markers.
    /// </summary>
    /// <remarks>
    /// Order matters and is the whole point: <c>snippet()</c> hands back page content, page content
    /// contains <c>&lt;script&gt;</c> sometimes, and escaping after marking would eat the marks
    /// while escaping before marking would not have escaped anything.
    /// </remarks>
    private static string Highlight(string snippet) =>
        WebUtility.HtmlEncode(snippet)
            .Replace(MarkOpen, "<mark>", StringComparison.Ordinal)
            .Replace(MarkClose, "</mark>", StringComparison.Ordinal);

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static async Task<int> ScalarIntAsync(
        SqliteConnection connection,
        string sql,
        List<SqliteParameter> parameters,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new SqliteParameter(parameter.ParameterName, parameter.Value));
        }

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? 0 : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static async Task<SqliteConnection> OpenAsync(CompendioDbContext db, CancellationToken cancellationToken)
    {
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        return connection;
    }

    private static async Task<bool> IsScopeIndexedAsync(CompendioDbContext db, string pagePath, CancellationToken cancellationToken)
    {
        var scopes = await db.SecureScopes
            .Where(s => s.RetiredAt == null && s.IndexContent)
            .Select(s => s.FolderPath)
            .ToListAsync(cancellationToken);

        var path = ContentPath.FromTrusted(pagePath);
        return scopes.Any(s => path.IsSelfOrUnder(ContentPath.FromTrusted(s)));
    }

    private static async Task ReplaceLinksAsync(
        CompendioDbContext db,
        Guid pageId,
        IReadOnlyList<string> links,
        CancellationToken cancellationToken)
    {
        var connection = await OpenAsync(db, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = "DELETE FROM PageLinks WHERE PageId = $id";
            delete.Parameters.Add(new SqliteParameter("$id", pageId.ToString()));
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var target in links)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = "INSERT OR IGNORE INTO PageLinks (PageId, Target) VALUES ($id, $target)";
            insert.Parameters.Add(new SqliteParameter("$id", pageId.ToString()));
            insert.Parameters.Add(new SqliteParameter("$target", target));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
