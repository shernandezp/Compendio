using Compendio.Application.Abstractions;
using Compendio.Domain.Content;
using Compendio.Domain.Entities;
using Compendio.Domain.Security;
using Compendio.Hosting.Configuration;
using Compendio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Compendio.Engine;

/// <summary>
/// Asks the people who can read a policy to confirm they have, and chases the ones who have not.
/// </summary>
/// <remarks>
/// <para>
/// "Required" is the evaluator's answer, asked per active account, not a role or a group guess. A
/// Reader restricted out of the folder owes nothing, and a reminder telling them to read a page they
/// cannot open would be worse than no reminder.
/// </para>
/// <para>
/// Two kinds fall out of one pass because they share every input: <see cref="NotificationKind.
/// AcknowledgmentRequested"/> when the round is fresh, and <see cref="NotificationKind.
/// AcknowledgmentOverdue"/> once it has been open longer than the configured window. The unread
/// dedup index keeps a person from collecting one of each every night.
/// </para>
/// </remarks>
public sealed class AcknowledgmentScan(
    IDbContextFactory<CompendioDbContext> dbFactory,
    IUserDirectory users,
    IPermissionEvaluator permissions,
    INotificationWriter notifications,
    IOptions<CompendioOptions> options,
    IClock clock,
    ILogger<AcknowledgmentScan> logger)
{
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var pages = await db.Pages
            .AsNoTracking()
            .Where(p => p.RequiresAcknowledgment)
            .Select(p => new { p.Id, p.Path, p.Title })
            .ToListAsync(cancellationToken);

        if (pages.Count == 0)
        {
            return 0;
        }

        var pageIds = pages.Select(p => p.Id).ToList();

        var rounds = (await db.AcknowledgmentRounds
                .AsNoTracking()
                .Where(r => pageIds.Contains(r.PageId))
                .ToListAsync(cancellationToken))
            .GroupBy(r => r.PageId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.OpenedAt).First());

        // A set of tuples rather than a list of rows: the loop below is pages × accounts, and a
        // linear scan of every acknowledgment inside it turns a nightly pass over a few hundred
        // people into something quadratic for no reason.
        var given = (await db.Acknowledgments
                .AsNoTracking()
                .Where(a => pageIds.Contains(a.PageId))
                .Select(a => new { a.PageId, a.UserId, a.PageVersionId })
                .ToListAsync(cancellationToken))
            .Select(a => (a.PageId, a.UserId, a.PageVersionId))
            .ToHashSet();

        var accounts = await users.ActiveUsersAsync(cancellationToken);
        var overdueBefore = clock.UtcNow.AddDays(-options.Value.Lifecycle.AcknowledgmentDueDays);
        var written = 0;

        foreach (var page in pages)
        {
            if (!rounds.TryGetValue(page.Id, out var round))
            {
                continue;
            }

            var path = ContentPath.FromTrusted(page.Path);
            var kind = round.OpenedAt < overdueBefore
                ? NotificationKind.AcknowledgmentOverdue
                : NotificationKind.AcknowledgmentRequested;

            foreach (var account in accounts)
            {
                if (given.Contains((page.Id, account.Id, round.PageVersionId)))
                {
                    continue;
                }

                var subject = await users.SubjectAsync(account.Id, cancellationToken);
                if (subject is null ||
                    await permissions.EffectiveAsync(subject.Value.Subject, path, cancellationToken) < PermissionLevel.Read)
                {
                    continue;
                }

                if (await notifications.NotifyAsync(account.Id, kind, page.Path, Payload.PageTitle(page.Title), cancellationToken))
                {
                    written++;
                }
            }
        }

        if (written > 0)
        {
            logger.LogInformation("Acknowledgment scan wrote {Count} reminder(s).", written);
        }

        return written;
    }
}
