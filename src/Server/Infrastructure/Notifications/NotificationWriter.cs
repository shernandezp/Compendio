using Compendio.Application.Abstractions;
using Compendio.Domain.Entities;
using Compendio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Compendio.Infrastructure.Notifications;

/// <inheritdoc />
/// <remarks>
/// <para>
/// Deduplication is enforced by a filtered unique index on <c>(UserId, Kind, TargetPath)</c> where
/// <c>ReadAt IS NULL</c>, not by a read-then-write in here. Checking first and inserting second is a
/// race: the review scan and the change pipeline can both decide a page needs the same notification
/// at the same moment, and the loser would either crash or write a duplicate.
/// </para>
/// <para>
/// So the insert is attempted and a unique-constraint violation is treated as "somebody already
/// said this", which is exactly what it means.
/// </para>
/// </remarks>
public sealed class NotificationWriter(
    IDbContextFactory<CompendioDbContext> dbFactory,
    IClock clock,
    ILogger<NotificationWriter> logger) : INotificationWriter
{
    public async Task<bool> NotifyAsync(
        Guid userId,
        NotificationKind kind,
        string targetPath,
        string? payloadJson = null,
        CancellationToken cancellationToken = default) =>
        await NotifyManyAsync([userId], kind, targetPath, payloadJson, cancellationToken) > 0;

    public async Task<int> NotifyManyAsync(
        IEnumerable<Guid> userIds,
        NotificationKind kind,
        string targetPath,
        string? payloadJson = null,
        CancellationToken cancellationToken = default)
    {
        var recipients = userIds.Distinct().ToArray();
        if (recipients.Length == 0)
        {
            return 0;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // One query to skip the obvious duplicates, so the common case does not rely on exceptions.
        // The index is still the authority — this only keeps the happy path quiet.
        var alreadyUnread = await db.Notifications
            .AsNoTracking()
            .Where(n => n.Kind == kind && n.TargetPath == targetPath && n.ReadAt == null && recipients.Contains(n.UserId))
            .Select(n => n.UserId)
            .ToHashSetAsync(cancellationToken);

        var written = 0;
        var now = clock.UtcNow;

        foreach (var userId in recipients.Where(id => !alreadyUnread.Contains(id)))
        {
            db.Notifications.Add(new Notification
            {
                Id = Guid.CreateVersion7(),
                UserId = userId,
                Kind = kind,
                TargetPath = targetPath,
                PayloadJson = payloadJson,
                CreatedAt = now,
            });

            written++;
        }

        if (written == 0)
        {
            return 0;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return written;
        }
        catch (DbUpdateException e) when (IsUniqueViolation(e))
        {
            // Lost the race with another writer. The row it wrote says the same thing ours would
            // have, so there is nothing to repair and nothing to report.
            logger.LogDebug("A concurrent writer already recorded {Kind} for '{Path}'.", kind, targetPath);
            return 0;
        }
    }

    public async Task<int> WithdrawAsync(NotificationKind kind, string targetPath, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        return await db.Notifications
            .Where(n => n.Kind == kind && n.TargetPath == targetPath && n.ReadAt == null)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>SQLite reports a violated unique index as error 19 (constraint).</summary>
    private static bool IsUniqueViolation(DbUpdateException e) =>
        e.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteErrorCode: 19 };
}
