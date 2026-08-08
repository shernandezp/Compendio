using Compendio.Application.Abstractions;
using Compendio.Domain;
using Compendio.Domain.Localization;

namespace Compendio.Api.Common;

/// <summary>
/// Resolves the caller's language, once per request.
/// </summary>
/// <remarks>
/// <para>
/// The chain is: <c>?lang=</c> → profile preference → cookie → <c>Accept-Language</c> → instance
/// default → <c>en</c>. The same chain runs on the client, so an API error comes back in the
/// language the SPA is already rendering.
/// </para>
/// <para>
/// The design note lists the profile preference first, and this puts <c>?lang=</c> ahead of it
/// deliberately. The note's point is that a stored preference must beat the <em>browser</em> —
/// "the browser is in English but I want Spanish". A query parameter is not the browser: it is an
/// explicit, more recent choice by the same person, and it is how a link is shared in a particular
/// language and how <c>?lang=en-XA</c> exercises the pseudo-locale. With the preference winning,
/// that parameter would do nothing for any signed-in user, which is every user.
/// </para>
/// </remarks>
public sealed class RequestLanguageMiddleware(RequestDelegate next)
{
    public const string ItemKey = "compendio.language";

    public async Task InvokeAsync(HttpContext context, IInstanceSettings instance)
    {
        var language = Resolve(context, instance);
        context.Items[ItemKey] = language;

        if (context.Request.Query.TryGetValue("lang", out var requested) &&
            SupportedLanguages.TryResolve(requested.ToString(), out var resolved))
        {
            context.Response.Cookies.Append(CompendioConstants.LanguageCookieName, resolved, new CookieOptions
            {
                HttpOnly = false, // The SPA reads it to pick its own catalog.
                SameSite = SameSiteMode.Strict,
                IsEssential = true,
                MaxAge = TimeSpan.FromDays(365),
                Path = "/",
            });
        }

        await next(context);
    }

    private static string Resolve(HttpContext context, IInstanceSettings instance)
    {
        if (instance.ForceSingleLanguage)
        {
            return instance.DefaultLanguage;
        }

        // 1. Explicit query parameter — for sharing a link in a language, and for testing.
        if (context.Request.Query.TryGetValue("lang", out var query) &&
            SupportedLanguages.TryResolve(query.ToString(), out var fromQuery))
        {
            return fromQuery;
        }

        // 2. Profile preference, carried as a claim so this needs no database round trip. Beats the
        // browser, which is the case the design note cares about.
        var claim = context.User.FindFirst(CompendioClaims.PreferredLanguage)?.Value;
        if (SupportedLanguages.TryResolve(claim, out var fromClaim))
        {
            return fromClaim;
        }

        // 3. Cookie, set by 1 and 2.
        if (context.Request.Cookies.TryGetValue(CompendioConstants.LanguageCookieName, out var cookie) &&
            SupportedLanguages.TryResolve(cookie, out var fromCookie))
        {
            return fromCookie;
        }

        // 4. Accept-Language, with BCP-47 fallback: es-MX resolves to es.
        foreach (var candidate in ParseAcceptLanguage(context.Request.Headers.AcceptLanguage.ToString()))
        {
            if (SupportedLanguages.TryResolve(candidate, out var fromHeader))
            {
                return fromHeader;
            }
        }

        // 5 and 6. The instance default is the wizard's answer, not just what is in the config file.
        return SupportedLanguages.ResolveOrFallback(instance.DefaultLanguage, SupportedLanguages.Fallback);
    }

    private static IEnumerable<string> ParseAcceptLanguage(string header) =>
        header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part =>
            {
                var semicolon = part.IndexOf(';');
                var tag = semicolon < 0 ? part : part[..semicolon];
                var quality = 1.0;

                if (semicolon >= 0)
                {
                    var q = part[(semicolon + 1)..].Trim();
                    if (q.StartsWith("q=", StringComparison.OrdinalIgnoreCase) &&
                        double.TryParse(q[2..], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                    {
                        quality = parsed;
                    }
                }

                return (Tag: tag.Trim(), Quality: quality);
            })
            .Where(x => x.Quality > 0)
            .OrderByDescending(x => x.Quality)
            .Select(x => x.Tag);
}

public static class CompendioClaims
{
    public const string Role = "compendio:role";
    public const string PreferredLanguage = "compendio:lang";
    public const string DisplayName = "compendio:name";
}

public static class RequestLanguageExtensions
{
    public static string Language(this HttpContext context) =>
        context.Items.TryGetValue(RequestLanguageMiddleware.ItemKey, out var value) && value is string language
            ? language
            : SupportedLanguages.Fallback;
}
