using Common.Mediator;
using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Compendio.Application.Lifecycle;

/// <summary>
/// Every page past its review date that the caller can read.
/// </summary>
/// <remarks>
/// A read surface, so it carries a leak-suite row: a restricted page is absent from this report for
/// an unauthorized user, and absent from its total as well.
/// </remarks>
/// <param name="Owner">Filter by the raw <c>owner</c> string. <c>"-"</c> means unassigned only.</param>
/// <param name="Space">Filter to a depth-1 folder.</param>
public sealed record GetStaleReportQuery(
    int Page = 1,
    int PageSize = 50,
    string? Owner = null,
    string? Space = null) : IQuery<PagedResult<StalePageDto>>;

public sealed class GetStaleReportHandler(
    ReadablePages readablePages,
    ICurrentUser currentUser,
    OwnerResolver owners,
    IClock clock) : IRequestHandler<GetStaleReportQuery, PagedResult<StalePageDto>>
{
    /// <summary>The sentinel the UI sends for "pages nobody owns".</summary>
    public const string Unassigned = "-";

    public async Task<PagedResult<StalePageDto>> Handle(GetStaleReportQuery request, CancellationToken cancellationToken = default)
    {
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);
        var now = clock.UtcNow;

        var directory = await owners.SnapshotAsync(cancellationToken);

        var query = await readablePages.QueryAsync(currentUser.Subject, cancellationToken);
        query = query.Where(p => p.NextReviewDate != null && p.NextReviewDate < now);

        if (request.Owner == Unassigned)
        {
            // "Unassigned" is the same question the report's flag answers: nobody reachable owns
            // this. A page naming somebody who has left is the interesting case, and filtering only
            // on an empty owner string would hide exactly those.
            var known = directory.All.Select(u => u.UserName.ToLowerInvariant()).ToList();

            query = query.Where(p => p.Owner == null || p.Owner == string.Empty || !known.Contains(p.Owner.ToLower()));
        }
        else if (!string.IsNullOrWhiteSpace(request.Owner))
        {
            query = query.Where(p => p.Owner == request.Owner);
        }

        if (!string.IsNullOrWhiteSpace(request.Space))
        {
            var prefix = request.Space.Trim().TrimEnd('/') + "/";
            query = query.Where(p => p.Path.StartsWith(prefix));
        }

        // Counted with the same predicate as the items, so "12 overdue" means twelve *you* can see.
        var total = await query.CountAsync(cancellationToken);

        var rows = await query
            .OrderBy(p => p.NextReviewDate)
            .ThenBy(p => p.Path)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var items = rows.Select(row => Map(row, directory, now)).ToArray();

        return new PagedResult<StalePageDto>(items, total, page, pageSize);
    }

    internal static StalePageDto Map(Page row, OwnerSnapshot directory, DateTimeOffset now)
    {
        var owner = directory.Resolve(row.Owner);

        return new StalePageDto(
            row.Path,
            row.Title,
            row.Owner,
            owner?.DisplayName,
            // An owner nobody matches is the interesting case, not an error: an SOP with no
            // reachable owner is precisely what this report exists to surface.
            Unassigned: owner is null,
            row.NextReviewDate,
            row.NextReviewDate is { } due ? (int)Math.Floor((now - due).TotalDays) : null,
            row.UpdatedAt);
    }
}
