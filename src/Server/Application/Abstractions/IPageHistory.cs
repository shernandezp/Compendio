using Compendio.Domain.Content;
using Compendio.Domain.Entities;

namespace Compendio.Application.Abstractions;

public sealed record VersionSummary(
    Guid Id,
    int Sequence,
    DateTimeOffset CreatedAt,
    Guid? AuthorUserId,
    string? AuthorDisplayName,
    VersionSource Source,
    string ContentHash,
    long ByteSize,
    string? Note,
    string Path);

/// <param name="Kind">"unchanged" | "added" | "removed" | "modified".</param>
public sealed record DiffLine(string Kind, int? LeftLine, int? RightLine, string Text, IReadOnlyList<DiffSpan> Pieces);

public sealed record DiffSpan(string Kind, string Text);

/// <param name="Source">Word-level diff of the file text, for the IT admin.</param>
/// <param name="RenderedHtml">
/// Block-level added/removed/changed over the rendered HTML, with inline word highlighting. This is
/// the view that makes history usable by someone who does not read Markdown.
/// </param>
public sealed record PageDiff(
    VersionSummary From,
    VersionSummary To,
    IReadOnlyList<DiffLine> Source,
    string RenderedHtml,
    int AddedLines,
    int RemovedLines);

/// <summary>
/// Page history: full snapshots in SQLite, not an embedded git repository.
/// </summary>
/// <remarks>
/// libgit2 fights single-file publishing, Windows file locking and antivirus, and the chiselled
/// container base — three problems for a feature whose requirement is "show me what this page said
/// last month".
/// </remarks>
public interface IPageHistory
{
    /// <summary>
    /// Records a version. Every change gets one, whatever its source — UI save, external edit,
    /// move, delete.
    /// </summary>
    Task SnapshotAsync(
        Page page,
        byte[] content,
        VersionSource source,
        Guid? authorUserId,
        string? note,
        DateTimeOffset at,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VersionSummary>> ListAsync(Guid pageId, CancellationToken cancellationToken = default);

    /// <summary>Decompressed and, for a secure scope, decrypted in memory.</summary>
    Task<string?> ContentAsync(Guid versionId, CancellationToken cancellationToken = default);

    Task<PageDiff?> DiffAsync(Guid fromVersionId, Guid toVersionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a deleted page's versions for the retention window rather than dropping them.
    /// </summary>
    Task TombstoneAsync(Guid pageId, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the tombstones on a deleted page's versions, when the page is brought back.
    /// </summary>
    /// <remarks>
    /// The inverse of <see cref="TombstoneAsync"/>, and the reason a restore keeps the page's
    /// identity: the versions are keyed by it, so reviving them under the same id gives the restored
    /// page its whole history rather than a fresh one starting at the restore.
    /// </remarks>
    Task ReviveAsync(Guid pageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Thins history: everything inside the retention window, then one per day, never below the
    /// floor. Purges expired tombstones.
    /// </summary>
    Task<int> ApplyRetentionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Encrypts the versions already stored for a folder that has just become a secure scope.
    /// </summary>
    /// <remarks>
    /// Marking a folder secure rewrites its files into envelopes; without this its history keeps
    /// every earlier revision of those same documents in the database, unencrypted. "This folder is
    /// encrypted" would then be false for exactly the documents that were in it when somebody
    /// decided it needed to be — and the copy left behind is the whole page, not a fragment.
    /// </remarks>
    Task<int> EncryptExistingAsync(ContentPath scope, CancellationToken cancellationToken = default);
}
