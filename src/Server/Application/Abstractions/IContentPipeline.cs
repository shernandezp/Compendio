using Compendio.Domain.Content;
using Compendio.Domain.Entities;

namespace Compendio.Application.Abstractions;

/// <summary>
/// The one ordered change pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Every change to content — a UI save, an external edit, a move, a delete, a reconciliation
/// finding — goes through here, so page metadata, the index queue, history snapshots and ACL path
/// maintenance are updated in one place and in one order. Ordering bugs are then a bug in one file
/// rather than a class of bug spread across four.
/// </para>
/// <para>
/// The methods prefixed <c>Ingest</c> are the file-system side: they attribute to nobody, take the
/// file's own timestamp, and never assume the last signed-in user did it.
/// </para>
/// </remarks>
public interface IContentPipeline
{
    /// <summary>Writes a page through the store and syncs the database in the same operation.</summary>
    /// <param name="source">
    /// How this change came about. <see cref="VersionSource.Normalization"/> for the one-time
    /// rewrite to canonical Markdown on a page's first editor save, so the noisy diff is
    /// attributable rather than mysterious; <see cref="VersionSource.Restore"/> when a version is
    /// restored, so history says what happened instead of showing an ordinary edit.
    /// </param>
    Task<Page> SavePageAsync(
        ContentPath path,
        byte[] content,
        string? expectedHash,
        Guid? actorUserId,
        VersionSource source = VersionSource.Editor,
        string? note = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Syncs the database from a file this process has already written.
    /// </summary>
    /// <remarks>
    /// For the checkbox substitution, which writes through the store directly. Routing it back
    /// through <see cref="SavePageAsync"/> would write the identical bytes to disk a second time.
    /// </remarks>
    Task<Page> RecordSavedAsync(
        ContentPath path,
        Guid? actorUserId,
        string? note = null,
        CancellationToken cancellationToken = default);

    Task DeletePageAsync(ContentPath path, Guid? actorUserId, CancellationToken cancellationToken = default);

    Task<Page> MovePageAsync(ContentPath from, ContentPath to, Guid? actorUserId, CancellationToken cancellationToken = default);

    Task<Folder> EnsureFolderAsync(ContentPath path, CancellationToken cancellationToken = default);

    Task DeleteFolderAsync(ContentPath path, Guid? actorUserId, CancellationToken cancellationToken = default);

    Task MoveFolderAsync(ContentPath from, ContentPath to, Guid? actorUserId, CancellationToken cancellationToken = default);

    // ---- File-system side -----------------------------------------------------------------------

    Task IngestChangeAsync(ContentPath path, CancellationToken cancellationToken = default);

    Task IngestDeleteAsync(ContentPath path, CancellationToken cancellationToken = default);

    Task IngestMoveAsync(ContentPath from, ContentPath to, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a folder that moved on disk without us moving it.
    /// </summary>
    /// <remarks>
    /// The database side of a folder move, with no disk write — the rename already happened. This is
    /// what keeps a restricted folder restricted when somebody renames it in Explorer: without it
    /// the pass sees a folder that vanished and a folder that appeared, tombstones the access rules
    /// at the old path, and the new one inherits from its parent. The folder would still be there,
    /// with the same documents in it, and open to everybody.
    /// </remarks>
    Task IngestFolderMoveAsync(ContentPath from, ContentPath to, CancellationToken cancellationToken = default);
}
