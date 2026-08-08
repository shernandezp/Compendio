using Common.Mediator;
using Compendio.Application.Lifecycle;

namespace Compendio.Api.Endpoints;

/// <summary>
/// Review dates, the stale report and the dashboard. Binding and dispatch only.
/// </summary>
/// <remarks>
/// The path travels as a query parameter rather than in the route, for the reason the page module
/// documents: a catch-all segment has to be last in a template, so <c>/pages/{*path}/lifecycle</c>
/// never matches.
/// </remarks>
public static class LifecycleEndpoints
{
    public static void MapLifecycle(this IEndpointRouteBuilder app)
    {
        var pages = app.MapGroup("/api/v1/pages").RequireAuthorization().WithTags("Lifecycle");

        pages.MapPut("/lifecycle", async (SetLifecycleRequest request, ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new SetPageLifecycleCommand(
                    request.Path,
                    request.Owner,
                    request.ReviewIntervalDays,
                    request.NextReviewDate,
                    request.RequiresAcknowledgment), ct)))
            .WithName("SetPageLifecycle")
            .WithSummary("Sets owner, review interval, next review date and whether acknowledgment is required.");

        pages.MapPost("/review-confirm", async (ReviewConfirmRequest request, ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new ConfirmReviewedCommand(request.Path), ct)))
            .WithName("ConfirmReviewed")
            .WithSummary("Resets the review clock. The only thing that does — an ordinary edit does not.");

        var lifecycle = app.MapGroup("/api/v1/lifecycle").RequireAuthorization().WithTags("Lifecycle");

        lifecycle.MapGet("/stale", async (int? page, int? pageSize, string? owner, string? space, ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new GetStaleReportQuery(page ?? 1, pageSize ?? 50, owner, space), ct)))
            .WithName("GetStaleReport");

        lifecycle.MapGet("/stale.csv", async (string? owner, string? space, ISender sender, CancellationToken ct) =>
            {
                var file = await sender.Send(new ExportStaleReportCsvQuery(owner, space), ct);
                return Results.File(file.Content, "text/csv; charset=utf-8", file.FileName);
            })
            .WithName("ExportStaleReport");

        // Not under /admin: setting an owner needs write on the page, not the Admin role, and an
        // editor with no list of usernames would be typing one from memory.
        app.MapGet("/api/v1/users", async (ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new ListPickableUsersQuery(), ct)))
            .RequireAuthorization()
            .WithTags("Lifecycle")
            .WithName("ListPickableUsers")
            .WithSummary("Active accounts as id, username and display name — nothing else.");

        app.MapGet("/api/v1/dashboard", async (ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new GetDashboardQuery(), ct)))
            .RequireAuthorization()
            .WithTags("Lifecycle")
            .WithName("GetDashboard");
    }
}

public sealed record SetLifecycleRequest(
    string Path,
    string? Owner,
    int? ReviewIntervalDays,
    DateTimeOffset? NextReviewDate,
    bool? RequiresAcknowledgment);

public sealed record ReviewConfirmRequest(string Path);
