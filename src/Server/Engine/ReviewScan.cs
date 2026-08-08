using Compendio.Application.Abstractions;
using Compendio.Application.Lifecycle;
using Compendio.Domain.Entities;
using Compendio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Compendio.Engine;

/// <summary>
/// Finds pages past their review date and tells their owners.
/// </summary>
/// <remarks>
/// <para>
/// A separate class from the timer that runs it, so a test can execute one pass deterministically
/// instead of waiting on a background service — which is the difference between a test that asserts
/// the behaviour and one that sleeps and hopes.
/// </para>
/// <para>
/// It notifies and nothing else. The banner, the dashboard and the report all compute staleness
/// themselves from <c>NextReviewDate</c>, so a scan that has not run yet costs a notification, never
/// a wrong screen.
/// </para>
/// </remarks>
public sealed class ReviewScan(
    IDbContextFactory<CompendioDbContext> dbFactory,
    OwnerResolver owners,
    INotificationWriter notifications,
    IClock clock,
    ILogger<ReviewScan> logger)
{
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = clock.UtcNow;

        var stale = await db.Pages
            .AsNoTracking()
            .Where(p => p.NextReviewDate != null && p.NextReviewDate < now && p.Owner != null)
            .Select(p => new { p.Path, p.Title, p.Owner })
            .ToListAsync(cancellationToken);

        if (stale.Count == 0)
        {
            return 0;
        }

        var directory = await owners.SnapshotAsync(cancellationToken);
        var notified = 0;
        var unassigned = 0;

        foreach (var page in stale)
        {
            var owner = directory.Resolve(page.Owner);
            if (owner is null)
            {
                // Reported by the stale report as unassigned. There is nobody to notify, and
                // notifying every admin about every ownerless page would be a daily flood.
                unassigned++;
                continue;
            }

            if (await notifications.NotifyAsync(
                    owner.Id,
                    NotificationKind.PageStale,
                    page.Path,
                    Payload.PageTitle(page.Title),
                    cancellationToken))
            {
                notified++;
            }
        }

        if (notified > 0 || unassigned > 0)
        {
            logger.LogInformation(
                "Review scan: {Stale} stale page(s), {Notified} owner(s) notified, {Unassigned} with no reachable owner.",
                stale.Count, notified, unassigned);
        }

        return notified;
    }

    /// <summary>
    /// Withdraws the stale notification for a page that is no longer stale.
    /// </summary>
    /// <remarks>
    /// Called after a review is confirmed. Leaving the row would send somebody to a page to do
    /// something that has just been done, which is how an inbox stops being worth opening.
    /// </remarks>
    public Task<int> WithdrawStaleAsync(string path, CancellationToken cancellationToken = default) =>
        notifications.WithdrawAsync(NotificationKind.PageStale, path, cancellationToken);
}

/// <summary>
/// The small JSON blobs notifications carry.
/// </summary>
/// <remarks>
/// Deliberately only what the inbox needs to render a line: a title, a language, an error summary.
/// Never page content, and never anything the recipient could not get by opening the page — the row
/// is re-checked against the evaluator on read, and a payload that carried content would sail past
/// that check.
/// </remarks>
public static class Payload
{
    public static string PageTitle(string title) =>
        System.Text.Json.JsonSerializer.Serialize(new { title });

    public static string Error(string summary) =>
        System.Text.Json.JsonSerializer.Serialize(new { error = summary });

    public static string Language(string title, string? lang) =>
        System.Text.Json.JsonSerializer.Serialize(new { title, lang });
}
