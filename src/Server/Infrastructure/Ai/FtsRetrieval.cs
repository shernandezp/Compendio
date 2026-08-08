using Compendio.Application.Abstractions;
using Compendio.Application.Ai;
using Compendio.Domain.Content;
using Compendio.Domain.Security;

namespace Compendio.Infrastructure.Ai;

/// <summary>
/// Retrieval over the existing FTS5 index, filtered before anything reaches a model.
/// </summary>
/// <remarks>
/// <para>
/// The order here is the whole security property, so it is written to be read top to bottom:
/// resolve the caller's readable folders, run the search with that predicate <em>in the SQL</em>,
/// drop anything the AI guard excludes, and only then read content off disk. Nothing is filtered
/// after the fact, nothing is left to the prompt, and the model is never asked to be careful.
/// </para>
/// <para>
/// Worth stating plainly: this is a worse retriever than a good vector index. It is also one that
/// adds no native dependency, no second endpoint and no second configuration — and the leak risk
/// lives in the filter above, which any retriever would need.
/// </para>
/// </remarks>
public sealed class FtsRetrieval(
    ISearchIndex index,
    IPermissionEvaluator permissions,
    IContentStore store,
    ICurrentUser currentUser,
    IAiSettings settings,
    AiGuard guard,
    ILogger<FtsRetrieval> logger) : IAiRetrieval
{
    /// <summary>Characters of each page used as a passage. Enough for a procedure's opening steps.</summary>
    private const int PassageCharacters = 1_800;

    public async Task<IReadOnlyList<RetrievedPassage>> FindAsync(
        IReadOnlyList<string> queries,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var subject = currentUser.Subject;
        var readable = await permissions.ReadableFolderPathsAsync(subject, cancellationToken);
        var configuration = await settings.GetAsync(cancellationToken);

        // Ranked union: a page found by two of the expanded queries is more likely to be the one
        // being asked about than a page found by one, and this is the cheapest way to say so.
        var scores = new Dictionary<string, (double Rank, string Title)>(StringComparer.Ordinal);

        foreach (var query in queries.Where(q => !string.IsNullOrWhiteSpace(q)).Take(4))
        {
            var results = await index.SearchAsync(new SearchRequest
            {
                Query = query,
                ReadableFolderPaths = readable,
                BypassFolderFilter = subject.Role == UserRole.Admin,
                PreferredLanguage = currentUser.Language,
                PageSize = Math.Clamp(limit * 2, 5, 40),
            }, cancellationToken);

            foreach (var hit in results.Items)
            {
                var existing = scores.GetValueOrDefault(hit.Path);
                scores[hit.Path] = (existing.Rank + 1.0, hit.Title);
            }
        }

        var passages = new List<RetrievedPassage>();

        foreach (var (path, entry) in scores.OrderByDescending(s => s.Value.Rank).ThenBy(s => s.Key, StringComparer.Ordinal))
        {
            if (passages.Count >= limit)
            {
                break;
            }

            var content = ContentPath.FromTrusted(path);

            // A second lock, not a duplicate of the first: the search predicate answers "may this
            // person read it", and this answers "may its content leave the instance at all".
            if (!await guard.IsContentAllowedAsync(configuration, content, cancellationToken))
            {
                continue;
            }

            var file = await store.ReadAsync(content, cancellationToken);
            if (file is null)
            {
                // Indexed but gone from disk — reconciliation has not caught up. Skipping is right:
                // answering from a page that no longer exists is worse than answering from fewer.
                continue;
            }

            var document = MarkdownParser.Parse(file.Text);
            var text = MarkdownText.Extract(document.Body);

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            passages.Add(new RetrievedPassage(
                path,
                entry.Title,
                text.Length > PassageCharacters ? text[..PassageCharacters] : text));
        }

        logger.LogDebug("Retrieval returned {Count} passage(s) from {Candidates} candidate(s).", passages.Count, scores.Count);
        return passages;
    }
}
