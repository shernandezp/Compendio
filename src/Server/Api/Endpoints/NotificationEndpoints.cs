using Common.Mediator;
using Compendio.Application.Notifications;

namespace Compendio.Api.Endpoints;

/// <summary>The signed-in person's inbox. Everything here is scoped to the caller by the handlers.</summary>
public static class NotificationEndpoints
{
    public static void MapNotifications(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/notifications").RequireAuthorization().WithTags("Notifications");

        group.MapGet("/", async (int? page, int? pageSize, bool? unreadOnly, ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(new ListNotificationsQuery(page ?? 1, pageSize ?? 25, unreadOnly ?? false), ct)))
            .WithName("ListNotifications");

        group.MapGet("/count", async (ISender sender, CancellationToken ct) =>
                Results.Ok(new { count = await sender.Send(new GetNotificationCountQuery(), ct) }))
            .WithName("GetNotificationCount")
            .WithSummary("Unread badge. Filtered by the same permission re-check as the list.");

        group.MapPost("/{id:guid}/read", async (Guid id, ISender sender, CancellationToken ct) =>
            {
                await sender.Send(new MarkNotificationReadCommand(id), ct);
                return Results.NoContent();
            })
            .WithName("MarkNotificationRead");

        group.MapPost("/read-all", async (ISender sender, CancellationToken ct) =>
            {
                await sender.Send(new MarkAllNotificationsReadCommand(), ct);
                return Results.NoContent();
            })
            .WithName("MarkAllNotificationsRead");
    }
}
