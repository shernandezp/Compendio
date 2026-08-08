using Common.Mediator;
using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Domain.Security;
using Compendio.Hosting.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Compendio.Application.Acknowledgments;

/// <summary>
/// What one person owes, and what they have already confirmed.
/// </summary>
/// <remarks>
/// <para>
/// Callers may ask about themselves; asking about somebody else needs the <c>Admin</c> role. "What
/// has Ana not read yet" is a management question, and answering it for everybody would turn the
/// inbox into a compliance dashboard on colleagues.
/// </para>
/// <para>
/// A read surface either way: the list is filtered to pages the <em>subject of the report</em> can
/// read, so it never becomes a way for an admin-less caller to enumerate paths.
/// </para>
/// </remarks>
public sealed record GetUserAcknowledgmentsQuery(Guid? UserId = null) : IQuery<IReadOnlyList<AcknowledgmentTaskDto>>;

public sealed class GetUserAcknowledgmentsHandler(
    ICurrentUser currentUser,
    IUserDirectory users,
    OutstandingAcknowledgments outstanding) : IRequestHandler<GetUserAcknowledgmentsQuery, IReadOnlyList<AcknowledgmentTaskDto>>
{
    public async Task<IReadOnlyList<AcknowledgmentTaskDto>> Handle(
        GetUserAcknowledgmentsQuery request,
        CancellationToken cancellationToken = default)
    {
        var targetId = request.UserId ?? currentUser.UserId;

        if (targetId != currentUser.UserId && currentUser.Role != UserRole.Admin)
        {
            throw new CompendioException(ProblemCodes.PageForbidden, StatusCodes.Status403Forbidden);
        }

        var subject = await users.SubjectAsync(targetId, cancellationToken);
        if (subject is null)
        {
            return [];
        }

        return await outstanding.ForAsync(subject.Value.Subject, cancellationToken);
    }
}

/// <summary>
/// The pages one subject still owes an acknowledgment for.
/// </summary>
/// <remarks>
/// Shared by the dashboard, this query and the overdue scan, so the three cannot disagree about what
/// "outstanding" means — a dashboard saying two and a reminder saying three is the kind of
/// contradiction that makes people stop trusting the feature.
/// </remarks>
public sealed class OutstandingAcknowledgments(
    ICompendioDbContext db,
    IPermissionEvaluator permissions,
    IOptions<CompendioOptions> options,
    IClock clock)
{
    public async Task<IReadOnlyList<AcknowledgmentTaskDto>> ForAsync(
        PermissionSubject subject,
        CancellationToken cancellationToken = default)
    {
        var pages = await db.Pages
            .AsNoTracking()
            .Where(p => p.RequiresAcknowledgment)
            .Select(p => new { p.Id, p.Path, p.Title })
            .ToListAsync(cancellationToken);

        if (pages.Count == 0)
        {
            return [];
        }

        var pageIds = pages.Select(p => p.Id).ToList();

        // The round in force per page: the newest row for each.
        var rounds = (await db.AcknowledgmentRounds
                .AsNoTracking()
                .Where(r => pageIds.Contains(r.PageId))
                .ToListAsync(cancellationToken))
            .GroupBy(r => r.PageId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.OpenedAt).First());

        var mine = await db.Acknowledgments
            .AsNoTracking()
            .Where(a => a.UserId == subject.UserId && pageIds.Contains(a.PageId))
            .Select(a => new { a.PageId, a.PageVersionId })
            .ToListAsync(cancellationToken);

        var acknowledged = mine
            .GroupBy(a => a.PageId)
            .ToDictionary(g => g.Key, g => g.Select(a => a.PageVersionId).ToHashSet());

        // Overdue is measured from when the round opened, not from when the person first saw it:
        // "you have had two weeks" is a fact about the policy, not about somebody's inbox habits.
        var overdueBefore = clock.UtcNow.AddDays(-options.Value.Lifecycle.AcknowledgmentDueDays);
        var tasks = new List<AcknowledgmentTaskDto>();

        foreach (var page in pages)
        {
            if (!rounds.TryGetValue(page.Id, out var round))
            {
                continue;
            }

            if (acknowledged.TryGetValue(page.Id, out var versions) && versions.Contains(round.PageVersionId))
            {
                continue;
            }

            var path = Domain.Content.ContentPath.FromTrusted(page.Path);
            if (await permissions.EffectiveAsync(subject, path, cancellationToken) < PermissionLevel.Read)
            {
                continue;
            }

            tasks.Add(new AcknowledgmentTaskDto(page.Path, page.Title, round.OpenedAt, Overdue: round.OpenedAt < overdueBefore));
        }

        return tasks;
    }
}
