using Common.Mediator;
using Compendio.Application.History;

namespace Compendio.Api.Endpoints;

public static class HistoryEndpoints
{
    public static void MapHistory(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1").RequireAuthorization().WithTags("History");

        // The path is a query parameter here for the same reason as in PageEndpoints: a catch-all
        // route cannot carry a suffix, and a page path contains slashes.
        group.MapGet("/versions", async (string path, ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new ListVersionsQuery(path), ct)))
            .WithName("ListVersions");

        group.MapGet("/versions/{id:guid}", async (Guid id, ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new GetVersionQuery(id), ct)))
            .WithName("GetVersion");

        group.MapGet("/diff", async (string path, Guid from, Guid to, ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new GetDiffQuery(path, from, to), ct)))
            .WithName("GetDiff")
            .WithSummary("Source diff for the admin, and a block-level rendered diff for everyone else.");

        group.MapPost("/versions/{id:guid}/restore", async (Guid id, RestoreRequest request, ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new RestoreVersionCommand(request.Path, id), ct)))
            .WithName("RestoreVersion")
            .WithSummary("Restores by writing a new version, so a mistaken restore is itself undoable.");
    }
}

public sealed record RestoreRequest(string Path);
