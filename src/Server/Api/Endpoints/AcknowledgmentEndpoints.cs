using Common.Mediator;
using Compendio.Application.Acknowledgments;

namespace Compendio.Api.Endpoints;

/// <summary>
/// Read-acknowledgment: giving one, and reporting on them.
/// </summary>
/// <remarks>
/// Giving one needs <c>read</c>; reporting needs <c>manage</c> on the folder. The handlers enforce
/// both — a list of who has and has not done something is different information from the page.
/// </remarks>
public static class AcknowledgmentEndpoints
{
    public static void MapAcknowledgments(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/acknowledgments").RequireAuthorization().WithTags("Acknowledgments");

        group.MapPost("/", async (AcknowledgeRequest request, ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new AcknowledgePageCommand(request.Path), ct)))
            .WithName("AcknowledgePage")
            .WithSummary("Explicit confirmation of reading. Never inferred from a page view.");

        group.MapGet("/page", async (string path, ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new GetAcknowledgmentReportQuery(path), ct)))
            .WithName("GetAcknowledgmentReport");

        group.MapGet("/user/{userId:guid}", async (Guid userId, ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new GetUserAcknowledgmentsQuery(userId), ct)))
            .WithName("GetUserAcknowledgments");

        group.MapGet("/mine", async (ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new GetUserAcknowledgmentsQuery(), ct)))
            .WithName("GetMyAcknowledgments");

        group.MapGet("/report.csv", async (string path, ISender sender, CancellationToken ct) =>
            {
                var file = await sender.Send(new ExportAcknowledgmentsCsvQuery(path), ct);
                return Results.File(file.Content, "text/csv; charset=utf-8", file.FileName);
            })
            .WithName("ExportAcknowledgmentReport");
    }
}

public sealed record AcknowledgeRequest(string Path);
