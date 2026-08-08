namespace Compendio.Application.Abstractions;

/// <param name="Path">Content-relative path, which is also what the answer cites.</param>
/// <param name="Text">Plain text, already truncated to a passage-sized chunk.</param>
public sealed record RetrievedPassage(string Path, string Title, string Text);

/// <summary>
/// Finds the passages an answer may be built from.
/// </summary>
/// <remarks>
/// <para>
/// The single most important property of this interface is that it takes no permission parameters.
/// The implementation resolves the caller's readable folders itself and filters <em>before</em>
/// returning — there is no way for a handler to forget, and no overload that skips the check.
/// </para>
/// <para>
/// FTS only in v1. A vector index would need a native per-RID SQLite extension the chiselled
/// container cannot load, or an embeddings endpoint the "one base URL" promise does not have.
/// Query expansion by the model plus BM25 is the honest substitute.
/// </para>
/// </remarks>
public interface IAiRetrieval
{
    /// <summary>
    /// Passages the current user may read, for a natural-language question.
    /// </summary>
    /// <param name="queries">
    /// The expanded search queries. The caller has already asked the model to rewrite the question
    /// into a few keyword queries, because a question is a bad BM25 query and a keyword set is not.
    /// </param>
    Task<IReadOnlyList<RetrievedPassage>> FindAsync(
        IReadOnlyList<string> queries,
        int limit,
        CancellationToken cancellationToken = default);
}
