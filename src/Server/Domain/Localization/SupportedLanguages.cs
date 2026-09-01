using System.Diagnostics.CodeAnalysis;

namespace Compendio.Domain.Localization;

/// <param name="Code">BCP-47 tag.</param>
/// <param name="EnglishName">Name in English, for the docs and the CLI.</param>
/// <param name="NativeName">Name in the language itself — what a picker shows.</param>
/// <param name="IsPseudo">A build-time pseudo-locale, offered only outside Production.</param>
public sealed record SupportedLanguage(string Code, string EnglishName, string NativeName, bool IsPseudo = false);

/// <summary>
/// The languages this instance offers, served from <c>GET /api/v1/languages</c>.
/// </summary>
/// <remarks>
/// The list lives here, in the domain, and every picker renders from it — so a locale file with no
/// entry is simply not offered, rather than half-working. Adding a language means adding a row
/// here, a catalog in <c>client/src/i18n/locales/</c>, a <c>.resx</c> satellite, and the culture to
/// <c>SatelliteResourceLanguages</c> in the csproj. That list is in <c>docs/translating.md</c>;
/// a language is data, not a pass over every screen.
/// </remarks>
public static class SupportedLanguages
{
    public const string Spanish = "es";
    public const string English = "en";

    /// <summary>
    /// The ~35 % text-expansion pseudo-locale. Reachable with <c>?lang=en-XA</c> outside
    /// Production, and the thing that finds a button that will overflow in Spanish.
    /// </summary>
    public const string Pseudo = "en-XA";

    /// <summary>Last-resort fallback when nothing in the resolution chain matched.</summary>
    public const string Fallback = English;

    public static IReadOnlyList<SupportedLanguage> All { get; } =
    [
        new(Spanish, "Spanish", "Español"),
        new(English, "English", "English"),
        new(Pseudo, "Pseudo-locale (testing)", "Ⓔⓝⓖⓛⓘⓢⓗ", IsPseudo: true),
    ];

    public static IReadOnlyList<SupportedLanguage> Shipping { get; } =
        All.Where(l => !l.IsPseudo).ToArray();

    public static bool IsSupported(string? code) => TryResolve(code, out _);

    /// <summary>
    /// Whether a page language is the reference text translations are made from.
    /// </summary>
    /// <remarks>
    /// English, or no language at all — a page that does not say what it is written in is treated as
    /// the original. Stated once, here, because two places ask it: the notifier that tells a
    /// translation's owner the source moved, and the banner on the translation itself. If they
    /// disagreed, the owner would be told about a change the page did not show, or the reverse.
    /// </remarks>
    public static bool IsReference(string? lang) =>
        string.IsNullOrWhiteSpace(lang) || string.Equals(lang.Trim(), English, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Matches with BCP-47 fallback: <c>es-MX</c> resolves to <c>es</c>. Case-insensitive, because
    /// <c>Accept-Language</c> and hand-typed <c>?lang=</c> values are not consistent about it.
    /// </summary>
    public static bool TryResolve(string? code, [NotNullWhen(true)] out string? resolved)
    {
        resolved = null;

        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var candidate = code.Trim();

        foreach (var language in All)
        {
            if (string.Equals(language.Code, candidate, StringComparison.OrdinalIgnoreCase))
            {
                resolved = language.Code;
                return true;
            }
        }

        var dash = candidate.IndexOf('-');
        if (dash <= 0)
        {
            return false;
        }

        var primary = candidate[..dash];
        foreach (var language in All)
        {
            if (string.Equals(language.Code, primary, StringComparison.OrdinalIgnoreCase))
            {
                resolved = language.Code;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The shipping language a translation may target, or null.
    /// </summary>
    /// <remarks>
    /// The pseudo-locale is excluded deliberately: it exists to find controls that will overflow in
    /// Spanish, and translating a policy into it would be a nonsense page in the content folder.
    /// </remarks>
    public static string? Normalize(string? code) =>
        TryResolve(code, out var resolved) && resolved != Pseudo ? resolved : null;

    /// <summary>The English name, for a prompt that has to name the target language to a model.</summary>
    public static string EnglishNameOf(string code) =>
        All.FirstOrDefault(l => l.Code == code)?.EnglishName ?? code;

    /// <summary>Resolves or falls back, never throws. The end of the resolution chain.</summary>
    public static string ResolveOrFallback(string? code, string instanceDefault) =>
        TryResolve(code, out var resolved) ? resolved
        : TryResolve(instanceDefault, out var fallback) ? fallback
        : Fallback;
}
