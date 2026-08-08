using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Compendio.Domain.Security;

public sealed class TamperedEnvelopeException(string message) : Exception(message);

/// <summary>
/// The on-disk format of an encrypted file.
/// </summary>
/// <remarks>
/// <code>
/// offset  size  field
/// 0       8     magic         "CMPDENC1"
/// 8       1     version       0x01
/// 9       1     alg           0x01 = AES-256-GCM
/// 10      16    key_id        data key that encrypted this file (big-endian GUID)
/// 26      12    nonce         random per encryption, never reused with a key
/// 38      N     ciphertext
/// 38+N    16    auth tag
/// </code>
/// <para>
/// Self-describing on purpose: a future version — or a panicked admin with the recovery tool —
/// must be able to read it without guessing. AES-256-GCM comes from the BCL, so there is no native
/// dependency to fight single-file publishing or the chiselled container.
/// </para>
/// <para>
/// The AAD binds the logical path, so an attacker with write access to the folder cannot swap
/// <c>Public/notes.md.enc</c> for <c>Secure/passwords.md.enc</c> and have the server decrypt it
/// into the wrong place.
/// </para>
/// </remarks>
public static class CryptoEnvelope
{
    public static ReadOnlySpan<byte> Magic => "CMPDENC1"u8;

    public const byte CurrentVersion = 0x01;
    public const byte AlgorithmAesGcm256 = 0x01;

    public const int MagicLength = 8;
    public const int KeyIdLength = 16;
    public const int NonceLength = 12;
    public const int TagLength = 16;
    public const int HeaderLength = MagicLength + 1 + 1 + KeyIdLength + NonceLength; // 38
    public const int KeyLength = 32;

    public const int MinimumLength = HeaderLength + TagLength;

    public readonly record struct Header(byte Version, byte Algorithm, Guid KeyId);

    public static bool LooksEncrypted(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= MinimumLength && bytes[..MagicLength].SequenceEqual(Magic);

    /// <summary>Reads the header without needing a key. What <c>doctor</c> uses.</summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> bytes, out Header header)
    {
        header = default;

        if (!LooksEncrypted(bytes))
        {
            return false;
        }

        var keyId = new Guid(bytes.Slice(MagicLength + 2, KeyIdLength), bigEndian: true);
        header = new Header(bytes[MagicLength], bytes[MagicLength + 1], keyId);
        return header is { Version: CurrentVersion, Algorithm: AlgorithmAesGcm256 };
    }

    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key, Guid keyId, string logicalPath)
    {
        ValidateKey(key);

        var output = new byte[HeaderLength + plaintext.Length + TagLength];
        var span = output.AsSpan();

        Magic.CopyTo(span);
        span[MagicLength] = CurrentVersion;
        span[MagicLength + 1] = AlgorithmAesGcm256;
        keyId.TryWriteBytes(span.Slice(MagicLength + 2, KeyIdLength), bigEndian: true, out _);

        var nonce = span.Slice(HeaderLength - NonceLength, NonceLength);
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = span.Slice(HeaderLength, plaintext.Length);
        var tag = span.Slice(HeaderLength + plaintext.Length, TagLength);

        Span<byte> aad = stackalloc byte[KeyIdLength + 1 + 256];
        var aadLength = BuildAad(keyId, CurrentVersion, logicalPath, ref aad);

        using var aes = new AesGcm(key, TagLength);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, aad[..aadLength]);

        return output;
    }

    /// <summary>
    /// Decrypts, authenticating the path binding. Throws <see cref="TamperedEnvelopeException"/>
    /// when the tag does not verify — a file that fails authentication is reported as tampered and
    /// is never partially rendered.
    /// </summary>
    public static byte[] Decrypt(ReadOnlySpan<byte> envelope, ReadOnlySpan<byte> key, string logicalPath)
    {
        ValidateKey(key);

        if (!TryReadHeader(envelope, out var header))
        {
            throw new TamperedEnvelopeException("The file is not a Compendio envelope, or its header is unreadable.");
        }

        var nonce = envelope.Slice(HeaderLength - NonceLength, NonceLength);
        var ciphertextLength = envelope.Length - HeaderLength - TagLength;
        if (ciphertextLength < 0)
        {
            throw new TamperedEnvelopeException("The envelope is truncated.");
        }

        var ciphertext = envelope.Slice(HeaderLength, ciphertextLength);
        var tag = envelope.Slice(HeaderLength + ciphertextLength, TagLength);

        Span<byte> aad = stackalloc byte[KeyIdLength + 1 + 256];
        var aadLength = BuildAad(header.KeyId, header.Version, logicalPath, ref aad);

        var plaintext = new byte[ciphertextLength];
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, aad[..aadLength]);
        }
        catch (CryptographicException e)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new TamperedEnvelopeException(
                "The file failed authentication: it was modified, truncated, moved between locations, or encrypted under a different key.")
            {
                Source = e.Source,
            };
        }

        return plaintext;
    }

    /// <summary>AAD = key_id ‖ version ‖ UTF-8 logical path.</summary>
    private static int BuildAad(Guid keyId, byte version, string logicalPath, ref Span<byte> buffer)
    {
        var pathBytes = Encoding.UTF8.GetByteCount(logicalPath);
        var required = KeyIdLength + 1 + pathBytes;

        if (buffer.Length < required)
        {
            buffer = new byte[required];
        }

        keyId.TryWriteBytes(buffer[..KeyIdLength], bigEndian: true, out _);
        buffer[KeyIdLength] = version;
        Encoding.UTF8.GetBytes(logicalPath, buffer.Slice(KeyIdLength + 1, pathBytes));
        return required;
    }

    private static void ValidateKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeyLength)
        {
            throw new ArgumentException($"A data key must be {KeyLength} bytes; got {key.Length}.", nameof(key));
        }
    }

    /// <summary>Wraps a data key under the master key. The AAD binds the key's own id.</summary>
    public static (byte[] Wrapped, byte[] Nonce) WrapKey(ReadOnlySpan<byte> dataKey, ReadOnlySpan<byte> masterKey, Guid keyId)
    {
        ValidateKey(masterKey);

        var nonce = new byte[NonceLength];
        RandomNumberGenerator.Fill(nonce);

        var wrapped = new byte[dataKey.Length + TagLength];
        Span<byte> aad = stackalloc byte[KeyIdLength + 4];
        keyId.TryWriteBytes(aad[..KeyIdLength], bigEndian: true, out _);
        BinaryPrimitives.WriteUInt32BigEndian(aad[KeyIdLength..], 0x4B455931); // "KEY1"

        using var aes = new AesGcm(masterKey, TagLength);
        aes.Encrypt(nonce, dataKey, wrapped.AsSpan(0, dataKey.Length), wrapped.AsSpan(dataKey.Length, TagLength), aad);

        return (wrapped, nonce);
    }

    public static byte[] UnwrapKey(ReadOnlySpan<byte> wrapped, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> masterKey, Guid keyId)
    {
        ValidateKey(masterKey);

        if (wrapped.Length <= TagLength)
        {
            throw new TamperedEnvelopeException("The wrapped data key is truncated.");
        }

        Span<byte> aad = stackalloc byte[KeyIdLength + 4];
        keyId.TryWriteBytes(aad[..KeyIdLength], bigEndian: true, out _);
        BinaryPrimitives.WriteUInt32BigEndian(aad[KeyIdLength..], 0x4B455931);

        var dataKey = new byte[wrapped.Length - TagLength];
        try
        {
            using var aes = new AesGcm(masterKey, TagLength);
            aes.Decrypt(nonce, wrapped[..dataKey.Length], wrapped[dataKey.Length..], dataKey, aad);
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(dataKey);
            throw new TamperedEnvelopeException("The scope's data key could not be unwrapped with this master key.");
        }

        return dataKey;
    }
}
