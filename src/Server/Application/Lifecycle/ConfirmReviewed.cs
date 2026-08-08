using Common.Mediator;
using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Domain.Content;
using Compendio.Domain.Entities;
using Compendio.Domain.Lifecycle;

namespace Compendio.Application.Lifecycle;

/// <summary>
/// "I have reviewed this page" — the only thing that resets the review clock.
/// </summary>
/// <remarks>
/// Deliberately not a side effect of saving. Fixing a typo is not a review, and if an ordinary edit
/// cleared the stale flag then the flag would mean "recently touched" rather than "recently checked"
/// — at which point the whole feature is lying to the person reading the banner.
/// </remarks>
public sealed record ConfirmReviewedCommand(string Path) : ICommand<PageLifecycleDto>;

public sealed class ConfirmReviewedHandler(
    PageMetadataWriter writer,
    IPathPolicy paths,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser,
    ICompendioDbContext db,
    INotificationWriter notifications,
    IClock clock,
    LifecycleProjection projection) : IRequestHandler<ConfirmReviewedCommand, PageLifecycleDto>
{
    public async Task<PageLifecycleDto> Handle(ConfirmReviewedCommand request, CancellationToken cancellationToken = default)
    {
        var path = paths.Require(request.Path, PathKind.Page);
        await permissions.RequireWriteAsync(currentUser.Subject, path, cancellationToken);

        var now = clock.UtcNow;

        var page = await writer.ApplyAsync(path, front => front with
        {
            // Measured from now, not from the date it was already overdue by: a page reviewed three
            // months late is due again a full interval from the review.
            NextReviewDate = ReviewSchedule.AfterReview(front.ReviewIntervalDays, now),
        }, currentUser.UserId, note: "review", cancellationToken);

        db.AuditLog.Add(new AuditEntry
        {
            Id = Guid.CreateVersion7(),
            At = now,
            ActorUserId = currentUser.UserId,
            Action = "lifecycle.reviewed",
            TargetType = "page",
            TargetPath = path.Value,
            AfterJson = page.NextReviewDate is { } due
                ? $"{{\"nextReviewDate\":\"{due:O}\"}}"
                : null,
        });

        await db.SaveChangesAsync(cancellationToken);

        // The reminder has been answered. Leaving it would send the owner back to a page to do
        // something they have just done, which is how an inbox stops being worth opening.
        await notifications.WithdrawAsync(NotificationKind.PageStale, path.Value, cancellationToken);

        return await projection.BuildAsync(page, cancellationToken);
    }
}
