using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Compendio.Application.Notifications;

/// <summary>
/// Drops notifications whose target the recipient can no longer read, and deletes them.
/// </summary>
/// <remarks>
/// <para>
/// The re-check exists because the write and the read are separated in time. Somebody owns a page,
/// gets told it is stale, and is then removed from the group that could read the folder — the row is
/// still in their inbox, still naming a path. Filtering only at write time would leave that row as a
/// way to learn a page exists, which is the leak the tree and search go out of their way to avoid.
/// </para>
/// <para>
/// Deleting rather than hiding, because a row nobody may ever see again is not worth keeping and a
/// hidden row would come back the moment access is restored — reopening the leak on a delay.
/// </para>
/// </remarks>
public sealed class NotificationAccessFilter(
    ICompendioDbContext db,
    ReadablePages readablePages,
    ICurrentUser currentUser,
    ILogger<NotificationAccessFilter> logger)
{
    public async Task<IReadOnlyList<Notification>> KeepReadableAsync(
        IReadOnlyList<Notification> rows,
        CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0)
        {
            return rows;
        }

        var subject = currentUser.Subject;
        var kept = new List<Notification>(rows.Count);
        var orphaned = new List<Guid>();

        // One evaluator call per distinct path, not per row: an inbox of twenty notifications about
        // the same page is one question.
        var verdicts = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (!verdicts.TryGetValue(row.TargetPath, out var readable))
            {
                readable = await readablePages.CanReadAsync(subject, row.TargetPath, cancellationToken);
                verdicts[row.TargetPath] = readable;
            }

            if (readable)
            {
                kept.Add(row);
            }
            else
            {
                orphaned.Add(row.Id);
            }
        }

        if (orphaned.Count > 0)
        {
            await db.Notifications.Where(n => orphaned.Contains(n.Id)).ExecuteDeleteAsync(cancellationToken);
            logger.LogInformation(
                "Purged {Count} notification(s) whose target is no longer readable by their recipient.",
                orphaned.Count);
        }

        return kept;
    }
}
