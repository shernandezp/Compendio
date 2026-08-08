using Compendio.Hosting.Configuration;
using Microsoft.Extensions.Options;

namespace Compendio.Engine;

/// <summary>
/// The timer that runs the lifecycle scans. Nothing else.
/// </summary>
/// <remarks>
/// <para>
/// A separate hosted service from <c>MaintenanceService</c> because the cadences differ by design:
/// housekeeping is six-hourly and invisible, while the review scan is daily and its output lands in
/// somebody's inbox. Running them together would mean either notifying four times a day or
/// reconciling four times less often.
/// </para>
/// <para>
/// The scans themselves live in <see cref="ReviewScan"/> and <see cref="AcknowledgmentScan"/>, so
/// they can be run directly by a test or by an admin action without a background service in the way.
/// </para>
/// </remarks>
public sealed class LifecycleService(
    IServiceScopeFactory scopeFactory,
    IOptions<CompendioOptions> options,
    ILogger<LifecycleService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hours = options.Value.Lifecycle.ReviewScanIntervalHours;
        if (hours <= 0)
        {
            logger.LogInformation("The review scan is disabled (Lifecycle:ReviewScanIntervalHours = 0).");
            return;
        }

        // Not on the first tick: startup is busy with migrations and reconciliation, and a
        // notification is never urgent enough to compete with getting the wiki serving.
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromHours(hours));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();

                await scope.ServiceProvider.GetRequiredService<ReviewScan>().RunAsync(stoppingToken);
                await scope.ServiceProvider.GetRequiredService<AcknowledgmentScan>().RunAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                // A failed scan is a missed notification, not lost data — every screen computes
                // staleness itself. Logging and trying again is the whole recovery story.
                logger.LogError(e, "The lifecycle scan failed; it will run again in {Hours} h.", hours);
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken))
            {
                return;
            }
        }
    }
}
