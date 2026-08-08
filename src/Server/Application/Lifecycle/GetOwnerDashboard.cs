using Common.Mediator;
using Compendio.Application.Abstractions;
using Compendio.Application.Acknowledgments;
using Compendio.Application.Common;
using Compendio.Application.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Compendio.Application.Lifecycle;

/// <summary>
/// The landing screen: what you own, what of it has gone stale, and what you owe.
/// </summary>
/// <remarks>
/// Assembled from the same three sources the dedicated screens use rather than from queries of its
/// own, so the dashboard cannot say something different from the report it links to.
/// </remarks>
public sealed record GetDashboardQuery(int StaleLimit = 10, int NotificationLimit = 5) : IQuery<DashboardDto>;

public sealed class GetDashboardHandler(
    ICompendioDbContext db,
    ReadablePages readablePages,
    ICurrentUser currentUser,
    OwnerResolver owners,
    OutstandingAcknowledgments outstanding,
    NotificationAccessFilter notificationFilter,
    IClock clock) : IRequestHandler<GetDashboardQuery, DashboardDto>
{
    public async Task<DashboardDto> Handle(GetDashboardQuery request, CancellationToken cancellationToken = default)
    {
        var subject = currentUser.Subject;
        var now = clock.UtcNow;
        var userName = currentUser.UserName;

        var readable = await readablePages.QueryAsync(subject, cancellationToken);

        // "Mine" is by the owner string matching this account's username — the same resolution rule
        // the rest of the feature uses, applied in the other direction.
        var mine = string.IsNullOrEmpty(userName)
            ? readable.Where(_ => false)
            : readable.Where(p => p.Owner != null && p.Owner.ToLower() == userName.ToLower());

        var myPageCount = await mine.CountAsync(cancellationToken);

        var staleRows = await mine
            .Where(p => p.NextReviewDate != null && p.NextReviewDate < now)
            .OrderBy(p => p.NextReviewDate)
            .Take(Math.Clamp(request.StaleLimit, 1, 50))
            .ToListAsync(cancellationToken);

        var directory = await owners.SnapshotAsync(cancellationToken);
        var stale = staleRows.Select(row => GetStaleReportHandler.Map(row, directory, now)).ToArray();

        var userId = currentUser.UserId;
        var recentRows = await db.Notifications
            .AsNoTracking()
            .Where(n => n.UserId == userId)
            .OrderByDescending(n => n.CreatedAt)
            .Take(Math.Clamp(request.NotificationLimit, 1, 20))
            .ToListAsync(cancellationToken);

        var recent = await notificationFilter.KeepReadableAsync(recentRows, cancellationToken);

        var unread = await db.Notifications
            .AsNoTracking()
            .CountAsync(n => n.UserId == userId && n.ReadAt == null, cancellationToken);

        return new DashboardDto(
            stale,
            myPageCount,
            unread,
            recent.Select(ListNotificationsHandler.Map).ToArray(),
            await outstanding.ForAsync(subject, cancellationToken));
    }
}
