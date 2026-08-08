using Common.Mediator;
using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Domain.Content;
using Microsoft.EntityFrameworkCore;

namespace Compendio.Application.Pages;

/// <param name="Offset">Byte offset of the <c>[ ]</c> or <c>[x]</c> marker in the file.</param>
/// <param name="Checked">The state to move to.</param>
/// <param name="ExpectedHash">The hash the caller rendered from.</param>
public sealed record ToggleCheckboxCommand(string Path, int Offset, bool Checked, string ExpectedHash) : ICommand<PageDto>;

/// <summary>
/// Ticks a checklist item from read mode.
/// </summary>
/// <remarks>
/// A byte-level substitution, not a re-serialization: <c>- [ ]</c> becomes <c>- [x]</c> at a known
/// offset, validated against the expected old value and the content hash. The server is allowed to
/// do this precisely because it is not writing Markdown — it is editing two characters — so the
/// rule that remark is the only serializer survives intact.
/// <para>
/// This is the interaction the acceptance scenario is built around: a technician at a server rack,
/// one-handed, ticking off a runbook step.
/// </para>
/// </remarks>
public sealed class ToggleCheckboxHandler(
    IContentStore store,
    IContentPipeline pipeline,
    IPathPolicy paths,
    IPermissionEvaluator permissions,
    ICurrentUser currentUser,
    PageProjection projection) : IRequestHandler<ToggleCheckboxCommand, PageDto>
{
    private const string Unchecked = "[ ]";
    private const string CheckedLower = "[x]";
    private const string CheckedUpper = "[X]";

    public async Task<PageDto> Handle(ToggleCheckboxCommand request, CancellationToken cancellationToken = default)
    {
        var path = paths.Require(request.Path, PathKind.Page);
        await permissions.RequireWriteAsync(currentUser.Subject, path, cancellationToken);

        var current = await store.ReadAsync(path, cancellationToken) ?? throw CompendioException.NotFound(path);

        var expected = request.Checked ? Unchecked : ActualCheckedForm(current.Bytes, request.Offset);
        var replacement = request.Checked ? CheckedLower : Unchecked;

        await store.SubstituteAsync(path, request.Offset, expected, replacement, request.ExpectedHash, cancellationToken);

        // The substitution already wrote the file, so this only syncs the database — history, the
        // page row and the index queue. Routing it back through SavePageAsync would write the
        // identical bytes to disk a second time.
        var page = await pipeline.RecordSavedAsync(path, currentUser.UserId, note: null, cancellationToken);

        var reread = await store.ReadAsync(path, cancellationToken);
        return await projection.BuildAsync(page, reread, includeContent: true, includeHtml: true, cancellationToken);
    }

    /// <summary>
    /// A file may use <c>[x]</c> or <c>[X]</c>. Unchecking has to match whichever is actually there.
    /// </summary>
    private static string ActualCheckedForm(byte[] bytes, int offset)
    {
        if (offset >= 0 && offset + 3 <= bytes.Length && bytes[offset + 1] == (byte)'X')
        {
            return CheckedUpper;
        }

        return CheckedLower;
    }
}
