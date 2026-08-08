using Common.Mediator;
using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Compendio.Application.Notifications;

/// <summary>
/// The signed-in person's inbox.
/// </summary>
/// <remarks>
/// A read surface, and a subtle one: rows are written when something happens and read later, so
/// access can change in between. Every row's target is re-checked against the evaluator here, and
/// one that is no longer readable is dropped from the response <em>and</em> deleted — otherwise a
/// notification becomes a way to learn that a page exists.
/// </remarks>
public sealed record ListNotificationsQuery(int Page = 1, int PageSize = 25, bool UnreadOnly = false)
    : IQuery<PagedResult<NotificationDto>>;

public sealed class ListNotificationsHandler(
    ICompendioDbContext db,
    ICurrentUser currentUser,
    NotificationAccessFilter filter) : IRequestHandler<ListNotificationsQuery, PagedResult<NotificationDto>>
{
    public async Task<PagedResult<NotificationDto>> Handle(ListNotificationsQuery request, CancellationToken cancellationToken = default)
    {
        if (!currentUser.IsAuthenticated)
        {
            return PagedResult<NotificationDto>.Empty(request.Page, request.PageSize);
        }

        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);
        var userId = currentUser.UserId;

        var query = db.Notifications.AsNoTracking().Where(n => n.UserId == userId);
        if (request.UnreadOnly)
        {
            query = query.Where(n => n.ReadAt == null);
        }

        var rows = await query
            .OrderByDescending(n => n.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var visible = await filter.KeepReadableAsync(rows, cancellationToken);

        // The total is recomputed from what survived rather than counted in SQL: the permission
        // re-check cannot be expressed in this query, and a total that counted rows the response
        // then dropped would be a count of pages the caller cannot see.
        var total = await query.CountAsync(cancellationToken) - (rows.Count - visible.Count);

        return new PagedResult<NotificationDto>(visible.Select(Map).ToArray(), Math.Max(total, visible.Count), page, pageSize);
    }

    internal static NotificationDto Map(Notification n) =>
        new(n.Id, n.Kind, n.TargetPath, n.PayloadJson, n.CreatedAt, n.ReadAt);
}

/// <summary>The unread badge. Same filter as the list — a count that leaks is still a leak.</summary>
public sealed record GetNotificationCountQuery : IQuery<int>;

public sealed class GetNotificationCountHandler(
    ICompendioDbContext db,
    ICurrentUser currentUser,
    NotificationAccessFilter filter) : IRequestHandler<GetNotificationCountQuery, int>
{
    /// <summary>
    /// Capped, because the badge is a badge. Beyond this the UI says "99+" and nobody is worse off,
    /// while the permission re-check stays bounded instead of walking an unbounded inbox.
    /// </summary>
    private const int Cap = 100;

    public async Task<int> Handle(GetNotificationCountQuery request, CancellationToken cancellationToken = default)
    {
        if (!currentUser.IsAuthenticated)
        {
            return 0;
        }

        var userId = currentUser.UserId;
        var rows = await db.Notifications
            .AsNoTracking()
            .Where(n => n.UserId == userId && n.ReadAt == null)
            .OrderByDescending(n => n.CreatedAt)
            .Take(Cap)
            .ToListAsync(cancellationToken);

        return (await filter.KeepReadableAsync(rows, cancellationToken)).Count;
    }
}
