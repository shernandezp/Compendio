using Compendio.Application.Abstractions;
using Microsoft.AspNetCore.DataProtection;

namespace Compendio.Infrastructure.Common;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>
/// Small secrets that live in the database. Nothing to do with content encryption.
/// </summary>
/// <remarks>
/// The key ring behind this must persist to <c>&lt;data&gt;/keys/dataprotection</c>. When it does
/// not — a service account with no home directory, a container running as a user without one — the
/// framework silently falls back to an in-memory ring and every restart signs every user out. It
/// shows up only in the deployed configurations, never in development, so there is a test.
/// </remarks>
public sealed class SecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("Compendio.Secrets.v1");

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public bool TryUnprotect(string protectedValue, out string plaintext)
    {
        try
        {
            plaintext = _protector.Unprotect(protectedValue);
            return true;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            plaintext = string.Empty;
            return false;
        }
    }
}
