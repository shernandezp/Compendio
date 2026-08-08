using Compendio.Hosting.Configuration;
using Compendio.Infrastructure.GitMirror;
using Microsoft.Extensions.Options;

namespace Compendio.Engine;

/// <summary>
/// The scheduled push. Does nothing at all when the mirror is disabled, which is the default.
/// </summary>
/// <remarks>
/// Returns immediately rather than ticking and skipping, so a disabled mirror costs one branch at
/// startup and no timer for the life of the process.
/// </remarks>
public sealed class GitMirrorService(
    IServiceScopeFactory scopeFactory,
    IOptions<CompendioOptions> options,
    ILogger<GitMirrorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value.GitMirror;

        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.RemoteUrl))
        {
            return;
        }

        // Late enough that reconciliation has settled: pushing a half-ingested content folder would
        // produce a commit that says something happened when nothing did.
        await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(5, settings.IntervalMinutes)));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var result = await scope.ServiceProvider.GetRequiredService<GitMirrorRunner>().RunAsync(stoppingToken);

                if (result.Ok && !result.Skipped)
                {
                    logger.LogInformation("Pushed the content folder to the mirror ({Commit}).", result.CommitSha?[..7]);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                // The runner records and notifies; anything reaching here is a bug in that path, and
                // it must still not take the service down.
                logger.LogError(e, "The git mirror pass threw.");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken))
            {
                return;
            }
        }
    }
}
