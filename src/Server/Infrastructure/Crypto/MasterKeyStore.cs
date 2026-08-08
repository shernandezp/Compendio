using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Compendio.Application.Abstractions;
using Compendio.Hosting;
using Compendio.Hosting.Configuration;
using Microsoft.Extensions.Options;

namespace Compendio.Infrastructure.Crypto;

/// <summary>
/// The instance master key: 32 random bytes, one per instance, wrapping every scope's data key.
/// </summary>
/// <remarks>
/// <para>
/// Two levels of key and no more. The master key lives in <c>&lt;data&gt;/keys/master.key</c>,
/// protected by DPAPI at <c>LocalMachine</c> scope on Windows and by mode <c>0600</c> on Linux.
/// Wrapped data keys live in the database, which is what makes the database itself safe to hand to
/// a DBA: without <c>keys/</c> they are inert.
/// </para>
/// <para>
/// Passphrase mode exists for organizations whose threat model includes the OS disk — the machine
/// itself being stolen, not just a backup tape. Once <c>Security:MasterPassphrase</c> is set, the
/// service cannot open secure scopes without it, and that is the trade being bought.
/// </para>
/// </remarks>
public sealed class MasterKeyStore(
    DataDirectory dataDirectory,
    IOptions<CompendioOptions> options,
    ILogger<MasterKeyStore> logger)
{
    private static ReadOnlySpan<byte> FileMagic => "CMPDMK01"u8;

    private const byte ModePlatform = 0x01;
    private const byte ModePassphrase = 0x02;
    private const int SaltLength = 16;
    private const int Pbkdf2Iterations = 600_000;

    private readonly SecurityOptions _security = options.Value.Security;
    private readonly Lock _gate = new();

    private byte[]? _cached;
    private SecureAvailability _availability = SecureAvailability.MasterKeyMissing;

    public SecureAvailability Availability
    {
        get
        {
            EnsureLoaded();
            return _availability;
        }
    }

    public bool Exists => File.Exists(dataDirectory.MasterKeyFile);

    /// <summary>The master key, or null when it is missing or unreadable. Never throws on load.</summary>
    public byte[]? TryGet()
    {
        EnsureLoaded();
        return _cached;
    }

    /// <summary>Creates the master key if it does not exist. Called when the first scope is made.</summary>
    public byte[] EnsureCreated()
    {
        lock (_gate)
        {
            if (_cached is not null)
            {
                return _cached;
            }

            if (File.Exists(dataDirectory.MasterKeyFile))
            {
                LoadLocked();
                return _cached ?? throw new InvalidOperationException(
                    "keys/master.key exists but cannot be read. Refusing to overwrite it — that would " +
                    "make every existing secure page unreadable. See `compendio doctor`.");
            }

            var key = RandomNumberGenerator.GetBytes(CryptoEnvelopeConstants.KeyLength);
            WriteLocked(key);
            _cached = key;
            _availability = SecureAvailability.Available;
            logger.LogInformation("Created a new instance master key at {Path}.", dataDirectory.MasterKeyFile);
            return key;
        }
    }

    /// <summary>
    /// Writes a master key recovered from a backup, protected for <em>this</em> machine.
    /// </summary>
    /// <remarks>
    /// The cross-machine restore is the whole reason this exists: DPAPI at <c>LocalMachine</c> scope
    /// is machine-bound, so a verbatim copy of <c>master.key</c> would be unreadable on the new box.
    /// </remarks>
    public void Import(byte[] masterKey)
    {
        lock (_gate)
        {
            WriteLocked(masterKey);
            _cached = masterKey;
            _availability = SecureAvailability.Available;
        }
    }

    /// <summary>Replaces the master key. Callers must rewrap every data key in the same operation.</summary>
    public byte[] Replace()
    {
        lock (_gate)
        {
            var key = RandomNumberGenerator.GetBytes(CryptoEnvelopeConstants.KeyLength);
            WriteLocked(key);
            _cached = key;
            _availability = SecureAvailability.Available;
            return key;
        }
    }

    /// <summary>
    /// Rewraps the master key under an arbitrary passphrase, for <c>compendio backup</c>.
    /// </summary>
    /// <remarks>
    /// The backup trap is real and has two obvious wrong answers: excluding <c>keys/</c> produces an
    /// archive that restores into unreadable garbage, and including it produces an archive where the
    /// key sits beside the ciphertext. Rewrapping under a passphrase the operator supplies is the
    /// only version that is both restorable and worth encrypting.
    /// </remarks>
    public static byte[] WrapForExport(ReadOnlySpan<byte> masterKey, string passphrase)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var derived = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, CryptoEnvelopeConstants.KeyLength);

        var nonce = RandomNumberGenerator.GetBytes(CryptoEnvelopeConstants.NonceLength);
        var ciphertext = new byte[masterKey.Length];
        var tag = new byte[CryptoEnvelopeConstants.TagLength];

        using (var aes = new AesGcm(derived, CryptoEnvelopeConstants.TagLength))
        {
            aes.Encrypt(nonce, masterKey, ciphertext, tag, FileMagic);
        }

        CryptographicOperations.ZeroMemory(derived);

        var output = new byte[FileMagic.Length + 1 + 4 + SaltLength + CryptoEnvelopeConstants.NonceLength + ciphertext.Length + tag.Length];
        var span = output.AsSpan();
        FileMagic.CopyTo(span);
        span[FileMagic.Length] = ModePassphrase;
        BinaryPrimitives.WriteInt32BigEndian(span.Slice(FileMagic.Length + 1, 4), Pbkdf2Iterations);
        salt.CopyTo(span.Slice(FileMagic.Length + 5, SaltLength));
        nonce.CopyTo(span.Slice(FileMagic.Length + 5 + SaltLength, CryptoEnvelopeConstants.NonceLength));
        ciphertext.CopyTo(span[(FileMagic.Length + 5 + SaltLength + CryptoEnvelopeConstants.NonceLength)..]);
        tag.CopyTo(span[^CryptoEnvelopeConstants.TagLength..]);

        return output;
    }

    public static byte[] UnwrapFromExport(ReadOnlySpan<byte> wrapped, string passphrase)
    {
        if (wrapped.Length < FileMagic.Length + 5 + SaltLength + CryptoEnvelopeConstants.NonceLength + CryptoEnvelopeConstants.TagLength ||
            !wrapped[..FileMagic.Length].SequenceEqual(FileMagic) ||
            wrapped[FileMagic.Length] != ModePassphrase)
        {
            throw new InvalidOperationException("The archive's key blob is not a Compendio passphrase-wrapped master key.");
        }

        var iterations = BinaryPrimitives.ReadInt32BigEndian(wrapped.Slice(FileMagic.Length + 1, 4));
        var salt = wrapped.Slice(FileMagic.Length + 5, SaltLength);
        var nonce = wrapped.Slice(FileMagic.Length + 5 + SaltLength, CryptoEnvelopeConstants.NonceLength);
        var body = wrapped[(FileMagic.Length + 5 + SaltLength + CryptoEnvelopeConstants.NonceLength)..];
        var ciphertext = body[..^CryptoEnvelopeConstants.TagLength];
        var tag = body[^CryptoEnvelopeConstants.TagLength..];

        var derived = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase), salt, iterations, HashAlgorithmName.SHA256, CryptoEnvelopeConstants.KeyLength);

        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(derived, CryptoEnvelopeConstants.TagLength);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, FileMagic);
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new InvalidOperationException("Wrong passphrase, or the archive's key blob is damaged.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derived);
        }

        return plaintext;
    }

    private void EnsureLoaded()
    {
        if (_cached is not null)
        {
            return;
        }

        lock (_gate)
        {
            if (_cached is null)
            {
                LoadLocked();
            }
        }
    }

    private void LoadLocked()
    {
        if (!File.Exists(dataDirectory.MasterKeyFile))
        {
            _availability = SecureAvailability.MasterKeyMissing;
            return;
        }

        try
        {
            var stored = File.ReadAllBytes(dataDirectory.MasterKeyFile);
            _cached = Unprotect(stored);
            _availability = SecureAvailability.Available;
        }
        catch (Exception e) when (e is CryptographicException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // Explicitly not fatal. A key problem must not take the whole wiki down: non-secure
            // content keeps serving and secure scopes report unavailable.
            _availability = SecureAvailability.MasterKeyUnreadable;
            logger.LogError(
                "The instance master key at {Path} could not be read ({Reason}). Secure scopes will report " +
                "as unavailable; all other content is unaffected.",
                dataDirectory.MasterKeyFile,
                e.GetType().Name);
        }
    }

    private void WriteLocked(byte[] key)
    {
        Directory.CreateDirectory(dataDirectory.Keys);
        var protectedBytes = Protect(key);
        var temp = dataDirectory.MasterKeyFile + ".tmp";

        File.WriteAllBytes(temp, protectedBytes);
        RestrictToOwner(temp);

        if (File.Exists(dataDirectory.MasterKeyFile))
        {
            File.Replace(temp, dataDirectory.MasterKeyFile, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temp, dataDirectory.MasterKeyFile);
        }

        RestrictToOwner(dataDirectory.MasterKeyFile);
    }

    private byte[] Protect(byte[] key)
    {
        if (!string.IsNullOrEmpty(_security.MasterPassphrase))
        {
            return WrapForExport(key, _security.MasterPassphrase);
        }

        var body = key;
        if (OperatingSystem.IsWindows())
        {
            body = ProtectedData.Protect(key, optionalEntropy: FileMagic.ToArray(), DataProtectionScope.LocalMachine);
        }

        var output = new byte[FileMagic.Length + 1 + body.Length];
        FileMagic.CopyTo(output);
        output[FileMagic.Length] = ModePlatform;
        body.CopyTo(output, FileMagic.Length + 1);
        return output;
    }

    private byte[] Unprotect(byte[] stored)
    {
        if (stored.Length <= FileMagic.Length + 1 || !stored.AsSpan(0, FileMagic.Length).SequenceEqual(FileMagic))
        {
            throw new InvalidOperationException("keys/master.key is not a Compendio master key file.");
        }

        var mode = stored[FileMagic.Length];

        if (mode == ModePassphrase)
        {
            if (string.IsNullOrEmpty(_security.MasterPassphrase))
            {
                throw new InvalidOperationException(
                    "The master key is passphrase-protected but Security:MasterPassphrase is not set.");
            }

            return UnwrapFromExport(stored, _security.MasterPassphrase);
        }

        var body = stored[(FileMagic.Length + 1)..];

        if (OperatingSystem.IsWindows())
        {
            return ProtectedData.Unprotect(body, optionalEntropy: FileMagic.ToArray(), DataProtectionScope.LocalMachine);
        }

        return body;
    }

    /// <summary>
    /// Mode 0600 on Linux. On Windows the equivalent is the installer restricting the ACL of
    /// <c>keys/</c> to the service account — narrowing a file's ACL from inside the running service
    /// is more likely to lock the product out of its own key than to protect it.
    /// </summary>
    private static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

/// <summary>Envelope sizes, duplicated here so this file does not depend on the domain layout.</summary>
internal static class CryptoEnvelopeConstants
{
    internal const int KeyLength = Domain.Security.CryptoEnvelope.KeyLength;
    internal const int NonceLength = Domain.Security.CryptoEnvelope.NonceLength;
    internal const int TagLength = Domain.Security.CryptoEnvelope.TagLength;
}
