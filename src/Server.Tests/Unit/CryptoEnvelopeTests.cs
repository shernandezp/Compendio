using System.Security.Cryptography;
using System.Text;
using Compendio.Domain.Security;
using Shouldly;

namespace Compendio.Tests.Unit;

/// <summary>
/// The envelope format and its two guarantees: authenticated contents, and a binding to the path.
/// </summary>
public sealed class CryptoEnvelopeTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(CryptoEnvelope.KeyLength);
    private static readonly Guid KeyId = Guid.CreateVersion7();

    private const string Path = "IT/Secrets/runbook.md";
    private static readonly byte[] Plaintext = Encoding.UTF8.GetBytes("---\ntitle: Router\n---\n\nadmin / hunter2\n");

    [Fact]
    public void RoundTrips()
    {
        var envelope = CryptoEnvelope.Encrypt(Plaintext, Key, KeyId, Path);
        CryptoEnvelope.Decrypt(envelope, Key, Path).ShouldBe(Plaintext);
    }

    [Fact]
    public void IsSelfDescribing()
    {
        var envelope = CryptoEnvelope.Encrypt(Plaintext, Key, KeyId, Path);

        CryptoEnvelope.LooksEncrypted(envelope).ShouldBeTrue();
        CryptoEnvelope.TryReadHeader(envelope, out var header).ShouldBeTrue();

        header.KeyId.ShouldBe(KeyId);
        header.Version.ShouldBe(CryptoEnvelope.CurrentVersion);
        header.Algorithm.ShouldBe(CryptoEnvelope.AlgorithmAesGcm256);
    }

    [Fact]
    public void DoesNotLeakThePlaintext()
    {
        var envelope = CryptoEnvelope.Encrypt(Plaintext, Key, KeyId, Path);

        // The needle that must not appear anywhere in the ciphertext.
        Encoding.UTF8.GetString(envelope).ShouldNotContain("hunter2");
        Contains(envelope, "hunter2"u8).ShouldBeFalse();
        Contains(envelope, "Router"u8).ShouldBeFalse();
    }

    /// <summary>Criterion 11, second half: a one-byte flip is refused, not partially rendered.</summary>
    [Theory]
    [InlineData(40)]  // ciphertext
    [InlineData(20)]  // key id
    [InlineData(30)]  // nonce
    public void RejectsATamperedByte(int offset)
    {
        var envelope = CryptoEnvelope.Encrypt(Plaintext, Key, KeyId, Path);
        envelope[offset] ^= 0x01;

        Should.Throw<TamperedEnvelopeException>(() => CryptoEnvelope.Decrypt(envelope, Key, Path));
    }

    [Fact]
    public void RejectsATamperedTag()
    {
        var envelope = CryptoEnvelope.Encrypt(Plaintext, Key, KeyId, Path);
        envelope[^1] ^= 0xFF;

        Should.Throw<TamperedEnvelopeException>(() => CryptoEnvelope.Decrypt(envelope, Key, Path));
    }

    /// <summary>
    /// The point of binding the path into the AAD: a file cannot be moved between locations and
    /// still decrypt, so an attacker with write access to the folder cannot swap
    /// <c>Public/notes.md.enc</c> for <c>Secure/passwords.md.enc</c>.
    /// </summary>
    [Fact]
    public void RefusesToDecryptUnderADifferentPath()
    {
        var envelope = CryptoEnvelope.Encrypt(Plaintext, Key, KeyId, Path);

        Should.Throw<TamperedEnvelopeException>(() => CryptoEnvelope.Decrypt(envelope, Key, "Public/notes.md"));
    }

    [Fact]
    public void RefusesToDecryptUnderADifferentKey()
    {
        var envelope = CryptoEnvelope.Encrypt(Plaintext, Key, KeyId, Path);
        var otherKey = RandomNumberGenerator.GetBytes(CryptoEnvelope.KeyLength);

        Should.Throw<TamperedEnvelopeException>(() => CryptoEnvelope.Decrypt(envelope, otherKey, Path));
    }

    [Fact]
    public void RejectsATruncatedEnvelope()
    {
        var envelope = CryptoEnvelope.Encrypt(Plaintext, Key, KeyId, Path);
        Should.Throw<TamperedEnvelopeException>(() => CryptoEnvelope.Decrypt(envelope[..20], Key, Path));
    }

    [Fact]
    public void UsesAFreshNoncePerEncryption()
    {
        var first = CryptoEnvelope.Encrypt(Plaintext, Key, KeyId, Path);
        var second = CryptoEnvelope.Encrypt(Plaintext, Key, KeyId, Path);

        const int nonceStart = CryptoEnvelope.HeaderLength - CryptoEnvelope.NonceLength;

        first.ShouldNotBe(second);
        first[nonceStart..CryptoEnvelope.HeaderLength].ShouldNotBe(second[nonceStart..CryptoEnvelope.HeaderLength]);
    }

    [Fact]
    public void WrapsAndUnwrapsADataKey()
    {
        var master = RandomNumberGenerator.GetBytes(CryptoEnvelope.KeyLength);
        var dataKey = RandomNumberGenerator.GetBytes(CryptoEnvelope.KeyLength);

        var (wrapped, nonce) = CryptoEnvelope.WrapKey(dataKey, master, KeyId);

        CryptoEnvelope.UnwrapKey(wrapped, nonce, master, KeyId).ShouldBe(dataKey);
    }

    [Fact]
    public void RefusesToUnwrapADataKeyUnderTheWrongMasterKey()
    {
        var master = RandomNumberGenerator.GetBytes(CryptoEnvelope.KeyLength);
        var dataKey = RandomNumberGenerator.GetBytes(CryptoEnvelope.KeyLength);
        var (wrapped, nonce) = CryptoEnvelope.WrapKey(dataKey, master, KeyId);

        var wrongMaster = RandomNumberGenerator.GetBytes(CryptoEnvelope.KeyLength);

        Should.Throw<TamperedEnvelopeException>(() => CryptoEnvelope.UnwrapKey(wrapped, nonce, wrongMaster, KeyId));
    }

    [Fact]
    public void RefusesToUnwrapADataKeyUnderTheWrongKeyId()
    {
        var master = RandomNumberGenerator.GetBytes(CryptoEnvelope.KeyLength);
        var dataKey = RandomNumberGenerator.GetBytes(CryptoEnvelope.KeyLength);
        var (wrapped, nonce) = CryptoEnvelope.WrapKey(dataKey, master, KeyId);

        Should.Throw<TamperedEnvelopeException>(() => CryptoEnvelope.UnwrapKey(wrapped, nonce, master, Guid.CreateVersion7()));
    }

    private static bool Contains(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle) =>
        haystack.IndexOf(needle) >= 0;
}
