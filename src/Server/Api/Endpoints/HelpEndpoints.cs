using Common.Mediator;
using Compendio.Application.Help;

namespace Compendio.Api.Endpoints;

/// <summary>The built-in user guide.</summary>
/// <remarks>
/// Authenticated, because the guide describes an instance somebody has to be signed in to use, and
/// an anonymous documentation endpoint is a fingerprinting surface for no benefit. The language is
/// the one the request already resolved — there is no <c>lang</c> parameter here, so help follows
/// the interface rather than drifting from it.
/// </remarks>
public static class HelpEndpoints
{
    public static void MapHelp(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/help").WithTags("Help").RequireAuthorization();

        group.MapGet("/", async (ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new GetHelpTopicsQuery(), ct)))
            .WithName("GetHelpTopics")
            .WithSummary("The guide's table of contents, filtered to what this person can act on.");

        group.MapGet("/{slug}", async (string slug, ISender sender, CancellationToken ct) =>
                await sender.Send(new GetHelpPageQuery(slug), ct) is { } page
                    ? Results.Ok(page)
                    : Results.NotFound())
            .WithName("GetHelpPage");
    }
}
