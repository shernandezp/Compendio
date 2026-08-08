using Common.Mediator;
using Compendio.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Compendio.Application.Notifications;

/// <summary>
/// Marks one row read.
/// </summary>
/// <remarks>
/// Scoped to the caller's own rows by the <c>UserId</c> predicate rather than by a lookup and a
/// check — an id belonging to somebody else simply matches nothing, which is the same answer as an
/// id that does not exist and leaks nothing either way.
/// </remarks>
public sealed record MarkNotificationReadCommand(Guid Id) : ICommand<int>;

public sealed class MarkNotificationReadHandler(
    ICompendioDbContext db,
    ICurrentUser currentUser,
    IClock clock) : IRequestHandler<MarkNotificationReadCommand, int>
{
    public async Task<int> Handle(MarkNotificationReadCommand request, CancellationToken cancellationToken = default)
    {
        var userId = currentUser.UserId;
        var now = clock.UtcNow;

        return await db.Notifications
            .Where(n => n.Id == request.Id && n.UserId == userId && n.ReadAt == null)
            .ExecuteUpdateAsync(n => n.SetProperty(x => x.ReadAt, now), cancellationToken);
    }
}

/// <summary>Clears the whole inbox. Reading is what lets a recurring condition notify again.</summary>
public sealed record MarkAllNotificationsReadCommand : ICommand<int>;

public sealed class MarkAllNotificationsReadHandler(
    ICompendioDbContext db,
    ICurrentUser currentUser,
    IClock clock) : IRequestHandler<MarkAllNotificationsReadCommand, int>
{
    public async Task<int> Handle(MarkAllNotificationsReadCommand request, CancellationToken cancellationToken = default)
    {
        var userId = currentUser.UserId;
        var now = clock.UtcNow;

        return await db.Notifications
            .Where(n => n.UserId == userId && n.ReadAt == null)
            .ExecuteUpdateAsync(n => n.SetProperty(x => x.ReadAt, now), cancellationToken);
    }
}
