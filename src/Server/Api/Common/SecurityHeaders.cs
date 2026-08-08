using Compendio.Hosting.Configuration;
using Microsoft.Extensions.Options;

namespace Compendio.Api.Common;

/// <summary>
/// The Content-Security-Policy and the headers around it.
/// </summary>
/// <remarks>
/// <para>
/// Concrete, because "add security headers" is not implementable. <c>script-src 'self'</c> with no
/// inline script at all — the pre-mount theme paint that stops a dark machine flashing white is a
/// <c>&lt;style&gt;</c> block, precisely so this line can stay as it is.
/// </para>
/// <para>
/// <c>style-src</c> is the one that needs thought. Mermaid injects styles at render time, so the
/// choice was a nonce, a hash, or a sandboxed iframe. This ships the nonce: it is generated per
/// response, handed to the SPA through a meta tag, and applied to Mermaid's injected style element.
/// <c>'unsafe-inline'</c> was ruled out — it would defeat the policy for every other style on the
/// page in order to accommodate one library.
/// </para>
/// </remarks>
public sealed class SecurityHeadersMiddleware(RequestDelegate next, IOptions<CompendioOptions> options)
{
    public const string NonceItemKey = "compendio.csp-nonce";

    /// <summary>
    /// The token <c>index.html</c> carries where the per-response nonce goes.
    /// </summary>
    /// <remarks>
    /// The SPA shell is a built artifact, so the nonce cannot be baked into it — it is substituted
    /// on the way out. A nonce that is generated and never delivered is the same as no nonce at all,
    /// and the symptom is a strict CSP that silently blocks the theme paint, Mantine's variables and
    /// every Mermaid diagram.
    /// </remarks>
    public const string NoncePlaceholder = "__CSP_NONCE__";

    private readonly SecurityOptions _security = options.Value.Security;

    public async Task InvokeAsync(HttpContext context)
    {
        var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        context.Items[NonceItemKey] = nonce;

        var headers = context.Response.Headers;

        headers["Content-Security-Policy"] = string.Join("; ",
        [
            "default-src 'self'",
            "script-src 'self'",
            $"style-src 'self' 'nonce-{nonce}'",
            // Split from style-src on purpose. A nonce can only be carried by a <style> element, so
            // `style-src` alone also blocks every `style="…"` attribute — which is how Mantine sets
            // its CSS variables and how any inline layout value is expressed. Naming the two
            // separately keeps the strict rule where it matters, on injected stylesheets, without
            // widening the policy for them: `'unsafe-inline'` here cannot introduce a stylesheet,
            // and page content reaches the browser with its style attributes already stripped by
            // the sanitizer.
            $"style-src-elem 'self' 'nonce-{nonce}'",
            "style-src-attr 'unsafe-inline'",
            "img-src 'self' data: blob:",
            "font-src 'self' data:",
            "connect-src 'self'",
            "object-src 'none'",
            "frame-ancestors 'none'",
            "base-uri 'self'",
            "form-action 'self'",
            "worker-src 'self' blob:",
        ]);

        headers["X-Content-Type-Options"] = "nosniff";
        headers["Referrer-Policy"] = "same-origin";
        headers["X-Frame-Options"] = "DENY";
        headers["Permissions-Policy"] = "camera=(self), microphone=(), geolocation=(), interest-cohort=()";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";

        if (_security.RequireHttps)
        {
            headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
        }

        // Nearly everything this API returns depends on who is asking, so caching it is a leak
        // waiting for a shared proxy. The SPA's own static assets are served before this runs.
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            headers.CacheControl = "no-store, no-cache, must-revalidate";
            headers.Pragma = "no-cache";
        }

        await next(context);
    }
}

/// <summary>
/// Serves the SPA shell with this response's CSP nonce substituted into it.
/// </summary>
/// <remarks>
/// <para>
/// This replaces <c>MapFallbackToFile</c>, which serves <c>index.html</c> as a static file and so
/// cannot carry anything that changes per response. The shell needs the nonce twice: on its own
/// pre-mount theme <c>&lt;style&gt;</c> block, and in a meta tag the client reads to hand to Mantine
/// and to the style element Mermaid injects into a rendered diagram.
/// </para>
/// <para>
/// The file is read once and held; the substitution is a string replace on a few kilobytes. The
/// response is <c>no-store</c> because its body is different every time and a cached copy would
/// carry a nonce that no longer matches the header.
/// </para>
/// </remarks>
public sealed class SpaShell(IWebHostEnvironment environment, ILogger<SpaShell> logger)
{
    private string? _template;

    public async Task WriteAsync(HttpContext context)
    {
        var template = _template ??= Load();

        if (template.Length == 0)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var nonce = context.Items[SecurityHeadersMiddleware.NonceItemKey] as string ?? string.Empty;

        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";

        await context.Response.WriteAsync(
            template.Replace(SecurityHeadersMiddleware.NoncePlaceholder, nonce, StringComparison.Ordinal),
            context.RequestAborted);
    }

    private string Load()
    {
        var file = environment.WebRootFileProvider.GetFileInfo("index.html");

        if (!file.Exists)
        {
            logger.LogError(
                "wwwroot/index.html is missing, so the site cannot be served. The API will still answer. " +
                "This means the client build did not reach the publish output.");
            return string.Empty;
        }

        using var stream = file.CreateReadStream();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
