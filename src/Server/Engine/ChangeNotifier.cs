using Compendio.Application.Abstractions;
using Compendio.Domain.Entities;
using Compendio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Compendio.Engine;

/// <summary>
/// Tells the people who need to know that a page changed.
/// </summary>
/// <remarks>
/// <para>
/// Split out of <see cref="ContentPipeline"/> rather than living inside it. The pipeline's job is
/// the ordered application of a change — disk, rows, history, ACL paths, index queue — and "who
/// should hear about this" is a different subject that would have taken the file past the hard line
/// ceiling. The pipeline calls in here once, at the end, and stays about ordering.
/// </para>
/// <para>
/// Failures are logged and swallowed. A notification is a courtesy; taking a content save down
/// because an inbox row could not be written would be the wrong trade in every case.
/// </para>
/// </remarks>
public sealed class ChangeNotifier(
    IUserDirectory users,
    INotificationWriter notifications,
    ILogger<ChangeNotifier> logger)
{
    /// <param name="source">Only an external edit interests a page's owner; their own save does not.</param>
    public async Task NotifyAsync(
        CompendioDbContext db,
        Page page,
        VersionSource source,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (source == VersionSource.External && page.Owner is { Length: > 0 })
            {
                // An external edit leaves no trace in the UI. Without this the owner of a runbook
                // somebody corrected in VS Code never finds out it changed.
                var ownerId = await users.ResolveOwnerAsync(page.Owner, cancellationToken);
                if (ownerId is { } id)
                {
                    await notifications.NotifyAsync(
                        id, NotificationKind.OwnedPageEditedExternally, page.Path,
                        Payload.PageTitle(page.Title), cancellationToken);
                }
            }

            await NotifyTranslationOwnersAsync(db, page, cancellationToken);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not write change notifications for '{Path}'.", page.Path);
        }
    }

    /// <summary>
    /// Tells the owner of every sibling translation that their source moved.
    /// </summary>
    /// <remarks>
    /// Only when the page that changed is the reference text. A Spanish page being edited does not
    /// make the English one stale — English is the reference translations are made from, so the
    /// arrow points one way. A translation silently drifting out of date is the failure the
    /// bilingual promise cannot afford.
    /// </remarks>
    private async Task NotifyTranslationOwnersAsync(CompendioDbContext db, Page page, CancellationToken cancellationToken)
    {
        if (page.TranslationKey is not { Length: > 0 } key)
        {
            return;
        }

        if (!Domain.Localization.SupportedLanguages.IsReference(page.Lang))
        {
            return;
        }

        var siblings = await db.Pages
            .AsNoTracking()
            .Where(p => p.TranslationKey == key && p.Id != page.Id && p.Owner != null)
            .Select(p => new { p.Path, p.Title, p.Owner, p.Lang })
            .ToListAsync(cancellationToken);

        foreach (var sibling in siblings)
        {
            var ownerId = await users.ResolveOwnerAsync(sibling.Owner, cancellationToken);
            if (ownerId is { } id)
            {
                await notifications.NotifyAsync(
                    id, NotificationKind.TranslationSourceChanged, sibling.Path,
                    Payload.Language(sibling.Title, sibling.Lang), cancellationToken);
            }
        }
    }
}
