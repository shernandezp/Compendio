using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Domain.Content;
using Compendio.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Compendio.Application.Lifecycle;

/// <summary>
/// Rewrites a page's front matter, leaving its body byte-for-byte alone.
/// </summary>
/// <remarks>
/// <para>
/// This does not break the rule that remark in the client is the only writer of Markdown. The body
/// is not re-serialized — it is copied verbatim out of the parsed document and back — and only the
/// YAML block above it is re-emitted, with the stable key order <see cref="MarkdownParser.Emit"/>
/// fixes. The blast radius of a review-date change is therefore the front-matter block, not the
/// page.
/// </para>
/// <para>
/// It writes through the store and then <see cref="IContentPipeline.RecordSavedAsync"/> rather than
/// <c>SavePageAsync</c>, for the same reason the checkbox path does: the file is already written, so
/// routing back through the save path would write identical bytes a second time. It also leaves
/// <c>IsCanonical</c> alone — setting a review date says nothing about whether the body is in
/// canonical form, and claiming otherwise would rob the next real save of its one-time
/// normalization.
/// </para>
/// </remarks>
public sealed class PageMetadataWriter(IContentStore store, IContentPipeline pipeline, ICompendioDbContext db)
{
    /// <param name="mutate">
    /// Applied to the parsed front matter. Unknown keys ride along in <c>Extra</c> untouched, which
    /// is what keeps another tool's front-matter keys from being eaten by a review-date edit.
    /// </param>
    public async Task<Page> ApplyAsync(
        ContentPath path,
        Func<FrontMatter, FrontMatter> mutate,
        Guid? actorUserId,
        string? note,
        CancellationToken cancellationToken = default)
    {
        var file = await store.ReadAsync(path, cancellationToken) ?? throw CompendioException.NotFound(path);
        var document = MarkdownParser.Parse(file.Text);

        var updated = mutate(document.FrontMatter);
        var composed = MarkdownParser.Compose(updated, document.Body, document.LineEnding);

        if (string.Equals(composed, file.Text, StringComparison.Ordinal))
        {
            // Nothing changed, so nothing is written and nothing is recorded. Going through the
            // pipeline anyway would add a history version saying somebody saved the same bytes —
            // noise in the one place people go to understand what changed.
            return await db.Pages
                       .AsNoTracking()
                       .FirstOrDefaultAsync(p => p.Path == path.Value, cancellationToken)
                   ?? throw CompendioException.NotFound(path);
        }

        var bytes = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(composed);
        await store.WriteAsync(path, bytes, file.ContentHash, cancellationToken);

        return await pipeline.RecordSavedAsync(path, actorUserId, note, cancellationToken);
    }
}
