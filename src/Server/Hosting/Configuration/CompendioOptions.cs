using Compendio.Domain.Localization;
using Compendio.Domain.Security;

namespace Compendio.Hosting.Configuration;

/// <summary>
/// Every knob, in one place.
/// </summary>
/// <remarks>
/// Bound from <c>appsettings.json</c>, <c>Section__Key</c> environment variables and the command
/// line, in that order of increasing precedence. Environment variables use the framework's native
/// <c>Section__Key</c> convention rather than a <c>COMPENDIO_*</c> prefix: a custom prefix would be
/// mapping code to maintain for no gain.
/// <para>
/// A fresh install with no configuration at all must start. Every default here is therefore a
/// working value, not a placeholder.
/// </para>
/// </remarks>
public sealed class CompendioOptions
{
    /// <summary>Resolved against the binary. Holds <c>content/</c>, <c>db/</c>, <c>logs/</c>, <c>keys/</c>.</summary>
    public string DataDir { get; set; } = "./data";

    public ContentOptions Content { get; set; } = new();

    public DatabaseOptions Database { get; set; } = new();

    public InstanceOptions Instance { get; set; } = new();

    public SearchOptions Search { get; set; } = new();

    public SecurityOptions Security { get; set; } = new();

    public TlsOptions Tls { get; set; } = new();

    public AttachmentOptions Attachments { get; set; } = new();

    public HistoryOptions History { get; set; } = new();

    public BootstrapOptions Bootstrap { get; set; } = new();

    public AppOptions App { get; set; } = new();

    public LifecycleOptions Lifecycle { get; set; } = new();

    /// <summary>Timeouts and limits only. The endpoint, model and key live in <c>Settings</c>.</summary>
    public AiOptions Ai { get; set; } = new();

    public GitMirrorOptions GitMirror { get; set; } = new();
}

public enum WatcherMode
{
    /// <summary>Native, falling back to polling on a network path or a buffer overflow.</summary>
    Auto = 0,
    Native = 1,
    Poll = 2,
}

public sealed class ContentOptions
{
    /// <summary>Defaults to <c>&lt;DataDir&gt;/content</c>.</summary>
    public string? Root { get; set; }

    public WatcherMode WatcherMode { get; set; } = WatcherMode.Auto;

    /// <summary>Polling interval when the native watcher is not trustworthy.</summary>
    public int PollSeconds { get; set; } = 30;

    /// <summary>Coalescing window for the several events one save produces.</summary>
    public int DebounceMilliseconds { get; set; } = 500;

    /// <summary>Window for correlating a delete and a create as one move, by content hash.</summary>
    public int RenameCorrelationSeconds { get; set; } = 2;

    /// <summary>
    /// How long the store remembers its own writes so they do not re-enter as external changes.
    /// Getting this wrong produces an index-rebuild loop that only appears under real use.
    /// </summary>
    public int OwnWriteWindowSeconds { get; set; } = 5;

    /// <summary>Refuse to read a page larger than this. A 50 MB "Markdown file" is a mistake.</summary>
    public long MaxPageBytes { get; set; } = 4 * 1024 * 1024;
}

public sealed class DatabaseOptions
{
    /// <summary>Defaults to <c>&lt;DataDir&gt;/db/compendio.db</c>, WAL.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Applied automatically on start, and idempotent.</summary>
    public bool AutoMigrate { get; set; } = true;

    /// <summary><c>VACUUM INTO</c> before migrating, so a failed upgrade is recoverable.</summary>
    public bool BackupBeforeMigrate { get; set; } = true;
}

public sealed class InstanceOptions
{
    public string Name { get; set; } = "Compendio";

    /// <summary>Spanish by decision; the user's own preference overrides it.</summary>
    public string DefaultLanguage { get; set; } = SupportedLanguages.Spanish;

    /// <summary>
    /// <see cref="PermissionLevel.Read"/> on a normal install, <see cref="PermissionLevel.None"/>
    /// on a locked-down one.
    /// </summary>
    public PermissionLevel DefaultAccess { get; set; } = PermissionLevel.Read;

    /// <summary>Forces one UI language for everyone. Some organizations want uniformity.</summary>
    public bool ForceSingleLanguage { get; set; }
}

public sealed class SearchOptions
{
    /// <summary>BM25 column weights: title, headings, body, tags, path.</summary>
    public SearchWeights Weights { get; set; } = new();

    /// <summary>Per-instance vocabulary fixes, applied at query time: <c>servidor=servidores=server</c>.</summary>
    public string[] Synonyms { get; set; } = [];

    /// <summary>Pages updated inside this window get a small ranking boost.</summary>
    public int RecencyBoostDays { get; set; } = 90;

    /// <summary>Multiplier for a page whose language matches the user's resolved UI language.</summary>
    public double LanguageBoost { get; set; } = 1.35;

    public double RecencyBoost { get; set; } = 1.15;
}

public sealed class SearchWeights
{
    public double Title { get; set; } = 10.0;

    public double Headings { get; set; } = 4.0;

    public double Body { get; set; } = 1.0;

    public double Tags { get; set; } = 6.0;

    public double Path { get; set; } = 2.0;
}

public sealed class SecurityOptions
{
    /// <summary>Adds HSTS and marks the auth cookie <c>Secure</c>.</summary>
    public bool RequireHttps { get; set; }

    public int LoginAttemptsPerMinute { get; set; } = 30;

    public int WritesPerMinute { get; set; } = 120;

    public int SearchesPerMinute { get; set; } = 120;

    /// <summary>Current OWASP guidance for PBKDF2-HMAC-SHA256. One line of configuration.</summary>
    public int PasswordIterations { get; set; } = 600_000;

    /// <summary>
    /// Optional. Wraps the master key with a key derived from this, for organizations whose threat
    /// model includes the OS disk. Once set, secure scopes will not open without it.
    /// </summary>
    public string? MasterPassphrase { get; set; }

    /// <summary>How long a deleted folder's ACL is kept before it stops being revivable.</summary>
    public int AclTombstoneDays { get; set; } = 30;
}

public sealed class TlsOptions
{
    /// <summary>Direct Kestrel TLS. False means plain HTTP, or a reverse proxy in front.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// A supplied PFX or PEM. Empty with <see cref="Enabled"/> uses the self-signed certificate in
    /// <c>&lt;data&gt;/keys/tls</c> that <c>compendio cert create</c> issues.
    /// </summary>
    public string? CertificatePath { get; set; }

    public string? CertificateKeyPath { get; set; }

    public string? CertificatePassword { get; set; }

    public int Port { get; set; } = 8443;
}

public sealed class AttachmentOptions
{
    public long MaxSizeBytes { get; set; } = 25 * 1024 * 1024;

    public int MaxPerPage { get; set; } = 50;

    /// <summary>
    /// Checked together with content-type sniffing — both, not either. Attachments are never served
    /// from a static file provider.
    /// </summary>
    public string[] AllowedTypes { get; set; } =
    [
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".avif",
        ".pdf", ".txt", ".csv", ".md", ".log",
        ".docx", ".xlsx", ".pptx", ".odt", ".ods",
        ".zip", ".json", ".yaml", ".yml", ".xml",
    ];
}

public sealed class HistoryOptions
{
    /// <summary>Keep everything this long, then thin to one version per day.</summary>
    public int RetentionDays { get; set; } = 365;

    /// <summary>Floor that thinning never goes below, however old the page is.</summary>
    public int MinVersionsKept { get; set; } = 20;

    /// <summary>How long a deleted page's versions survive — what saves an org from a bad sync.</summary>
    public int DeletedRetentionDays { get; set; } = 90;
}

public sealed class BootstrapOptions
{
    /// <summary>Optional. The setup wizard is the normal path; this is for unattended installs.</summary>
    public string? AdminUser { get; set; }

    public string? AdminPassword { get; set; }

    public string? AdminEmail { get; set; }
}

public sealed class AppOptions
{
    /// <summary>Empty means "show no link" rather than a broken one.</summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>Set when the app is hosted under a sub-path behind a reverse proxy.</summary>
    public string? BasePath { get; set; }
}

public sealed class LifecycleOptions
{
    /// <summary>How often the review scan runs. 0 disables it entirely.</summary>
    public int ReviewScanIntervalHours { get; set; } = 24;

    /// <summary>Notifications older than this are purged by the maintenance pass.</summary>
    public int NotificationRetentionDays { get; set; } = 90;

    /// <summary>How long an outstanding acknowledgment waits before it is called overdue.</summary>
    public int AcknowledgmentDueDays { get; set; } = 14;
}

public sealed class AiOptions
{
    /// <summary>
    /// A hard ceiling on one request. Generous, because a local model on CPU genuinely takes its
    /// time and cutting it off at thirty seconds would make Ollama look broken rather than slow.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>How many retrieved passages reach the prompt for "Ask the wiki".</summary>
    public int MaxContextPassages { get; set; } = 12;

    /// <summary>Characters of a page sent in one request. Keeps a 4 MB page from becoming a bill.</summary>
    public int MaxInputCharacters { get; set; } = 24_000;

    /// <summary>
    /// The per-person daily cap a fresh instance starts with, until an administrator sets one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately a real number rather than "unlimited". The endpoint the admin pastes in may be
    /// metered, and the failure this guards against is not malice — it is a page loop, a retried
    /// batch, or somebody discovering the button. Generous enough that no ordinary day hits it: an
    /// editor improving, summarizing and translating their way through a dozen pages spends about
    /// forty.
    /// </para>
    /// <para>
    /// The cap is on requests rather than tokens because requests are what the product can count
    /// honestly. An OpenAI-compatible endpoint reports token usage inconsistently and Ollama's
    /// numbers mean nothing financially, so a token budget would be a number that looks precise and
    /// is not. <see cref="MaxInputCharacters"/> bounds the size of each request instead.
    /// </para>
    /// </remarks>
    public int DefaultDailyPerUser { get; set; } = 50;

    /// <summary>
    /// The instance-wide daily cap a fresh instance starts with. 0 — no second ceiling by default,
    /// because the per-person cap already bounds the common failure and a whole-instance limit that
    /// nobody chose would stop the wiki working for everyone at once.
    /// </summary>
    public int DefaultDailyPerInstance { get; set; }

    /// <summary>
    /// How long a usage row is kept. Only the last 24 hours are ever counted; the rest is kept
    /// briefly so the admin screen can say who spent the budget, and then pruned.
    /// </summary>
    public int UsageRetentionDays { get; set; } = 30;
}

public sealed class GitMirrorOptions
{
    /// <summary>Off by default. Optional is what makes shelling out to <c>git</c> acceptable.</summary>
    public bool Enabled { get; set; }

    public string? RemoteUrl { get; set; }

    public string Branch { get; set; } = "main";

    public int IntervalMinutes { get; set; } = 60;

    public string CommitName { get; set; } = "Compendio";

    public string CommitEmail { get; set; } = "compendio@localhost";

    /// <summary>A push that hangs on a credential prompt must not hang the service.</summary>
    public int TimeoutSeconds { get; set; } = 120;
}
