namespace Compendio.Domain.Entities;

/// <summary>Where a page version came from. Recorded honestly, never inferred.</summary>
public enum VersionSource
{
    /// <summary>Saved in the editor by a signed-in user.</summary>
    Editor = 0,

    /// <summary>Arrived from the file system. Attributed to nobody, timestamped from the file.</summary>
    External = 1,

    /// <summary>Created by a move or rename.</summary>
    Move = 2,

    /// <summary>The page was deleted. Tombstoned for the retention window.</summary>
    Delete = 3,

    /// <summary>A restore, which writes a new version rather than rewinding.</summary>
    Restore = 4,

    /// <summary>The one-time normalization to canonical Markdown on first editor save.</summary>
    Normalization = 5,
}

/// <summary>
/// A full snapshot of a page at one point in time, Brotli-compressed.
/// </summary>
/// <remarks>
/// Keyed by page identity rather than path, so history survives a rename. Snapshots inside a secure
/// scope are encrypted with the scope key and diffed in memory.
/// </remarks>
public sealed class PageVersion
{
    public Guid Id { get; set; }

    public Guid PageId { get; set; }

    /// <summary>1-based, dense per page.</summary>
    public int Sequence { get; set; }

    /// <summary>Null for an external edit — that is the point of recording the source.</summary>
    public Guid? AuthorUserId { get; set; }

    public VersionSource Source { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string ContentHash { get; set; } = string.Empty;

    public long ByteSize { get; set; }

    public string? Note { get; set; }

    /// <summary>Brotli-compressed page bytes, then encrypted if the page is in a secure scope.</summary>
    public byte[] Content { get; set; } = [];

    public bool IsEncrypted { get; set; }

    public Guid? KeyId { get; set; }

    /// <summary>The page's path at the time — history survives moves, and shows them.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Set when the page is deleted; the row is purged after the retention window.</summary>
    public DateTimeOffset? TombstonedAt { get; set; }

    // There is deliberately no navigation to Page and no foreign key. A deleted page's versions are
    // tombstoned for the retention window and must survive the page row being gone — which is
    // exactly what saves an organization from a mis-synced backup client. A foreign key here would
    // make deletion either impossible (Restrict) or destroy the history (Cascade).
}

/// <summary>
/// Append-only. Not configurable off — one insert is what turns "someone opened up the HR folder"
/// from an argument into a lookup.
/// </summary>
public sealed class AuditEntry
{
    public Guid Id { get; set; }

    public DateTimeOffset At { get; set; }

    public Guid? ActorUserId { get; set; }

    /// <summary>Stable machine code, never a localized sentence.</summary>
    public string Action { get; set; } = string.Empty;

    public string TargetType { get; set; } = string.Empty;

    public string TargetPath { get; set; } = string.Empty;

    public string? BeforeJson { get; set; }

    public string? AfterJson { get; set; }
}

/// <summary>
/// One AI request that reached a provider, recorded so the daily budget can be counted.
/// </summary>
/// <remarks>
/// <para>
/// A separate table rather than a row in <see cref="AuditEntry"/>: the audit log is append-only
/// history an administrator reads, and counting a rolling window over it on every AI request would
/// put a scan of the whole log behind every button press. This one is small, indexed for exactly
/// that query, and pruned.
/// </para>
/// <para>
/// It records that a request happened, never what it said. The feature name and a character count
/// are enough to answer "who is spending the budget" without the usage table becoming a second copy
/// of everybody's page content.
/// </para>
/// </remarks>
public sealed class AiUsageEntry
{
    public Guid Id { get; set; }

    public DateTimeOffset At { get; set; }

    /// <summary>
    /// Who made it. <see cref="Guid.Empty"/> is the system caller, matching the audit log's
    /// convention; null is reserved for a row whose user has been hard-deleted.
    /// </summary>
    public Guid? UserId { get; set; }

    /// <summary>An <c>AiFeatures</c> machine name — <c>improve</c>, <c>ask</c>. Never a localized word.</summary>
    public string Feature { get; set; } = string.Empty;

    /// <summary>Characters sent, after truncation. The closest cheap proxy for what the request cost.</summary>
    public int InputCharacters { get; set; }
}

public enum IndexOperation
{
    Upsert = 0,
    Delete = 1,
    Move = 2,
}

/// <summary>
/// Durable work list for the indexer, so a crash mid-batch resumes rather than silently leaving
/// stale rows. Startup drains it and then reconciles against the file system.
/// </summary>
public sealed class IndexQueueItem
{
    public Guid Id { get; set; }

    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// The page this item is about, captured when it was queued.
    /// </summary>
    /// <remarks>
    /// Carried explicitly because a delete is queued <em>after</em> the page row is gone, so there
    /// is nothing left to look the id up from. Resolving it from the path afterwards does not work
    /// either: <c>PageText.Path</c> holds the search-tokenized form, not the real path.
    /// </remarks>
    public Guid? PageId { get; set; }

    /// <summary>Previous path, for <see cref="IndexOperation.Move"/>.</summary>
    public string? FromPath { get; set; }

    public IndexOperation Operation { get; set; }

    public DateTimeOffset EnqueuedAt { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }
}

/// <summary>Instance settings that outlive a config file — set in the wizard, edited in admin.</summary>
public sealed class Setting
{
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; }
}

public static class SettingKeys
{
    public const string SetupCompletedAt = "setup.completedAt";
    public const string InstanceDefaultLanguage = "instance.defaultLanguage";
    public const string InstanceDefaultAccess = "instance.defaultAccess";
    public const string InstanceName = "instance.name";
    public const string ForceSingleLanguage = "instance.forceSingleLanguage";
    public const string AclVersion = "acl.version";
    public const string LastBackupAt = "backup.lastAt";
    public const string SearchSynonyms = "search.synonyms";

    // ---- AI. The key is protected with ISecretProtector; the rest is plain. -----------------------
    // Deliberately not the master key: that one is created lazily when the first secure scope is
    // made, so an instance with no secure scope would have nothing to encrypt an API key with.
    public const string AiBaseUrl = "ai.baseUrl";
    public const string AiModel = "ai.model";
    public const string AiApiKey = "ai.apiKey";
    public const string AiAllowedSpaces = "ai.allowedSpaces";

    /// <summary>
    /// The features that are switched <em>off</em>, despite the key's name.
    /// </summary>
    /// <remarks>
    /// The stored key string is kept as it shipped so an existing instance does not silently lose
    /// its setting on upgrade. Storing the disabled list rather than the enabled one is deliberate:
    /// a feature added in a later version is then on by default rather than invisible until an
    /// administrator notices it exists.
    /// </remarks>
    public const string AiDisabledFeatures = "ai.enabledFeatures";

    /// <summary>AI requests one person may make in a rolling 24 hours. 0 means no limit.</summary>
    public const string AiDailyPerUser = "ai.dailyPerUser";

    /// <summary>AI requests the whole instance may make in a rolling 24 hours. 0 means no limit.</summary>
    public const string AiDailyPerInstance = "ai.dailyPerInstance";

    // ---- Git mirror. Four values, which does not earn a table. ------------------------------------
    public const string GitMirrorLastSuccessAt = "gitmirror.lastSuccessAt";
    public const string GitMirrorLastAttemptAt = "gitmirror.lastAttemptAt";
    public const string GitMirrorLastCommit = "gitmirror.lastCommit";
    public const string GitMirrorLastError = "gitmirror.lastError";
    public const string GitMirrorConsecutiveFailures = "gitmirror.consecutiveFailures";
}
