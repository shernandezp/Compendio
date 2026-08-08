using Compendio.Domain.Entities;

namespace Compendio.Application.Abstractions;

/// <summary>
/// The only way a notification row is created.
/// </summary>
/// <remarks>
/// <para>
/// Written by the change pipeline and the lifecycle scan; never by an endpoint. A notification is a
/// consequence of something happening, not of somebody asking for one.
/// </para>
/// <para>
/// Deduplication lives behind this seam rather than in each caller. The database enforces at most
/// one <em>unread</em> row per <c>(user, kind, target)</c>; this interface makes hitting that
/// constraint a no-op instead of an exception, so a caller in a loop does not have to know the rule.
/// </para>
/// </remarks>
public interface INotificationWriter
{
    /// <summary>Adds a row unless an unread one already covers the same thing.</summary>
    /// <returns>True when a row was written.</returns>
    Task<bool> NotifyAsync(
        Guid userId,
        NotificationKind kind,
        string targetPath,
        string? payloadJson = null,
        CancellationToken cancellationToken = default);

    /// <summary>Fans one notification out to several people in a single round trip.</summary>
    Task<int> NotifyManyAsync(
        IEnumerable<Guid> userIds,
        NotificationKind kind,
        string targetPath,
        string? payloadJson = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Withdraws unread rows of a kind for a target, for everyone.
    /// </summary>
    /// <remarks>
    /// The condition went away before anybody dealt with it — a stale page was reviewed, a page that
    /// required acknowledgment no longer does. Leaving the row would send someone to a page to do
    /// something that is already done.
    /// </remarks>
    Task<int> WithdrawAsync(NotificationKind kind, string targetPath, CancellationToken cancellationToken = default);
}
