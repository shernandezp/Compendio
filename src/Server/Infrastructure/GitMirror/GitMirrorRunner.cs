using Compendio.Application.Abstractions;
using Compendio.Domain.Entities;
using Compendio.Hosting;
using Compendio.Hosting.Configuration;
using Compendio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Compendio.Infrastructure.GitMirror;

/// <param name="Skipped">Nothing had changed, so nothing was pushed. Not a failure.</param>
public sealed record GitMirrorResult(bool Ok, bool Skipped, string Message, string? CommitSha);

/// <summary>
/// Commits the content folder and pushes it to a remote.
/// </summary>
/// <remarks>
/// <para>
/// Push-only. There is no pull and no merge back: a round trip would need a conflict model against a
/// live file watcher, which is a feature rather than a flag. The honest framing is "off-box history
/// and a readable copy", not "edit by pull request".
/// </para>
/// <para>
/// Secure scopes need no special handling here, and that is the point of the design: they are
/// already <c>.enc</c> envelopes on disk, so pushing the folder pushes ciphertext with nothing to
/// remember and nothing to forget. This is the case that makes the encryption worth building — the
/// moment content leaves the server is the moment somebody else's disk enters the threat model.
/// </para>
/// <para>
/// Compendio stores no git credential of its own. Authentication is whatever the operator's git
/// already has: an SSH agent, a credential helper, or a token inside the remote URL.
/// </para>
/// </remarks>
public sealed class GitMirrorRunner(
    GitCli git,
    DataDirectory dataDirectory,
    IDbContextFactory<CompendioDbContext> dbFactory,
    IUserDirectory users,
    INotificationWriter notifications,
    IOptions<CompendioOptions> options,
    IClock clock,
    ILogger<GitMirrorRunner> logger) : IGitMirror
{
    /// <summary>Two in a row before anybody is told. One failed push is usually a laptop on a train.</summary>
    private const int FailuresBeforeNotifying = 2;

    public Task<bool> IsGitAvailableAsync(CancellationToken cancellationToken = default) =>
        git.IsAvailableAsync(cancellationToken);

    public async Task<GitPushOutcome> PushAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunAsync(cancellationToken);
        return new GitPushOutcome(result.Ok, result.Skipped, result.Message, result.CommitSha);
    }

    public async Task<GitMirrorResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var settings = options.Value.GitMirror;

        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.RemoteUrl))
        {
            return new GitMirrorResult(false, Skipped: true, "disabled", null);
        }

        if (!await git.IsAvailableAsync(cancellationToken))
        {
            // Reported, not thrown. git being absent must leave every other feature working.
            return await RecordAsync(new GitMirrorResult(false, false, "git is not on PATH", null), cancellationToken);
        }

        var root = dataDirectory.Content;
        var timeout = TimeSpan.FromSeconds(Math.Max(10, settings.TimeoutSeconds));

        try
        {
            if (await EnsureRepositoryAsync(root, settings, timeout, cancellationToken) is { Ok: false } failure)
            {
                return await RecordAsync(new GitMirrorResult(false, false, failure.Message, null), cancellationToken);
            }

            await git.RunAsync(root, timeout, cancellationToken, "add", "--all");

            var status = await git.RunAsync(root, timeout, cancellationToken, "status", "--porcelain");
            var nothingToCommit = status.Ok && status.StandardOutput.Trim().Length == 0;

            if (!nothingToCommit)
            {
                var message = $"Compendio content sync {clock.UtcNow:yyyy-MM-dd HH:mm} UTC";
                var commit = await git.RunAsync(root, timeout, cancellationToken,
                    "-c", $"user.name={settings.CommitName}",
                    "-c", $"user.email={settings.CommitEmail}",
                    "commit", "--message", message);

                if (!commit.Ok)
                {
                    return await RecordAsync(new GitMirrorResult(false, false, commit.Message, null), cancellationToken);
                }
            }

            // Pushed even when there was nothing new to commit. "No local changes" does not mean the
            // remote is up to date: an operator who points the mirror at a fresh repository, or whose
            // last push failed, has a remote that is behind a local history nothing will ever add to.
            // A push with nothing to send is a cheap no-op; skipping it strands the remote for ever.
            var push = await git.RunAsync(root, timeout, cancellationToken, "push", "origin", settings.Branch);
            if (!push.Ok)
            {
                return await RecordAsync(new GitMirrorResult(false, false, push.Message, null), cancellationToken);
            }

            var sha = await git.RunAsync(root, timeout, cancellationToken, "rev-parse", "HEAD");

            return await RecordAsync(
                new GitMirrorResult(
                    true,
                    // Skipped describes the commit, not the run: nothing new was recorded, and the
                    // history this feature exists to provide is not padded with empty commits.
                    Skipped: nothingToCommit,
                    nothingToCommit ? "no changes" : "pushed",
                    sha.Ok ? sha.StandardOutput.Trim() : null),
                cancellationToken);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "The git mirror failed.");
            return await RecordAsync(new GitMirrorResult(false, false, e.Message, null), cancellationToken);
        }
    }

    /// <summary>
    /// Initializes the repository and points <c>origin</c> at the configured remote.
    /// </summary>
    /// <remarks>
    /// Idempotent, because the content folder may already be a repository — a user who cloned their
    /// wiki into place, or a previous run. Re-pointing <c>origin</c> on every run is deliberate: the
    /// configured remote is the authority, and an admin changing it in the UI should take effect.
    /// </remarks>
    private async Task<GitResult> EnsureRepositoryAsync(
        string root,
        GitMirrorOptions settings,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(Path.Combine(root, ".git")))
        {
            var init = await git.RunAsync(root, timeout, cancellationToken, "init", "--initial-branch", settings.Branch);
            if (!init.Ok)
            {
                return init;
            }
        }

        var remote = await git.RunAsync(root, timeout, cancellationToken, "remote", "get-url", "origin");

        var result = remote.Ok
            ? await git.RunAsync(root, timeout, cancellationToken, "remote", "set-url", "origin", settings.RemoteUrl!)
            : await git.RunAsync(root, timeout, cancellationToken, "remote", "add", "origin", settings.RemoteUrl!);

        return result;
    }

    /// <summary>
    /// Records the outcome in <c>Settings</c> and tells the admins about a run of failures.
    /// </summary>
    /// <remarks>
    /// Four values do not earn a table. The consecutive-failure counter is what keeps a flaky remote
    /// from writing an admin a notification every hour — the first failure is recorded and silent,
    /// the second is worth interrupting somebody for.
    /// </remarks>
    private async Task<GitMirrorResult> RecordAsync(GitMirrorResult result, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = clock.UtcNow;

        await SetAsync(db, SettingKeys.GitMirrorLastAttemptAt, now.ToString("O"), cancellationToken);

        if (result.Ok)
        {
            await SetAsync(db, SettingKeys.GitMirrorLastSuccessAt, now.ToString("O"), cancellationToken);
            await SetAsync(db, SettingKeys.GitMirrorLastError, string.Empty, cancellationToken);
            await SetAsync(db, SettingKeys.GitMirrorConsecutiveFailures, "0", cancellationToken);

            if (result.CommitSha is { Length: > 0 } sha)
            {
                await SetAsync(db, SettingKeys.GitMirrorLastCommit, sha, cancellationToken);
            }

            await db.SaveChangesAsync(cancellationToken);
            return result;
        }

        var failures = int.TryParse(await GetAsync(db, SettingKeys.GitMirrorConsecutiveFailures, cancellationToken), out var previous)
            ? previous + 1
            : 1;

        await SetAsync(db, SettingKeys.GitMirrorLastError, Truncate(result.Message), cancellationToken);
        await SetAsync(db, SettingKeys.GitMirrorConsecutiveFailures, failures.ToString(), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        if (failures == FailuresBeforeNotifying)
        {
            var admins = await users.ActiveAdminIdsAsync(cancellationToken);
            await notifications.NotifyManyAsync(
                admins, NotificationKind.GitMirrorFailed, string.Empty,
                Engine.Payload.Error(Truncate(result.Message)), cancellationToken);
        }

        logger.LogWarning("The git mirror failed ({Failures} in a row): {Message}", failures, result.Message);
        return result;
    }

    /// <summary>Bounded, because git's stderr on a failed push can be a wall of text.</summary>
    private static string Truncate(string message) => message.Length <= 500 ? message : message[..500];

    private static async Task<string?> GetAsync(CompendioDbContext db, string key, CancellationToken cancellationToken) =>
        await db.Settings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(cancellationToken);

    private async Task SetAsync(CompendioDbContext db, string key, string value, CancellationToken cancellationToken)
    {
        var existing = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);

        if (existing is null)
        {
            db.Settings.Add(new Setting { Key = key, Value = value, UpdatedAt = clock.UtcNow });
            return;
        }

        existing.Value = value;
        existing.UpdatedAt = clock.UtcNow;
    }
}
