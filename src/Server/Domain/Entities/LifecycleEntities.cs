namespace Compendio.Domain.Entities;

/// <summary>
/// A person confirming they have read one specific version of a page.
/// </summary>
/// <remarks>
/// <para>
/// Versioned on purpose. An acknowledgment that pointed at the page rather than the version would
/// let the report claim somebody had read a document that no longer exists, which is worse than
/// having no report at all — the whole compliance case rests on the claim being exact.
/// </para>
/// <para>
/// There is deliberately no foreign key to <see cref="Page"/> or <see cref="PageVersion"/>,
/// following the same reasoning as <see cref="PageVersion"/> itself: an acknowledgment has to
/// outlive the page it is about. "Who had signed off on the policy we deleted in March" is exactly
/// the question this table exists to answer, and a cascade would erase the answer.
/// </para>
/// </remarks>
public sealed class Acknowledgment
{
    public Guid Id { get; set; }

    public Guid PageId { get; set; }

    /// <summary>The exact version that was read.</summary>
    public Guid PageVersionId { get; set; }

    public Guid UserId { get; set; }

    public DateTimeOffset AcknowledgedAt { get; set; }

    /// <summary>The page's path at the time, so a report can be read after a move.</summary>
    public string Path { get; set; } = string.Empty;
}

/// <summary>
/// One opening of acknowledgment on a page: everybody who can read it owes a confirmation, measured
/// against <see cref="PageVersionId"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes "an ordinary edit does not re-ask two hundred people" implementable. Without
/// it the only available baseline is "the current version", and every typo fix would re-open the
/// acknowledgment — which is how the feature gets switched off.
/// </para>
/// <para>
/// A row rather than a column on <see cref="Page"/> for two reasons. The page table is rebuildable
/// from the content folder and this is not derivable from a file, so a column there would be lost by
/// a delete-and-restore. And an auditor's real question is "when was this re-opened, and who decided
/// it was material" — which a row answers and a column does not.
/// </para>
/// </remarks>
public sealed class AcknowledgmentRound
{
    public Guid Id { get; set; }

    public Guid PageId { get; set; }

    /// <summary>The version everyone is being asked to confirm they have read.</summary>
    public Guid PageVersionId { get; set; }

    public DateTimeOffset OpenedAt { get; set; }

    /// <summary>Null when the round was opened by front matter changing on disk rather than in the UI.</summary>
    public Guid? OpenedByUserId { get; set; }

    /// <summary>Why this round exists. Machine value, read by the report and the audit trail.</summary>
    public AcknowledgmentRoundReason Reason { get; set; }

    /// <summary>The page's path when the round opened, so a report survives a move.</summary>
    public string Path { get; set; } = string.Empty;
}

public enum AcknowledgmentRoundReason
{
    /// <summary>The page began requiring acknowledgment.</summary>
    Opened = 0,

    /// <summary>An editor marked a save as a material revision, re-asking everyone.</summary>
    MaterialRevision = 1,
}

/// <summary>What a notification is about. Stable machine values — never a localized sentence.</summary>
public enum NotificationKind
{
    /// <summary>A page you own has passed its review date.</summary>
    PageStale = 0,

    /// <summary>A page you own was changed on disk rather than in the editor.</summary>
    OwnedPageEditedExternally = 1,

    /// <summary>The source of a translation you own has changed.</summary>
    TranslationSourceChanged = 2,

    /// <summary>A page you can read requires acknowledgment, and you have not given it.</summary>
    AcknowledgmentRequested = 3,

    /// <summary>An acknowledgment you owe has passed its due window.</summary>
    AcknowledgmentOverdue = 4,

    /// <summary>The scheduled git push failed twice in a row. Admins only.</summary>
    GitMirrorFailed = 5,
}

/// <summary>
/// One row in a person's inbox.
/// </summary>
/// <remarks>
/// <para>
/// Written by the change pipeline and the lifecycle scan, never by an endpoint — a notification is a
/// consequence of something happening, not of somebody asking.
/// </para>
/// <para>
/// <see cref="ReadAt"/> is what makes deduplication work: the unique index covers
/// <c>(UserId, Kind, TargetPath)</c> only while the row is unread, so a page that stays stale for
/// three months produces one row rather than ninety, and the same condition recurring after the
/// person has dealt with it produces a fresh one.
/// </para>
/// <para>
/// <see cref="TargetPath"/> is re-checked against the permission evaluator when the inbox is read.
/// Access changes after a row is written, and a notification is a read surface like any other.
/// </para>
/// </remarks>
public sealed class Notification
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public NotificationKind Kind { get; set; }

    /// <summary>Content-relative path of the page this is about. Empty for instance-level notices.</summary>
    public string TargetPath { get; set; } = string.Empty;

    /// <summary>
    /// Small JSON payload for rendering — a page title, a language code, an error summary. Never
    /// page content, and never anything the recipient could not read by asking for the page.
    /// </summary>
    public string? PayloadJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ReadAt { get; set; }
}
