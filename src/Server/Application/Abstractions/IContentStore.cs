using Compendio.Domain.Content;

namespace Compendio.Application.Abstractions;

/// <param name="WasEncrypted">
/// True when the bytes came out of a <c>.enc</c> envelope. The caller needs to know so it does not
/// write plaintext back over a secure page.
/// </param>
public sealed record ContentFile(
    ContentPath Path,
    byte[] Bytes,
    string ContentHash,
    long ByteSize,
    DateTimeOffset ModifiedAt,
    bool WasEncrypted)
{
    /// <summary>UTF-8 text of the file, BOM stripped.</summary>
    public string Text => System.Text.Encoding.UTF8.GetString(Bytes).TrimStart('﻿');
}

/// <param name="Path">Content-relative path. Always plaintext-logical: no <c>.enc</c> suffix.</param>
public sealed record ContentEntry(ContentPath Path, bool IsFolder, long ByteSize, DateTimeOffset ModifiedAt, bool IsEncryptedOnDisk);

/// <summary>
/// The disk, behind one interface.
/// </summary>
/// <remarks>
/// <para>
/// Every method takes a <see cref="ContentPath"/>, which by construction has passed
/// <see cref="PathPolicy"/> — that is what makes the path-safety property test tractable: it has a
/// finite set of entry points to prove nothing escapes from.
/// </para>
/// <para>
/// Encryption is handled here rather than above: callers work in plaintext and the store decides,
/// from the secure-scope map, whether the bytes hit the disk inside an envelope. Otherwise every
/// caller would have to remember, and one forgetting is a plaintext leak.
/// </para>
/// </remarks>
public interface IContentStore
{
    bool Exists(ContentPath path);

    bool FolderExists(ContentPath path);

    /// <summary>
    /// The logical names directly inside a folder — files with any <c>.enc</c> suffix removed, and
    /// sub-folders — compared case-insensitively.
    /// </summary>
    /// <remarks>
    /// For choosing a new name. <see cref="Exists"/> answers the file system's question, which on
    /// Windows ignores case and on Linux does not; a name picked against that answer on Linux can
    /// collide the moment the folder is copied to a Windows share. Empty when the folder does not
    /// exist yet.
    /// </remarks>
    IReadOnlySet<string> EntryNames(ContentPath folder);

    Task<ContentFile?> ReadAsync(ContentPath path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomic write: temp file in the same directory, flush, atomic replace. A crash mid-save
    /// leaves the old file or the new file, never a truncated one. Original line endings preserved.
    /// </summary>
    /// <param name="expectedHash">
    /// The hash the caller last read. A mismatch throws <c>page.conflict</c> carrying both
    /// versions — never an overwrite. Null only for a create.
    /// </param>
    Task<ContentFile> WriteAsync(
        ContentPath path,
        byte[] bytes,
        string? expectedHash,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a known byte range, validated against the expected old value and the content hash.
    /// </summary>
    /// <remarks>
    /// This is how a checkbox is ticked in read mode: <c>- [ ]</c> becomes <c>- [x]</c> at a known
    /// offset. The server may do this because it is not writing Markdown, it is editing two
    /// characters — the canonical-Markdown rule stays intact.
    /// </remarks>
    Task<ContentFile> SubstituteAsync(
        ContentPath path,
        int offset,
        string expected,
        string replacement,
        string expectedHash,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(ContentPath path, CancellationToken cancellationToken = default);

    Task MoveAsync(ContentPath from, ContentPath to, CancellationToken cancellationToken = default);

    Task CreateFolderAsync(ContentPath path, CancellationToken cancellationToken = default);

    Task DeleteFolderAsync(ContentPath path, bool recursive, CancellationToken cancellationToken = default);

    Task MoveFolderAsync(ContentPath from, ContentPath to, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every page, attachment and folder under <paramref name="root"/>, ignore rules applied. What
    /// reconciliation walks.
    /// </summary>
    IAsyncEnumerable<ContentEntry> EnumerateAsync(ContentPath root, CancellationToken cancellationToken = default);

    /// <summary>SHA-256 of the file's bytes as they would be read (that is, after decryption).</summary>
    Task<string?> HashAsync(ContentPath path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that the application itself is about to write these bytes, so the watcher does not
    /// re-enter them as an external change. Expected hashes expire after a few seconds.
    /// </summary>
    void ExpectOwnWrite(ContentPath path, string contentHash);

    /// <summary>True if this exact (path, hash) was an application write within the window.</summary>
    bool WasOwnWrite(ContentPath path, string contentHash);
}
