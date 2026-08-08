using Common.Mediator;
using Compendio.Application.Abstractions;
using Compendio.Application.Admin;
using Compendio.Application.Common;
using Compendio.Domain.Localization;

namespace Compendio.Api.Endpoints;

/// <summary>Languages, about, templates, and the two probes.</summary>
public static class MetaEndpoints
{
    public static void MapMeta(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1").WithTags("Meta");

        // The pickers render from this, so a locale file with no entry here is simply not offered
        // rather than half-working.
        group.MapGet("/languages", (IWebHostEnvironment environment) =>
                Results.Ok((environment.IsProduction() ? SupportedLanguages.Shipping : SupportedLanguages.All)
                    .Select(l => new LanguageDto(l.Code, l.EnglishName, l.NativeName))))
            .AllowAnonymous()
            .WithName("GetLanguages");

        group.MapGet("/about", async (ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new GetAboutQuery(), ct)))
            .AllowAnonymous()
            .WithName("GetAbout")
            .WithSummary("Version and the AGPL §5d notice, which the footer shows.");

        group.MapGet("/templates", async (ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new GetTemplatesQuery(), ct)))
            .RequireAuthorization()
            .WithName("GetTemplates");
    }

    /// <summary>
    /// Liveness and readiness.
    /// </summary>
    /// <remarks>
    /// <c>/health</c> answers as soon as the process is up — it is what the container HEALTHCHECK
    /// and a load balancer use, and making it depend on the index would restart a healthy container
    /// during a rebuild. <c>/ready</c> reports the index state, which is information, not a verdict.
    /// </remarks>
    public static void MapProbes(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
            .AllowAnonymous()
            .ExcludeFromDescription();

        app.MapGet("/ready", async (ISearchIndex index, CancellationToken ct) =>
            {
                var status = await index.StatusAsync(ct);
                return Results.Ok(new
                {
                    status = status.IsReady ? "ready" : status.State,
                    index = status.State,
                    queueDepth = status.QueueDepth,
                    percentComplete = status.PercentComplete,
                });
            })
            .AllowAnonymous()
            .ExcludeFromDescription();
    }
}
