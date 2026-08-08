using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Domain.Entities;
using Compendio.Domain.Lifecycle;

namespace Compendio.Application.Lifecycle;

/// <summary>
/// Turns a page row into its lifecycle DTO, resolving the owner along the way.
/// </summary>
/// <remarks>
/// Shared by everything that reports lifecycle state so they cannot disagree about what "stale" or
/// "unassigned" means — the kind of divergence that ends with a banner and a report contradicting
/// each other on the same page.
/// </remarks>
public sealed class LifecycleProjection(OwnerResolver owners, IClock clock)
{
    public async Task<PageLifecycleDto> BuildAsync(Page page, CancellationToken cancellationToken = default)
    {
        var owner = (await owners.SnapshotAsync(cancellationToken)).Resolve(page.Owner);

        return new PageLifecycleDto(
            page.Path,
            page.Title,
            page.Owner,
            owner?.Id,
            owner?.DisplayName,
            page.ReviewIntervalDays,
            page.NextReviewDate,
            page.RequiresAcknowledgment,
            ReviewSchedule.IsStale(page.NextReviewDate, clock.UtcNow));
    }
}
