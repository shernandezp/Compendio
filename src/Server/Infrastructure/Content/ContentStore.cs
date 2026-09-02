using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Domain;
using Compendio.Domain.Content;
using Compendio.Hosting.Configuration;
using Microsoft.Extensions.Options;

namespace Compendio.Infrastructure.Content;

/// <summary>
/// The disk, and the only code that touches it.
/// </summary>
/// <remarks>
/// <para>
/// Two invariants everything else leans on. First, every write is atomic: a temp file in the same
/// directory, flushed, then replaced into place — so a crash mid-save leaves the old file or the new
/// file and never a truncated one. Second, every write is conditional on the hash the caller last
/// read, so two writers produce a <c>page.conflict</c> carrying both versions rather than a silent
/// overwrite.
/// </para>
/// <para>
/// Encryption lives here rather than in the callers. A caller that forgets to encrypt writes a
/// secret in plaintext, and there is no way to notice afterwards, so the decision is made once at
/// the point the bytes reach the disk.
/// </para>
/// </remarks>
public sealed class ContentStore(
    IPathPolicy paths,
    ISecureScopeRegistry secureScopes,
    IContentCrypto crypto,
    IClock clock,
    IOptions<CompendioOptions> options,
    ILogger<ContentStore> logger) : IContentStore
{
    private readonly ContentOptions _content = options.Value.Content;

    /// <summary>
    /// Hashes this process wrote, so the watcher does not re-enter them as external changes.
    /// Getting this wrong produces an index-rebuild loop that only shows up under real use.
    /// </summary>
    private readonly ConcurrentDictionary<string, (string Hash, DateTimeOffset At)> _ownWrites = new(StringComparer.Ordinal);

    public bool Exists(ContentPath path) => ResolveOnDisk(path) is not null;

    public bool FolderExists(ContentPath path) =>
        paths.TryResolve(path, out var absolute) && Directory.Exists(absolute);

    public IReadOnlySet<string> EntryNames(ContentPath folder)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!paths.TryResolve(folder, out var absolute) || !Directory.Exists(absolute))
        {
            return names;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(absolute))
        {
            var name = Path.GetFileName(entry);
            if (name.EndsWith(CompendioConstants.EncryptedExtension, StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^CompendioConstants.EncryptedExtension.Length];
            }

            names.Add(name);
        }

        return names;
    }

    public async Task<ContentFile?> ReadAsync(ContentPath path, CancellationToken cancellationToken = default)
    {
        var located = ResolveOnDisk(path);
        if (located is null)
        {
            return null;
        }

        var (absolute, isEncrypted) = located.Value;
        var info = new FileInfo(absolute);

        // The page budget applies to pages. An attachment has its own, larger limit, enforced on
        // upload — refusing to read back a 5 MB PDF the upload rules had accepted was a bug.
        if (IsPage(path) && info.Length > _content.MaxPageBytes && !isEncrypted)
        {
            logger.LogWarning("Refusing to read {Path}: {Size} bytes exceeds Content:MaxPageBytes.", path.Value, info.Length);
            throw CompendioException.BadRequest(ProblemCodes.PathInvalid, path.Value);
        }

        var raw = await File.ReadAllBytesAsync(absolute, cancellationToken);

        byte[] plaintext;
        if (isEncrypted)
        {
            var scope = await secureScopes.ScopeForAsync(path, cancellationToken)
                        ?? throw CompendioException.SecureUnavailable(path.Value);

            plaintext = await crypto.DecryptAsync(scope, path, raw, cancellationToken);
        }
        else
        {
            plaintext = raw;
        }

        return new ContentFile(
            path,
            plaintext,
            Hash(plaintext),
            plaintext.LongLength,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            isEncrypted);
    }

    public async Task<ContentFile> WriteAsync(
        ContentPath path,
        byte[] bytes,
        string? expectedHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (IsPage(path) && bytes.LongLength > _content.MaxPageBytes)
        {
            throw CompendioException.BadRequest(ProblemCodes.PathInvalid, path.Value);
        }

        var scope = await secureScopes.ScopeForAsync(path, cancellationToken);
        var located = ResolveOnDisk(path);

        await GuardAgainstConflictAsync(path, located, expectedHash, cancellationToken);

        if (!paths.TryResolve(path, out var logical))
        {
            throw CompendioException.InvalidPath(PathRule.EscapesRoot);
        }

        var target = scope is null ? logical : logical + CompendioConstants.EncryptedExtension;
        var payload = scope is null
            ? bytes
            : await crypto.EncryptAsync(scope.Value, path, bytes, cancellationToken);

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        var hash = Hash(bytes);

        // Announced *before* the write: the watcher can fire while we are still inside this call.
        ExpectOwnWrite(path, hash);

        await WriteAtomicAsync(target, payload, cancellationToken);

        // A page that moves between plaintext and secure must not leave the other form behind.
        if (scope is null)
        {
            TryDelete(logical + CompendioConstants.EncryptedExtension);
        }
        else if (located is { IsEncrypted: false })
        {
            ShredBestEffort(logical);
        }

        return new ContentFile(path, bytes, hash, bytes.LongLength, clock.UtcNow, scope is not null);
    }

    /// <summary>
    /// Replaces a known byte range. Used for ticking a checkbox in read mode.
    /// </summary>
    /// <remarks>
    /// This is a substitution, not a re-serialization. The server has no Markdown serializer and
    /// this method does not become one: it validates the expected text at the expected offset,
    /// swaps it for text of the same intent, and writes the result back through the same conditional
    /// atomic write as everything else.
    /// </remarks>
    public async Task<ContentFile> SubstituteAsync(
        ContentPath path,
        int offset,
        string expected,
        string replacement,
        string expectedHash,
        CancellationToken cancellationToken = default)
    {
        var current = await ReadAsync(path, cancellationToken)
                      ?? throw CompendioException.NotFound(path);

        if (!string.Equals(current.ContentHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new ContentConflictException(path, expectedHash, current.ContentHash, current.Text);
        }

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var replacementBytes = Encoding.UTF8.GetBytes(replacement);

        if (offset < 0 || offset + expectedBytes.Length > current.Bytes.Length)
        {
            throw new ContentConflictException(path, expectedHash, current.ContentHash, current.Text);
        }

        if (!current.Bytes.AsSpan(offset, expectedBytes.Length).SequenceEqual(expectedBytes))
        {
            // The offset is stale even though the hash matched — that means the caller computed it
            // against different content, which is a conflict from the user's point of view.
            throw new ContentConflictException(path, expectedHash, current.ContentHash, current.Text);
        }

        var updated = new byte[current.Bytes.Length - expectedBytes.Length + replacementBytes.Length];
        current.Bytes.AsSpan(0, offset).CopyTo(updated);
        replacementBytes.CopyTo(updated, offset);
        current.Bytes.AsSpan(offset + expectedBytes.Length)
            .CopyTo(updated.AsSpan(offset + replacementBytes.Length));

        return await WriteAsync(path, updated, expectedHash, cancellationToken);
    }

    public Task DeleteAsync(ContentPath path, CancellationToken cancellationToken = default)
    {
        var located = ResolveOnDisk(path);
        if (located is null)
        {
            return Task.CompletedTask;
        }

        if (located.Value.IsEncrypted)
        {
            ShredBestEffort(located.Value.Absolute);
        }
        else
        {
            File.Delete(located.Value.Absolute);
        }

        return Task.CompletedTask;
    }

    public async Task MoveAsync(ContentPath from, ContentPath to, CancellationToken cancellationToken = default)
    {
        var source = ResolveOnDisk(from) ?? throw CompendioException.NotFound(from);

        // A rename that only changes letter case finds "itself" at the destination on Windows. The
        // move below handles that correctly; on a case-sensitive disk a genuinely distinct file at
        // the destination surfaces from the move itself and is reported the same way.
        var caseOnly = to.IsCaseVariantOf(from);

        if (!caseOnly && ResolveOnDisk(to) is not null)
        {
            throw CompendioException.Exists(to);
        }

        var fromScope = await secureScopes.ScopeForAsync(from, cancellationToken);
        var toScope = await secureScopes.ScopeForAsync(to, cancellationToken);

        if (fromScope is null && toScope is null)
        {
            if (!paths.TryResolve(to, out var plainTarget))
            {
                throw CompendioException.InvalidPath(PathRule.EscapesRoot);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(plainTarget)!);

            try
            {
                File.Move(source.Absolute, plainTarget);
            }
            catch (IOException) when (caseOnly && File.Exists(plainTarget) && !SameEntry(source.Absolute, plainTarget))
            {
                throw CompendioException.Exists(to);
            }

            return;
        }

        if (caseOnly)
        {
            // Inside a secure scope the envelope binds the logical path, so even a case change means
            // re-encrypting under the new spelling. Write-then-delete, the order used below, is not
            // available here: on a case-insensitive disk the destination *is* the source, and the
            // conditional write would refuse to create over it. Delete-then-write instead; the
            // plaintext is in memory and in history, so a crash in between loses nothing final.
            var plaintext = await ReadAsync(from, cancellationToken) ?? throw CompendioException.NotFound(from);
            await DeleteAsync(from, cancellationToken);
            await WriteAsync(to, plaintext.Bytes, expectedHash: null, cancellationToken);
            return;
        }

        // Crossing a secure boundary in either direction, or moving inside one: the AAD binds the
        // logical path, so the bytes have to be re-encrypted under the new path whatever happens.
        var content = await ReadAsync(from, cancellationToken) ?? throw CompendioException.NotFound(from);
        await WriteAsync(to, content.Bytes, expectedHash: null, cancellationToken);
        await DeleteAsync(from, cancellationToken);
    }

    public Task CreateFolderAsync(ContentPath path, CancellationToken cancellationToken = default)
    {
        if (!paths.TryResolve(path, out var absolute))
        {
            throw CompendioException.InvalidPath(PathRule.EscapesRoot);
        }

        Directory.CreateDirectory(absolute);
        return Task.CompletedTask;
    }

    public Task DeleteFolderAsync(ContentPath path, bool recursive, CancellationToken cancellationToken = default)
    {
        if (path.IsRoot)
        {
            throw CompendioException.InvalidPath(PathRule.EscapesRoot);
        }

        if (!paths.TryResolve(path, out var absolute) || !Directory.Exists(absolute))
        {
            return Task.CompletedTask;
        }

        Directory.Delete(absolute, recursive);
        return Task.CompletedTask;
    }

    public async Task MoveFolderAsync(ContentPath from, ContentPath to, CancellationToken cancellationToken = default)
    {
        if (!paths.TryResolve(from, out var source) || !Directory.Exists(source))
        {
            throw CompendioException.NotFound(from);
        }

        if (!paths.TryResolve(to, out var target))
        {
            throw CompendioException.InvalidPath(PathRule.EscapesRoot);
        }

        var caseOnly = to.IsCaseVariantOf(from);

        if (!caseOnly && (Directory.Exists(target) || File.Exists(target)))
        {
            throw CompendioException.Exists(to);
        }

        var fromSecure = await secureScopes.IsSecureAsync(from, cancellationToken);
        var toSecure = await secureScopes.IsSecureAsync(to, cancellationToken);

        if (caseOnly || (fromSecure == toSecure && !fromSecure))
        {
            // A case-only rename inside a secure scope keeps every file's logical path — only the
            // spelling of the folder changes, and the envelopes are bound to the path the registry
            // will resolve after the rename, which is this one. Nothing needs re-encrypting.
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            try
            {
                Directory.Move(source, target);
            }
            catch (IOException) when (caseOnly && Directory.Exists(target) && !SameEntry(source, target))
            {
                throw CompendioException.Exists(to);
            }

            return;
        }

        // Same reason as MoveAsync: the envelope binds the logical path, so every file is rewritten.
        var entries = new List<ContentEntry>();
        await foreach (var entry in EnumerateAsync(from, cancellationToken))
        {
            entries.Add(entry);
        }

        foreach (var entry in entries.Where(e => !e.IsFolder))
        {
            await MoveAsync(entry.Path, entry.Path.Rebase(from, to), cancellationToken);
        }

        Directory.Delete(source, recursive: true);
    }

    public async IAsyncEnumerable<ContentEntry> EnumerateAsync(
        ContentPath root,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!paths.TryResolve(root, out var absolute) || !Directory.Exists(absolute))
        {
            yield break;
        }

        var pending = new Stack<string>();
        pending.Push(absolute);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();

            IEnumerable<string> directories;
            IEnumerable<string> files;
            try
            {
                directories = Directory.EnumerateDirectories(current);
                files = Directory.EnumerateFiles(current);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or DirectoryNotFoundException)
            {
                logger.LogWarning("Skipping '{Directory}' during enumeration: {Message}", current, e.Message);
                continue;
            }

            foreach (var directory in directories)
            {
                if (!paths.TryMap(directory, PathKind.Folder, out var folderPath) || PathPolicy.IsIgnored(folderPath.Value))
                {
                    continue;
                }

                pending.Push(directory);
                var info = new DirectoryInfo(directory);
                yield return new ContentEntry(folderPath.Value, IsFolder: true, 0,
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), IsEncryptedOnDisk: false);
            }

            foreach (var file in files)
            {
                var isEncrypted = file.EndsWith(CompendioConstants.EncryptedExtension, StringComparison.OrdinalIgnoreCase);
                var logicalName = isEncrypted
                    ? file[..^CompendioConstants.EncryptedExtension.Length]
                    : file;

                if (!paths.TryMap(logicalName, PathKind.Any, out var filePath) || PathPolicy.IsIgnored(filePath.Value))
                {
                    continue;
                }

                var info = new FileInfo(file);
                yield return new ContentEntry(filePath.Value, IsFolder: false, info.Length,
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), isEncrypted);
            }

            await Task.Yield();
        }
    }

    public async Task<string?> HashAsync(ContentPath path, CancellationToken cancellationToken = default)
    {
        var file = await ReadAsync(path, cancellationToken);
        return file?.ContentHash;
    }

    public void ExpectOwnWrite(ContentPath path, string contentHash) =>
        _ownWrites[path.Value] = (contentHash, clock.UtcNow);

    public bool WasOwnWrite(ContentPath path, string contentHash)
    {
        if (!_ownWrites.TryGetValue(path.Value, out var entry))
        {
            return false;
        }

        if (clock.UtcNow - entry.At > TimeSpan.FromSeconds(_content.OwnWriteWindowSeconds))
        {
            _ownWrites.TryRemove(path.Value, out _);
            return false;
        }

        return string.Equals(entry.Hash, contentHash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>SHA-256 of the file's plaintext bytes, lower-case hex.</summary>
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static bool IsPage(ContentPath path) => path.Extension == CompendioConstants.MarkdownExtension;

    /// <summary>
    /// Whether two absolute paths that differ only by case name the same directory entry.
    /// </summary>
    /// <remarks>
    /// Asked of the disk, not of the platform: the entry's actual name is what a listing returns,
    /// and if the destination's listed name is the source's spelling then there is one entry, not
    /// two. A macOS volume can be either way, so "is this Windows" would be the wrong question.
    /// </remarks>
    private static bool SameEntry(string source, string target)
    {
        var directory = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(directory))
        {
            return false;
        }

        var actual = Directory.EnumerateFileSystemEntries(directory, Path.GetFileName(target))
            .Select(Path.GetFileName)
            .FirstOrDefault();

        return actual is not null && string.Equals(actual, Path.GetFileName(source), StringComparison.Ordinal);
    }

    private async Task GuardAgainstConflictAsync(
        ContentPath path,
        (string Absolute, bool IsEncrypted)? located,
        string? expectedHash,
        CancellationToken cancellationToken)
    {
        if (located is null)
        {
            // Creating. A caller that thought it was updating gets told the page vanished.
            if (!string.IsNullOrEmpty(expectedHash))
            {
                throw new ContentConflictException(path, expectedHash, string.Empty, string.Empty);
            }

            return;
        }

        if (expectedHash is null)
        {
            // Unconditional create over an existing file is never what the caller meant.
            throw CompendioException.Exists(path);
        }

        var current = await ReadAsync(path, cancellationToken);
        if (current is not null && !string.Equals(current.ContentHash, expectedHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new ContentConflictException(path, expectedHash, current.ContentHash, current.Text);
        }
    }

    /// <summary>
    /// Finds the file, whether it is stored as plaintext or inside an envelope. Returns null when
    /// neither exists or the path does not resolve inside the root.
    /// </summary>
    private (string Absolute, bool IsEncrypted)? ResolveOnDisk(ContentPath path)
    {
        if (!paths.TryResolve(path, out var absolute))
        {
            return null;
        }

        if (File.Exists(absolute))
        {
            return (absolute, false);
        }

        var encrypted = absolute + CompendioConstants.EncryptedExtension;
        return File.Exists(encrypted) ? (encrypted, true) : null;
    }

    /// <summary>How an in-flight atomic write is named while it is being written.</summary>
    private const string TemporaryWritePrefix = ".compendio-";

    private const string TemporaryWriteSuffix = ".tmp";

    /// <summary>
    /// Whether a file name is one of the store's own in-flight writes.
    /// </summary>
    /// <remarks>
    /// For anything that walks the content folder outside the store — the backup, most of all. The
    /// temp file is open with no sharing until it is moved into place, so on Windows reading it
    /// throws; and it is not content, so it has no business in an archive either way.
    /// </remarks>
    public static bool IsTemporaryWrite(string fileName) =>
        fileName.StartsWith(TemporaryWritePrefix, StringComparison.Ordinal) &&
        fileName.EndsWith(TemporaryWriteSuffix, StringComparison.OrdinalIgnoreCase);

    private static async Task WriteAtomicAsync(string target, byte[] payload, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(target)!;
        var temp = Path.Combine(directory, $"{TemporaryWritePrefix}{Guid.CreateVersion7():N}{TemporaryWriteSuffix}");

        try
        {
            await using (var stream = new FileStream(
                             temp,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 64 * 1024,
                             FileOptions.SequentialScan))
            {
                await stream.WriteAsync(payload, cancellationToken);
                // Durability before the rename, or the rename can land before the bytes do.
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(target))
            {
                // Replace keeps the destination's ACLs and attributes, which matters on a share.
                File.Replace(temp, target, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, target);
            }
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Antivirus and indexers hold handles briefly; the reconciler will catch the leftover.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Overwrites before deleting, then deletes.
    /// </summary>
    /// <remarks>
    /// Best effort, and the docs say so in those words: secure deletion is not achievable on SSDs,
    /// journalling or copy-on-write file systems, or snapshotted volumes. Promising it would be
    /// false, so this reduces the window rather than closing it.
    /// </remarks>
    private static void ShredBestEffort(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var length = new FileInfo(path).Length;
            if (length > 0 && length < 8 * 1024 * 1024)
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
                var buffer = new byte[Math.Min(length, 64 * 1024)];
                RandomNumberGenerator.Fill(buffer);

                var remaining = length;
                while (remaining > 0)
                {
                    var chunk = (int)Math.Min(remaining, buffer.Length);
                    stream.Write(buffer, 0, chunk);
                    remaining -= chunk;
                }

                stream.Flush(flushToDisk: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        TryDelete(path);
    }
}
