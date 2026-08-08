using Common.Mediator;
using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Domain.Content;
using Compendio.Domain.Lifecycle;

namespace Compendio.Application.Lifecycle;

/// <param name="Owner">
/// A <em>username</em>, not a display name. The dashboard and the notification fan-out both need a
/// user id, and free text resolves to nobody. Null leaves the current owner alone; an empty string
/// clears it.
/// </param>
/// <param name="NextReviewDate">Authoritative when supplied.</param>
public sealed record SetPageLifecycleCommand(
    string Path,
    string? Owner,
    int? ReviewIntervalDays,
    DateTimeOffset? NextReviewDate,
    bool? RequiresAcknowledgment) : ICommand<PageLifecycleDto>;

public sealed class SetPageLifecycleHandler(
    PageMetadataWriter writer,
    IPathPolicy paths,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser,
    Acknowledgments.AcknowledgmentRounds rounds,
    IClock clock,
    LifecycleProjection projection) : IRequestHandler<SetPageLifecycleCommand, PageLifecycleDto>
{
    public async Task<PageLifecycleDto> Handle(SetPageLifecycleCommand request, CancellationToken cancellationToken = default)
    {
        Validate(request);

        var path = paths.Require(request.Path, PathKind.Page);
        await permissions.RequireWriteAsync(currentUser.Subject, path, cancellationToken);

        var now = clock.UtcNow;

        var page = await writer.ApplyAsync(path, front => front with
        {
            Owner = request.Owner is null
                ? front.Owner
                : request.Owner.Trim() is { Length: > 0 } named ? named : null,
            ReviewIntervalDays = request.ReviewIntervalDays ?? front.ReviewIntervalDays,
            NextReviewDate = ResolveNextReview(request, front, now),
            RequiresAcknowledgment = request.RequiresAcknowledgment ?? front.RequiresAcknowledgment,
        }, currentUser.UserId, note: "lifecycle", cancellationToken);

        // Turning acknowledgment on is what opens the first round, and without this nobody can
        // acknowledge the page at all: the report and the acknowledge action both measure against a
        // round, and there would be none. The save path opens *later* rounds; this opens the first.
        await rounds.SynchronizeAsync(page, materialRevision: false, currentUser.UserId, cancellationToken);

        return await projection.BuildAsync(page, cancellationToken);
    }

    /// <summary>
    /// An explicitly supplied date always wins. A newly supplied interval restarts the clock from
    /// now — which is what "review this every 90 days" means when somebody types it into the panel.
    /// Supplying neither leaves whatever the page already declares.
    /// </summary>
    private static DateTimeOffset? ResolveNextReview(SetPageLifecycleCommand request, FrontMatter front, DateTimeOffset now) =>
        request.NextReviewDate is { } explicitDate
            ? explicitDate
            : request.ReviewIntervalDays is { } interval
                ? ReviewSchedule.AfterReview(interval, now)
                : ReviewSchedule.Next(front.NextReviewDate, front.ReviewIntervalDays, now);

    private static void Validate(SetPageLifecycleCommand request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(request.Path))
        {
            errors["path"] = ["required"];
        }

        if (request.ReviewIntervalDays is { } days && !ReviewSchedule.IsValidInterval(days))
        {
            errors["reviewIntervalDays"] = ["range"];
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }
    }
}
