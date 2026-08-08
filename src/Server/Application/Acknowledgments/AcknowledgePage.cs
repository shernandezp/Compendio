using Common.Mediator;
using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Domain.Content;
using Compendio.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Compendio.Application.Acknowledgments;

/// <summary>
/// "I have read this."
/// </summary>
/// <remarks>
/// An explicit action, never inferred from a page view. An acknowledgment derived from somebody
/// having loaded a URL is worthless to the compliance case this feature exists for, and worse than
/// worthless if anybody relies on it.
/// </remarks>
public sealed record AcknowledgePageCommand(string Path) : ICommand<AcknowledgmentReceiptDto>;

public sealed record AcknowledgmentReceiptDto(string Path, Guid PageVersionId, DateTimeOffset AcknowledgedAt);

public sealed class AcknowledgePageHandler(
    ICompendioDbContext db,
    IPathPolicy paths,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser,
    AcknowledgmentRounds rounds,
    IClock clock) : IRequestHandler<AcknowledgePageCommand, AcknowledgmentReceiptDto>
{
    public async Task<AcknowledgmentReceiptDto> Handle(AcknowledgePageCommand request, CancellationToken cancellationToken = default)
    {
        var path = paths.Require(request.Path, PathKind.Page);

        // Read, not write: acknowledging is something a reader does, and requiring write access
        // would mean only editors could confirm they had read the policy.
        await permissions.RequireReadAsync(currentUser.Subject, path, cancellationToken);

        var page = await db.Pages.AsNoTracking().FirstOrDefaultAsync(p => p.Path == path.Value, cancellationToken)
                   ?? throw CompendioException.NotFound(path);

        if (!page.RequiresAcknowledgment)
        {
            throw CompendioException.BadRequest(ProblemCodes.AcknowledgmentNotRequired, path.Value);
        }

        var round = await rounds.CurrentAsync(page.Id, cancellationToken)
                    ?? throw CompendioException.BadRequest(ProblemCodes.AcknowledgmentNotRequired, path.Value);

        var now = clock.UtcNow;
        var userId = currentUser.UserId;

        var existing = await db.Acknowledgments
            .AsNoTracking()
            .FirstOrDefaultAsync(
                a => a.PageId == page.Id && a.UserId == userId && a.PageVersionId == round.PageVersionId,
                cancellationToken);

        if (existing is not null)
        {
            // Idempotent. Clicking twice is not a second acknowledgment, and refusing the second
            // click would be an error message for doing nothing wrong.
            return new AcknowledgmentReceiptDto(page.Path, existing.PageVersionId, existing.AcknowledgedAt);
        }

        db.Acknowledgments.Add(new Acknowledgment
        {
            Id = Guid.CreateVersion7(),
            PageId = page.Id,
            PageVersionId = round.PageVersionId,
            UserId = userId,
            AcknowledgedAt = now,
            Path = page.Path,
        });

        db.AuditLog.Add(new AuditEntry
        {
            Id = Guid.CreateVersion7(),
            At = now,
            ActorUserId = userId,
            Action = "acknowledgment.given",
            TargetType = "page",
            TargetPath = page.Path,
            AfterJson = $"{{\"versionId\":\"{round.PageVersionId}\"}}",
        });

        await db.SaveChangesAsync(cancellationToken);

        // The reminder has been dealt with. Leaving it would send the person back to a page to do
        // something they have just done.
        await ClearRemindersAsync(userId, page.Path, cancellationToken);

        return new AcknowledgmentReceiptDto(page.Path, round.PageVersionId, now);
    }

    private async Task ClearRemindersAsync(Guid userId, string path, CancellationToken cancellationToken) =>
        await db.Notifications
            .Where(n => n.UserId == userId
                        && n.TargetPath == path
                        && n.ReadAt == null
                        && (n.Kind == NotificationKind.AcknowledgmentRequested ||
                            n.Kind == NotificationKind.AcknowledgmentOverdue))
            .ExecuteDeleteAsync(cancellationToken);
}
