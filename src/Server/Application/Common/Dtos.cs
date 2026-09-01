using Compendio.Domain.Security;

namespace Compendio.Application.Common;

/// <summary>
/// The shapes the API returns.
/// </summary>
/// <remarks>
/// Separate from the EF entities on purpose: an entity leaking out of an endpoint takes its
/// navigation properties, its internal ids and its future columns with it, and every one of those
/// becomes a contract nobody agreed to. Every timestamp here is ISO-8601 UTC — the server never
/// formats a date for display, the client does.
/// </remarks>
public sealed record PageDto
{
    public required string Path { get; init; }

    public required string Title { get; init; }

    public string? Lang { get; init; }

    public string? TranslationKey { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public string? Owner { get; init; }

    // ---- Lifecycle. Carried on the page so the banners need no second request. -------------------
    public int? ReviewIntervalDays { get; init; }

    public DateTimeOffset? NextReviewDate { get; init; }

    public bool RequiresAcknowledgment { get; init; }

    /// <summary>Computed by the server, so the banner and the report cannot disagree.</summary>
    public bool IsStale { get; init; }

    public required string ContentHash { get; init; }

    public long ByteSize { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public string? UpdatedBy { get; init; }

    /// <summary>True when the last change came from the file system rather than the editor.</summary>
    public bool LastEditWasExternal { get; init; }

    public bool IsSecure { get; init; }

    /// <summary>False until a human saves it in the editor and the one-time normalization runs.</summary>
    public bool IsCanonical { get; init; }

    /// <summary>The caller's effective level here. The UI renders from this, it does not decide.</summary>
    public required PermissionLevel Level { get; init; }

    /// <summary>Raw Markdown. Present on a read, absent from list responses.</summary>
    public string? Content { get; init; }

    /// <summary>Sanitized HTML.</summary>
    public string? Html { get; init; }

    public IReadOnlyList<HeadingDto> Headings { get; init; } = [];

    public bool ContainsMermaid { get; init; }

    /// <summary>Sibling language versions, from <c>translationKey</c>.</summary>
    public IReadOnlyList<TranslationDto> Translations { get; init; } = [];

    public IReadOnlyList<AttachmentDto> Attachments { get; init; } = [];
}

public sealed record HeadingDto(int Level, string Text, string Anchor);

/// <param name="IsStale">
/// This sibling is the source text and it changed after the page being read did. Set only on a
/// translation's view of its source, so the "source has changed" banner lands on the translation.
/// </param>
public sealed record TranslationDto(string Path, string Lang, string Title, bool IsStale);

/// <summary>
/// A page whose file is gone but whose history is still inside the retention window.
/// </summary>
/// <param name="PageId">The identity the versions are keyed by; a restore keeps it, and the history with it.</param>
/// <param name="Path">Where the page was when it was deleted.</param>
/// <param name="Title">From the last version's front matter, so the list reads as pages rather than paths.</param>
/// <param name="DeletedAt">When the versions were tombstoned.</param>
/// <param name="LastVersionAt">When the content a restore would bring back was written.</param>
/// <param name="Versions">How much history comes back with it.</param>
public sealed record DeletedPageDto(
    Guid PageId,
    string Path,
    string Title,
    DateTimeOffset DeletedAt,
    DateTimeOffset LastVersionAt,
    int Versions);

public sealed record AttachmentDto(string Path, string Name, string ContentType, long ByteSize, DateTimeOffset CreatedAt);

/// <param name="Level">
/// The caller's effective level on this node. Nodes at <c>none</c> are absent from the response
/// entirely, never present-and-greyed — folder names leak.
/// </param>
public sealed record TreeNodeDto
{
    public required string Path { get; init; }

    public required string Name { get; init; }

    public required string Title { get; init; }

    public required bool IsFolder { get; init; }

    public bool IsSecure { get; init; }

    public required PermissionLevel Level { get; init; }

    public string? Lang { get; init; }

    public IReadOnlyList<TreeNodeDto> Children { get; init; } = [];
}

/// <summary>
/// The navigation tree plus the caller's effective level at the root.
/// </summary>
/// <remarks>
/// The root is not a node — its children are the top level — so its permission level had nowhere to
/// go, and the client was left showing a top-level "New page" button to people who can only read the
/// root. <see cref="RootLevel"/> is that missing fact: the create affordances that target the root
/// gate on it.
/// </remarks>
public sealed record TreeDto(PermissionLevel RootLevel, IReadOnlyList<TreeNodeDto> Nodes);

public sealed record SearchHitDto(
    string Path,
    string Title,
    string Excerpt,
    string? Lang,
    IReadOnlyList<string> Tags,
    DateTimeOffset UpdatedAt);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize)
{
    public static PagedResult<T> Empty(int page, int pageSize) => new([], 0, page, pageSize);
}

public sealed record UserDto(
    Guid Id,
    string UserName,
    string DisplayName,
    string? Email,
    UserRole Role,
    bool Active,
    string? PreferredLanguage,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSignInAt,
    IReadOnlyList<Guid> GroupIds);

public sealed record GroupDto(Guid Id, string Name, bool Active, int MemberCount, IReadOnlyList<Guid> MemberIds);

public sealed record AclEntryDto(AclSubjectType SubjectType, Guid? SubjectId, string SubjectName, PermissionLevel Level);

/// <param name="InheritParent">
/// <c>true</c> — inherits and may only add access. <c>false</c> — restricted to exactly the entries
/// below. Those are the only two states, and there are no deny entries.
/// </param>
public sealed record AclDto(
    string FolderPath,
    bool InheritParent,
    IReadOnlyList<AclEntryDto> Entries,
    IReadOnlyList<AclEntryDto> InheritedFrom,
    bool IsSecure,
    DateTimeOffset? UpdatedAt);

/// <param name="Reason">Why the user ends up at this level — what the effective-access preview shows.</param>
public sealed record EffectiveAccessDto(Guid UserId, string DisplayName, PermissionLevel Level, string Reason);

public sealed record SecureScopeDto(
    string FolderPath,
    Guid KeyId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RotatedAt,
    bool IndexContent,
    bool AllowAi,
    string Availability,
    long EncryptionCount);

public sealed record AuditEntryDto(
    Guid Id,
    DateTimeOffset At,
    Guid? ActorUserId,
    string? ActorDisplayName,
    string Action,
    string TargetType,
    string TargetPath,
    string? BeforeJson,
    string? AfterJson);

/// <param name="NeedsSetup">True while no user exists. Everything else redirects to the wizard.</param>
public sealed record SetupStateDto(bool NeedsSetup, string DefaultLanguage, IReadOnlyList<LanguageDto> Languages, string ContentRoot);

public sealed record LanguageDto(string Code, string EnglishName, string NativeName);

public sealed record ProfileDto(
    Guid Id,
    string UserName,
    string DisplayName,
    string? Email,
    UserRole Role,
    string Language,
    bool IsSetupComplete);

public sealed record AboutDto(
    string Product,
    string Version,
    string License,
    string SourceUrl,
    string LicenseNotice,
    string InstanceName);

public sealed record StatusDto(
    string Version,
    string InstallMode,
    string ContentRoot,
    int PageCount,
    int FolderCount,
    string WatcherMode,
    string IndexState,
    int IndexQueueDepth,
    string SecureAvailability,
    long DatabaseBytes,
    long ContentBytes,
    DateTimeOffset? LastBackupAt);

public sealed record TemplateDto(string Id, string Title, string? Description, string Content);
