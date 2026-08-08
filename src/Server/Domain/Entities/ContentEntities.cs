namespace Compendio.Domain.Entities;

/// <summary>
/// A folder in the content tree. Mirrors a directory on disk; the disk is the source of truth and
/// this row is the queryable projection of it.
/// </summary>
public sealed class Folder
{
    public Guid Id { get; set; }

    /// <summary>Content-relative, forward slashes. Unique. The empty string is the root.</summary>
    public string Path { get; set; } = string.Empty;

    public Guid? ParentId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Denormalized from <see cref="SecureScope"/> so tree queries need no join.</summary>
    public bool IsSecure { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Folder? Parent { get; set; }

    public ICollection<Folder> Children { get; set; } = [];

    public ICollection<Page> Pages { get; set; } = [];
}

/// <summary>
/// A page. Everything here except <see cref="Id"/> is reconstructible from the file, which is what
/// makes <c>compendio reindex</c> and a full reconciliation possible.
/// </summary>
public sealed class Page
{
    public Guid Id { get; set; }

    public Guid FolderId { get; set; }

    /// <summary>Content-relative path including <c>.md</c>. Unique.</summary>
    public string Path { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>BCP-47 from front matter. Null means the instance default language.</summary>
    public string? Lang { get; set; }

    /// <summary>Stable key shared by every language version of the same document.</summary>
    public string? TranslationKey { get; set; }

    /// <summary>Normalized, space-separated. Denormalized for the <c>tag:</c> filter.</summary>
    public string Tags { get; set; } = string.Empty;

    public string? Owner { get; set; }

    /// <summary>SHA-256 of the file bytes, lower-case hex. Required on every write.</summary>
    public string ContentHash { get; set; } = string.Empty;

    public long ByteSize { get; set; }

    public bool IsSecure { get; set; }

    /// <summary>
    /// Whether the file is in canonical Markdown. False until a human saves it in the editor, at
    /// which point the one-time normalization happens and shows up in history as such.
    /// </summary>
    public bool IsCanonical { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedByUserId { get; set; }

    /// <summary>
    /// True when the last change arrived from the file system rather than the UI. Attribution
    /// honesty: an external edit is never credited to the last signed-in user.
    /// </summary>
    public bool LastEditWasExternal { get; set; }

    // ---- Lifecycle: schema in v0, behaviour in v1. ---------------------------------------------
    public int? ReviewIntervalDays { get; set; }

    public DateTimeOffset? NextReviewDate { get; set; }

    public bool RequiresAcknowledgment { get; set; }

    public Folder? Folder { get; set; }

    public PageText? Text { get; set; }

    // No Versions navigation on purpose: history outlives its page, so there is no foreign key
    // between them and therefore nothing for EF to navigate.
    public ICollection<Attachment> Attachments { get; set; } = [];
}

/// <summary>
/// Extracted plain text, one row per page. The FTS5 table is external-content over this, so the
/// index stores no second copy and reindexing is a pure function of the file.
/// </summary>
public sealed class PageText
{
    /// <summary>
    /// The primary key, and deliberately an integer rather than the page's GUID.
    /// </summary>
    /// <remarks>
    /// An external-content FTS5 table joins on the content table's <c>rowid</c>, and only an
    /// <c>INTEGER PRIMARY KEY</c> is a rowid alias in SQLite. Keying this table on the GUID instead
    /// leaves <c>RowId</c> as an ordinary column that nothing populates.
    /// </remarks>
    public long RowId { get; set; }

    public Guid PageId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Headings { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public string Tags { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public Page? Page { get; set; }
}

public sealed class Attachment
{
    public Guid Id { get; set; }

    public Guid PageId { get; set; }

    /// <summary>Content-relative path, normally <c>&lt;folder&gt;/assets/&lt;name&gt;</c>.</summary>
    public string Path { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public long ByteSize { get; set; }

    public bool IsSecure { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Page? Page { get; set; }
}
