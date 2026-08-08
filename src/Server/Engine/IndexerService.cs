using Compendio.Application.Abstractions;
using Compendio.Domain.Content;
using Compendio.Domain.Entities;
using Compendio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Compendio.Engine;

/// <summary>
/// Drains <c>IndexQueue</c> into the FTS index.
/// </summary>
/// <remarks>
/// The queue is durable so that a crash mid-batch resumes rather than silently leaving stale rows.
/// A row that keeps failing is retried with backoff and eventually parked with its error recorded,
/// where <c>doctor</c> reports it — dropping it silently would produce a wiki whose search quietly
/// stops finding one folder.
/// </remarks>
public sealed class IndexerService(
    IDbContextFactory<CompendioDbContext> dbFactory,
    IServiceScopeFactory scopeFactory,
    ILogger<IndexerService> logger) : BackgroundService
{
    private const int MaxAttempts = 5;
    private const int BatchSize = 50;

    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A short grace period so the first reconciliation pass can enqueue before we start.
        await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await DrainAsync(stoppingToken);
                if (processed == 0)
                {
                    await Task.Delay(Idle, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogError(e, "The indexer hit an unexpected error; retrying.");
                await Task.Delay(Idle, stoppingToken);
            }
        }
    }

    private async Task<int> DrainAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // The index is scoped (it owns a DbContext); a background service is a singleton. Resolving
        // per batch rather than per item keeps the scope short without churning one per row.
        using var scope = scopeFactory.CreateScope();
        var index = scope.ServiceProvider.GetRequiredService<ISearchIndex>();

        var batch = await db.IndexQueue
            .Where(q => q.Attempts < MaxAttempts)
            .OrderBy(q => q.EnqueuedAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        if (batch.Count == 0)
        {
            return 0;
        }

        foreach (var item in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await ApplyAsync(db, index, item, cancellationToken);
                db.IndexQueue.Remove(item);
            }
            catch (Exception e)
            {
                item.Attempts++;
                item.LastError = $"{e.GetType().Name}: {e.Message}";

                if (item.Attempts >= MaxAttempts)
                {
                    logger.LogError(e, "Giving up on indexing '{Path}' after {Attempts} attempts.", item.Path, item.Attempts);
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return batch.Count;
    }

    private static async Task ApplyAsync(CompendioDbContext db, ISearchIndex index, IndexQueueItem item, CancellationToken cancellationToken)
    {
        // The queued page id is authoritative. A delete is queued after the page row is gone, so a
        // path lookup would find nothing — and PageText.Path holds the search-tokenized form of the
        // path ("IT VPN session"), so matching a real path against it never succeeds either. That
        // combination is how index rows for deleted pages survive and keep answering searches.
        var pageId = item.PageId
                     ?? await db.Pages
                         .AsNoTracking()
                         .Where(p => p.Path == item.Path)
                         .Select(p => (Guid?)p.Id)
                         .FirstOrDefaultAsync(cancellationToken);

        if (pageId is not { } id)
        {
            return;
        }

        var stillExists = await db.Pages.AsNoTracking().AnyAsync(p => p.Id == id, cancellationToken);

        if (item.Operation == IndexOperation.Delete || !stillExists)
        {
            await index.RemoveAsync(id, cancellationToken);
            return;
        }

        await index.UpsertAsync(id, cancellationToken);
    }
}

/// <summary>
/// Periodic housekeeping: history thinning, ACL tombstone expiry, and a reconciliation sweep.
/// </summary>
/// <remarks>
/// One timer rather than three services, because all three are cheap, none is urgent, and a single
/// place to look when housekeeping has not run is worth more than the separation.
/// </remarks>
public sealed class MaintenanceService(
    IServiceScopeFactory scopeFactory,
    IOptions<Hosting.Configuration.CompendioOptions> options,
    ILogger<MaintenanceService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Not on the first tick: startup is busy enough with migrations and reconciliation.
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        using var timer = new PeriodicTimer(Interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();

                await scope.ServiceProvider.GetRequiredService<IPageHistory>()
                    .ApplyRetentionAsync(stoppingToken);

                await ExpireAclTombstonesAsync(scope.ServiceProvider, stoppingToken);
                await PurgeNotificationsAsync(scope.ServiceProvider, stoppingToken);

                // Only the last 24 hours of AI usage is ever counted; the rest is kept for the admin
                // screen's "who spent it" and then dropped, so the table cannot grow forever.
                await scope.ServiceProvider.GetRequiredService<Application.Ai.AiBudget>()
                    .PruneAsync(stoppingToken);

                // The reconciliation sweep. The watcher is best-effort by design — it drops events
                // under load, misses everything that happens while the service is stopped, and is
                // unreliable over SMB — so "a full pass can always repair the drift" has to mean a
                // pass that actually runs without somebody restarting the service to get one.
                var report = await scope.ServiceProvider.GetRequiredService<Reconciler>()
                    .RunAsync(stoppingToken);

                if (report.FoundDrift)
                {
                    logger.LogInformation(
                        "The maintenance reconciliation pass repaired drift: {Added} added, {Updated} updated, {Removed} removed.",
                        report.Added, report.Updated, report.Removed);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Maintenance pass failed; it will run again in {Hours} h.", Interval.TotalHours);
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken))
            {
                return;
            }
        }
    }

    private async Task ExpireAclTombstonesAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<ICompendioDbContext>();
        var cutoff = DateTimeOffset.UtcNow.AddDays(-options.Value.Security.AclTombstoneDays);

        var expired = await db.AclNodes
            .Where(n => n.TombstonedAt != null && n.TombstonedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);

        if (expired > 0)
        {
            logger.LogInformation("Expired {Count} access-rule tombstone(s) past the retention window.", expired);
        }
    }

    /// <summary>
    /// Drops read notifications past the retention window.
    /// </summary>
    /// <remarks>
    /// Only read ones. An unread row is somebody's outstanding work — a policy still unacknowledged,
    /// a page still stale — and deleting it on a timer would quietly cancel the obligation instead
    /// of the reminder.
    /// </remarks>
    private async Task PurgeNotificationsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<ICompendioDbContext>();
        var cutoff = DateTimeOffset.UtcNow.AddDays(-options.Value.Lifecycle.NotificationRetentionDays);

        var purged = await db.Notifications
            .Where(n => n.ReadAt != null && n.CreatedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);

        if (purged > 0)
        {
            logger.LogInformation("Purged {Count} read notification(s) past the retention window.", purged);
        }
    }
}
