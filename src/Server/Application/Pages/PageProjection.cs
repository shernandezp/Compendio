using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Domain.Content;
using Compendio.Domain.Entities;
using Compendio.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace Compendio.Application.Pages;

/// <summary>
/// Builds a <see cref="PageDto"/> from a page row and its file.
/// </summary>
/// <remarks>
/// Shared by every endpoint that returns a page, so rendering, link resolution, translation
/// discovery and the caller's effective level are decided in one place. The link resolver is
/// permission-aware here rather than in the renderer, because only this layer knows who is asking.
/// </remarks>
public sealed class PageProjection(
    ICompendioDbContext db,
    IUserDirectory users,
    IMarkdownRenderer renderer,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser,
    IClock clock)
{
    public async Task<PageDto> BuildAsync(
        Page page,
        ContentFile? file,
        bool includeContent,
        bool includeHtml,
        CancellationToken cancellationToken)
    {
        var path = ContentPath.FromTrusted(page.Path);
        var level = await permissions.EffectiveAsync(currentUser.Subject, path, cancellationToken);

        var updatedBy = page.UpdatedByUserId is { } userId
            ? await users.DisplayNameAsync(userId, cancellationToken)
            : null;

        RenderedPage? rendered = null;
        if (includeHtml && file is not null)
        {
            var resolver = await BuildLinkResolverAsync(cancellationToken);
            rendered = renderer.Render(file.Text, path, resolver);
        }

        return new PageDto
        {
            Path = page.Path,
            Title = page.Title,
            Lang = page.Lang,
            TranslationKey = page.TranslationKey,
            Tags = page.Tags.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            Owner = page.Owner,
            ReviewIntervalDays = page.ReviewIntervalDays,
            NextReviewDate = page.NextReviewDate,
            RequiresAcknowledgment = page.RequiresAcknowledgment,
            // Computed here rather than in the client, so the banner on the page and the row in the
            // report are the same judgement rather than two implementations of one rule.
            IsStale = Domain.Lifecycle.ReviewSchedule.IsStale(page.NextReviewDate, clock.UtcNow),
            ContentHash = page.ContentHash,
            ByteSize = page.ByteSize,
            UpdatedAt = page.UpdatedAt,
            UpdatedBy = updatedBy,
            LastEditWasExternal = page.LastEditWasExternal,
            IsSecure = page.IsSecure,
            IsCanonical = page.IsCanonical,
            Level = level,
            Content = includeContent ? file?.Text : null,
            Html = rendered?.Html,
            Headings = rendered is null
                ? []
                : rendered.Headings.Select(h => new HeadingDto(h.Level, h.Text, h.Anchor)).ToArray(),
            ContainsMermaid = rendered?.ContainsMermaid ?? false,
            Translations = await TranslationsAsync(page, cancellationToken),
            Attachments = await AttachmentsAsync(page.Id, cancellationToken),
        };
    }

    /// <summary>
    /// Sibling language versions, found by <c>translationKey</c>.
    /// </summary>
    /// <remarks>
    /// A missing translation is a banner, never a 404 and never a blank page — showing the version
    /// that does exist and saying so is the only behaviour that is useful to the reader.
    /// The staleness flag is what keeps a bilingual wiki honest: it says the source moved on.
    /// </remarks>
    private async Task<IReadOnlyList<TranslationDto>> TranslationsAsync(Page page, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(page.TranslationKey))
        {
            return [];
        }

        var readable = await permissions.ReadableFolderPathsAsync(currentUser.Subject, cancellationToken);
        var isAdmin = currentUser.Role == UserRole.Admin;

        var siblings = await db.Pages
            .AsNoTracking()
            .Where(p => p.TranslationKey == page.TranslationKey && p.Id != page.Id)
            .Select(p => new { p.Path, p.Lang, p.Title, p.UpdatedAt, p.FolderId })
            .ToListAsync(cancellationToken);

        var folderPaths = await db.Folders
            .AsNoTracking()
            .ToDictionaryAsync(f => f.Id, f => f.Path, cancellationToken);

        return siblings
            .Where(s => isAdmin || (folderPaths.TryGetValue(s.FolderId, out var folder) && readable.Contains(folder)))
            .Select(s => new TranslationDto(
                s.Path,
                s.Lang ?? string.Empty,
                s.Title,
                IsStale: s.UpdatedAt < page.UpdatedAt))
            .ToArray();
    }

    private async Task<IReadOnlyList<AttachmentDto>> AttachmentsAsync(Guid pageId, CancellationToken cancellationToken) =>
        await db.Attachments
            .AsNoTracking()
            .Where(a => a.PageId == pageId)
            .OrderBy(a => a.Path)
            .Select(a => new AttachmentDto(
                a.Path,
                a.Path.Substring(a.Path.LastIndexOf('/') + 1),
                a.ContentType,
                a.ByteSize,
                a.CreatedAt))
            .ToArrayAsync(cancellationToken);

    /// <summary>
    /// Resolves <c>[[wiki links]]</c> to paths the caller may actually read.
    /// </summary>
    /// <remarks>
    /// A link to a restricted page comes back unresolved and renders as "does not exist". Rendering
    /// it as a working link would turn every page into a page-name oracle, which is the leak the
    /// tree and search go out of their way to avoid.
    /// </remarks>
    private async Task<Func<string, string?>> BuildLinkResolverAsync(CancellationToken cancellationToken)
    {
        var readable = await permissions.ReadableFolderPathsAsync(currentUser.Subject, cancellationToken);
        var isAdmin = currentUser.Role == UserRole.Admin;

        var candidates = await db.Pages
            .AsNoTracking()
            .Join(db.Folders, p => p.FolderId, f => f.Id, (p, f) => new { p.Path, p.Title, p.Slug, FolderPath = f.Path })
            .ToListAsync(cancellationToken);

        var visible = candidates
            .Where(c => isAdmin || readable.Contains(c.FolderPath))
            .ToList();

        var byPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in visible)
        {
            byPath.TryAdd(candidate.Path, candidate.Path);
            byPath.TryAdd(candidate.Path[..^CompendioConstantsMarkdownLength(candidate.Path)], candidate.Path);
            byPath.TryAdd(candidate.Slug, candidate.Path);
            byPath.TryAdd(candidate.Title, candidate.Path);
        }

        return target => byPath.GetValueOrDefault(target.Trim());
    }

    private static int CompendioConstantsMarkdownLength(string path) =>
        path.EndsWith(Domain.CompendioConstants.MarkdownExtension, StringComparison.OrdinalIgnoreCase)
            ? Domain.CompendioConstants.MarkdownExtension.Length
            : 0;
}
