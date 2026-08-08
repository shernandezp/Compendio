using Compendio.Domain.Content;

namespace Compendio.Application.Abstractions;

/// <summary>Why a secure scope is not currently usable. Reported, never guessed at.</summary>
public enum SecureAvailability
{
    Available = 0,

    /// <summary><c>keys/master.key</c> is missing. Non-secure content keeps serving.</summary>
    MasterKeyMissing = 1,

    /// <summary>Present but unreadable — wrong passphrase, wrong machine for DPAPI, bad ACL.</summary>
    MasterKeyUnreadable = 2,

    /// <summary>The scope's data key will not unwrap under the master key.</summary>
    DataKeyUnwrappable = 3,
}

/// <summary>
/// File-level encryption for secure scopes.
/// </summary>
/// <remarks>
/// A missing or unwrappable key must not stop the service: non-secure content serves normally and
/// secure scopes report <c>secure.unavailable</c>. It must not fail open either — there is no path
/// through here that returns plaintext when a key is unavailable.
/// </remarks>
public interface IContentCrypto
{
    /// <summary>False when the master key is missing or unreadable.</summary>
    bool IsAvailable { get; }

    SecureAvailability Availability { get; }

    /// <summary>Encrypts for a scope. The logical path is bound into the envelope's AAD.</summary>
    Task<byte[]> EncryptAsync(ContentPath scope, ContentPath logicalPath, byte[] plaintext, CancellationToken cancellationToken = default);

    /// <summary>
    /// Decrypts. Throws <c>secure.tampered</c> when authentication fails and
    /// <c>secure.unavailable</c> when the key cannot be obtained.
    /// </summary>
    Task<byte[]> DecryptAsync(ContentPath scope, ContentPath logicalPath, byte[] envelope, CancellationToken cancellationToken = default);

    /// <summary>Creates a scope's data key, wrapped under the master key.</summary>
    Task<Guid> CreateScopeKeyAsync(ContentPath scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// New data key for a scope. Resumable: a kill mid-rotation leaves every file readable under
    /// one key or the other, never neither.
    /// </summary>
    Task<Guid> RotateScopeKeyAsync(ContentPath scope, CancellationToken cancellationToken = default);

    /// <summary>New master key; rewraps the data keys only. Files are untouched.</summary>
    Task RotateMasterKeyAsync(CancellationToken cancellationToken = default);

    /// <summary>Per-scope health, for <c>doctor</c> and the admin status screen.</summary>
    Task<IReadOnlyDictionary<string, SecureAvailability>> ProbeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Protects small secrets that live in the database — nothing to do with content encryption.
/// </summary>
/// <remarks>
/// Backed by ASP.NET Core Data Protection, whose key ring must persist to <c>&lt;data&gt;/keys/</c>.
/// A service account with no home directory silently gets an in-memory key ring instead, which logs
/// every user out on every restart and only shows up in the deployed configurations. There is a
/// test for exactly that.
/// </remarks>
public interface ISecretProtector
{
    string Protect(string plaintext);

    bool TryUnprotect(string protectedValue, out string plaintext);
}
