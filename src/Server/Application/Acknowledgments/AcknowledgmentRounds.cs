using Compendio.Application.Abstractions;
using Compendio.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Compendio.Application.Acknowledgments;

/// <summary>
/// Opens and finds the round a page's acknowledgments are measured against.
/// </summary>
/// <remarks>
/// The one place that decides when everyone is asked to read a page again. Keeping it here rather
/// than inside the save handler means the rule — an ordinary edit changes nothing, an explicitly
/// material one re-opens — is stated once and can be read in one screen.
/// </remarks>
public sealed class AcknowledgmentRounds(ICompendioDbContext db, IClock clock)
{
    /// <summary>The round in force, or null when the page has never required acknowledgment.</summary>
    public Task<AcknowledgmentRound?> CurrentAsync(Guid pageId, CancellationToken cancellationToken = default) =>
        db.AcknowledgmentRounds
            .AsNoTracking()
            .Where(r => r.PageId == pageId)
            .OrderByDescending(r => r.OpenedAt)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Brings a page's rounds in line with what it now declares, after a save.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three cases, and only two of them open a round. A page that has just started requiring
    /// acknowledgment opens its first. A page saved as a material revision opens another. A page
    /// saved ordinarily opens none — which is the whole point: acknowledgments already given stay
    /// given, and nobody is asked to re-read a typo fix.
    /// </para>
    /// <para>
    /// The current version is resolved here rather than passed in, because the caller has just
    /// written one and the sequence is what identifies it.
    /// </para>
    /// </remarks>
    public async Task<AcknowledgmentRound?> SynchronizeAsync(
        Page page,
        bool materialRevision,
        Guid? actorUserId,
        CancellationToken cancellationToken = default)
    {
        if (!page.RequiresAcknowledgment)
        {
            return null;
        }

        var current = await CurrentAsync(page.Id, cancellationToken);

        if (current is not null && !materialRevision)
        {
            return current;
        }

        var version = await db.PageVersions
            .AsNoTracking()
            .Where(v => v.PageId == page.Id && v.TombstonedAt == null)
            .OrderByDescending(v => v.Sequence)
            .Select(v => new { v.Id })
            .FirstOrDefaultAsync(cancellationToken);

        if (version is null)
        {
            // No history yet. Nothing to bind an acknowledgment to, and inventing a version id would
            // make the report point at something that never existed.
            return current;
        }

        if (current is not null && current.PageVersionId == version.Id)
        {
            // A material save that produced no new version — the metadata writer short-circuits when
            // the bytes are unchanged. Re-opening against the same version would ask people to
            // re-read what they have already confirmed.
            return current;
        }

        var round = new AcknowledgmentRound
        {
            Id = Guid.CreateVersion7(),
            PageId = page.Id,
            PageVersionId = version.Id,
            OpenedAt = clock.UtcNow,
            OpenedByUserId = actorUserId,
            Reason = current is null ? AcknowledgmentRoundReason.Opened : AcknowledgmentRoundReason.MaterialRevision,
            Path = page.Path,
        };

        db.AcknowledgmentRounds.Add(round);

        db.AuditLog.Add(new AuditEntry
        {
            Id = Guid.CreateVersion7(),
            At = round.OpenedAt,
            ActorUserId = actorUserId,
            Action = round.Reason == AcknowledgmentRoundReason.Opened
                ? "acknowledgment.opened"
                : "acknowledgment.reopened",
            TargetType = "page",
            TargetPath = page.Path,
            AfterJson = $"{{\"versionId\":\"{round.PageVersionId}\"}}",
        });

        await db.SaveChangesAsync(cancellationToken);
        return round;
    }
}
