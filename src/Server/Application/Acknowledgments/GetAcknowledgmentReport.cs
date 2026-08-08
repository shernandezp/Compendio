using Common.Mediator;
using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Domain.Content;
using Compendio.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace Compendio.Application.Acknowledgments;

/// <summary>
/// Who has confirmed reading a page, and who has not.
/// </summary>
/// <remarks>
/// <para>
/// "Required" means everyone who can read the page — the same evaluator answer the page itself uses,
/// asked per active account. A policy nobody can read is a policy nobody owes an acknowledgment for,
/// and computing the list any other way would produce a report demanding confirmations that cannot
/// be given.
/// </para>
/// <para>
/// Reading the report needs <c>manage</c> on the page's folder. It is a list of who has and has not
/// done something, which is a different kind of information from the page itself.
/// </para>
/// </remarks>
public sealed record GetAcknowledgmentReportQuery(string Path) : IQuery<AcknowledgmentReportDto>;

public sealed class GetAcknowledgmentReportHandler(
    ICompendioDbContext db,
    IPathPolicy paths,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser,
    IUserDirectory users,
    AcknowledgmentRounds rounds) : IRequestHandler<GetAcknowledgmentReportQuery, AcknowledgmentReportDto>
{
    public async Task<AcknowledgmentReportDto> Handle(GetAcknowledgmentReportQuery request, CancellationToken cancellationToken = default)
    {
        var path = paths.Require(request.Path, PathKind.Page);
        await permissions.RequireManageAsync(currentUser.Subject, path, cancellationToken);

        var page = await db.Pages.AsNoTracking().FirstOrDefaultAsync(p => p.Path == path.Value, cancellationToken)
                   ?? throw CompendioException.NotFound(path);

        var round = await rounds.CurrentAsync(page.Id, cancellationToken);
        if (round is null)
        {
            return new AcknowledgmentReportDto(page.Path, page.Title, Guid.Empty, 0, 0, 0, []);
        }

        var sequence = await db.PageVersions
            .AsNoTracking()
            .Where(v => v.Id == round.PageVersionId)
            .Select(v => v.Sequence)
            .FirstOrDefaultAsync(cancellationToken);

        var given = await db.Acknowledgments
            .AsNoTracking()
            .Where(a => a.PageId == page.Id && a.PageVersionId == round.PageVersionId)
            .ToDictionaryAsync(a => a.UserId, a => a, cancellationToken);

        var people = new List<AcknowledgmentStatusDto>();

        foreach (var user in await users.ActiveUsersAsync(cancellationToken))
        {
            var subject = await users.SubjectAsync(user.Id, cancellationToken);
            if (subject is null)
            {
                continue;
            }

            // Required = can read. Asked of the evaluator rather than assumed from a role, because
            // a Reader restricted out of the folder does not owe anything.
            if (await permissions.EffectiveAsync(subject.Value.Subject, path, cancellationToken) < PermissionLevel.Read)
            {
                continue;
            }

            given.TryGetValue(user.Id, out var acknowledgment);

            people.Add(new AcknowledgmentStatusDto(
                user.Id,
                user.DisplayName,
                acknowledgment is not null,
                acknowledgment?.PageVersionId,
                acknowledgment?.AcknowledgedAt));
        }

        return new AcknowledgmentReportDto(
            page.Path,
            page.Title,
            round.PageVersionId,
            sequence,
            people.Count,
            people.Count(p => p.HasAcknowledged),
            people.OrderBy(p => p.DisplayName, StringComparer.CurrentCulture).ToArray());
    }
}
