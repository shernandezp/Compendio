using System.Collections.Concurrent;
using System.Security.Cryptography;
using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Domain.Content;
using Compendio.Domain.Entities;
using Compendio.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace Compendio.Infrastructure.Crypto;

/// <summary>
/// Encrypts and decrypts secure-scope content with AES-256-GCM from the BCL.
/// </summary>
/// <remarks>
/// <para>
/// No third-party crypto, by decision: a native dependency here would fight single-file publishing
/// and the chiselled container base, and XChaCha20-Poly1305 would need a library when the point is
/// to need none.
/// </para>
/// <para>
/// There is no path through this class that returns plaintext when a key is unavailable. That is
/// the fail-closed half of the failure design; the fail-open half is that a missing key does not
/// stop the service — the rest of the wiki serves normally.
/// </para>
/// </remarks>
public sealed class ContentCrypto(
    MasterKeyStore masterKeys,
    IDbContextFactory<Persistence.CompendioDbContext> dbFactory,
    IClock clock,
    ISecureScopeRegistry registry,
    ILogger<ContentCrypto> logger) : IContentCrypto, IDisposable
{
    /// <summary>Unwrapped data keys, by scope path. Cleared when a scope is rotated or removed.</summary>
    private readonly ConcurrentDictionary<string, (Guid KeyId, byte[] Key)> _dataKeys = new(StringComparer.Ordinal);

    public bool IsAvailable => masterKeys.Availability == SecureAvailability.Available;

    public SecureAvailability Availability => masterKeys.Availability;

    public async Task<byte[]> EncryptAsync(ContentPath scope, ContentPath logicalPath, byte[] plaintext, CancellationToken cancellationToken = default)
    {
        var (keyId, key) = await ResolveKeyAsync(scope, cancellationToken);
        var envelope = CryptoEnvelope.Encrypt(plaintext, key, keyId, logicalPath.Value);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.SecureScopes
            .Where(s => s.FolderPath == scope.Value && s.KeyId == keyId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.EncryptionCount, x => x.EncryptionCount + 1), cancellationToken);

        return envelope;
    }

    public async Task<byte[]> DecryptAsync(ContentPath scope, ContentPath logicalPath, byte[] envelope, CancellationToken cancellationToken = default)
    {
        if (!CryptoEnvelope.TryReadHeader(envelope, out var header))
        {
            throw CompendioException.Tampered(logicalPath);
        }

        var key = await ResolveKeyByIdAsync(scope, header.KeyId, cancellationToken);

        try
        {
            return CryptoEnvelope.Decrypt(envelope, key, logicalPath.Value);
        }
        catch (TamperedEnvelopeException)
        {
            // Deliberately no path detail beyond the logical path itself: the exception message
            // must never carry any part of the plaintext.
            logger.LogError("Envelope authentication failed for a file in scope {Scope}.", scope.Value);
            throw CompendioException.Tampered(logicalPath);
        }
    }

    public async Task<Guid> CreateScopeKeyAsync(ContentPath scope, CancellationToken cancellationToken = default)
    {
        var master = masterKeys.EnsureCreated();
        var keyId = Guid.CreateVersion7();
        var dataKey = RandomNumberGenerator.GetBytes(CryptoEnvelope.KeyLength);
        var (wrapped, nonce) = CryptoEnvelope.WrapKey(dataKey, master, keyId);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.SecureScopes.Add(new SecureScope
        {
            Id = Guid.CreateVersion7(),
            FolderPath = scope.Value,
            KeyId = keyId,
            WrappedDek = wrapped,
            Nonce = nonce,
            CreatedAt = clock.UtcNow,
        });

        await db.SaveChangesAsync(cancellationToken);

        _dataKeys[scope.Value] = (keyId, dataKey);
        registry.Invalidate();
        return keyId;
    }

    /// <summary>
    /// Generates a new data key for a scope and retires the old one.
    /// </summary>
    /// <remarks>
    /// The old key stays in the table marked retired rather than being deleted, so a kill part-way
    /// through the file rewrite leaves every file readable under one key or the other — never
    /// neither. The rewrite itself is the caller's job and is resumable for the same reason.
    /// </remarks>
    public async Task<Guid> RotateScopeKeyAsync(ContentPath scope, CancellationToken cancellationToken = default)
    {
        var master = masterKeys.TryGet() ?? throw CompendioException.SecureUnavailable(scope.Value);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = clock.UtcNow;

        await db.SecureScopes
            .Where(s => s.FolderPath == scope.Value && s.RetiredAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RetiredAt, now), cancellationToken);

        var keyId = Guid.CreateVersion7();
        var dataKey = RandomNumberGenerator.GetBytes(CryptoEnvelope.KeyLength);
        var (wrapped, nonce) = CryptoEnvelope.WrapKey(dataKey, master, keyId);

        db.SecureScopes.Add(new SecureScope
        {
            Id = Guid.CreateVersion7(),
            FolderPath = scope.Value,
            KeyId = keyId,
            WrappedDek = wrapped,
            Nonce = nonce,
            CreatedAt = now,
            RotatedAt = now,
        });

        await db.SaveChangesAsync(cancellationToken);

        _dataKeys[scope.Value] = (keyId, dataKey);
        return keyId;
    }

    /// <summary>Cheap: the files are untouched, only the wrapped data keys are rewritten.</summary>
    public async Task RotateMasterKeyAsync(CancellationToken cancellationToken = default)
    {
        var oldMaster = masterKeys.TryGet()
                        ?? throw CompendioException.SecureUnavailable("master");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var scopes = await db.SecureScopes.ToListAsync(cancellationToken);

        var unwrapped = new List<(SecureScope Scope, byte[] Key)>(scopes.Count);
        foreach (var scope in scopes)
        {
            unwrapped.Add((scope, CryptoEnvelope.UnwrapKey(scope.WrappedDek, scope.Nonce, oldMaster, scope.KeyId)));
        }

        var newMaster = masterKeys.Replace();

        foreach (var (scope, key) in unwrapped)
        {
            var (wrapped, nonce) = CryptoEnvelope.WrapKey(key, newMaster, scope.KeyId);
            scope.WrappedDek = wrapped;
            scope.Nonce = nonce;
            CryptographicOperations.ZeroMemory(key);
        }

        await db.SaveChangesAsync(cancellationToken);
        ClearCache();
    }

    public async Task<IReadOnlyDictionary<string, SecureAvailability>> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, SecureAvailability>(StringComparer.Ordinal);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var scopes = await db.SecureScopes
            .Where(s => s.RetiredAt == null)
            .ToListAsync(cancellationToken);

        var master = masterKeys.TryGet();

        foreach (var scope in scopes)
        {
            if (master is null)
            {
                result[scope.FolderPath] = masterKeys.Availability;
                continue;
            }

            try
            {
                var key = CryptoEnvelope.UnwrapKey(scope.WrappedDek, scope.Nonce, master, scope.KeyId);
                CryptographicOperations.ZeroMemory(key);
                result[scope.FolderPath] = SecureAvailability.Available;
            }
            catch (TamperedEnvelopeException)
            {
                result[scope.FolderPath] = SecureAvailability.DataKeyUnwrappable;
            }
        }

        return result;
    }

    public void Dispose() => ClearCache();

    private void ClearCache()
    {
        foreach (var (_, entry) in _dataKeys)
        {
            CryptographicOperations.ZeroMemory(entry.Key);
        }

        _dataKeys.Clear();
    }

    /// <summary>The scope's current (non-retired) key.</summary>
    private async Task<(Guid KeyId, byte[] Key)> ResolveKeyAsync(ContentPath scope, CancellationToken cancellationToken)
    {
        if (_dataKeys.TryGetValue(scope.Value, out var cached))
        {
            return cached;
        }

        var master = masterKeys.TryGet() ?? throw CompendioException.SecureUnavailable(scope.Value);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.SecureScopes
            .Where(s => s.FolderPath == scope.Value && s.RetiredAt == null)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw CompendioException.SecureUnavailable(scope.Value);

        var key = UnwrapOrThrow(row, master, scope);
        var entry = (row.KeyId, key);
        _dataKeys[scope.Value] = entry;
        return entry;
    }

    /// <summary>
    /// A specific historical key, so a file written before a rotation still opens. This is why
    /// retired keys are kept rather than deleted.
    /// </summary>
    private async Task<byte[]> ResolveKeyByIdAsync(ContentPath scope, Guid keyId, CancellationToken cancellationToken)
    {
        if (_dataKeys.TryGetValue(scope.Value, out var cached) && cached.KeyId == keyId)
        {
            return cached.Key;
        }

        var master = masterKeys.TryGet() ?? throw CompendioException.SecureUnavailable(scope.Value);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.SecureScopes.FirstOrDefaultAsync(s => s.KeyId == keyId, cancellationToken)
                  ?? throw CompendioException.SecureUnavailable(scope.Value);

        return UnwrapOrThrow(row, master, scope);
    }

    private byte[] UnwrapOrThrow(SecureScope row, byte[] master, ContentPath scope)
    {
        try
        {
            return CryptoEnvelope.UnwrapKey(row.WrappedDek, row.Nonce, master, row.KeyId);
        }
        catch (TamperedEnvelopeException)
        {
            logger.LogError("The data key for scope {Scope} will not unwrap under the current master key.", scope.Value);
            throw CompendioException.SecureUnavailable(scope.Value);
        }
    }
}
