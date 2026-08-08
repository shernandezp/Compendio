namespace Compendio.Application.Common;

/// <summary>
/// Stable machine codes for <c>ProblemDetails</c>.
/// </summary>
/// <remarks>
/// The code is what logs and clients key on; the <c>title</c> and <c>detail</c> beside it are
/// localized. Logs record the code and never the localized text, so a Spanish instance's logs stay
/// greppable and pasteable into a GitHub issue.
/// </remarks>
public static class ProblemCodes
{
    /// <summary>409 — content hash mismatch. The body carries both versions.</summary>
    public const string PageConflict = "page.conflict";

    /// <summary>
    /// 404 — absent <em>or</em> unreadable, deliberately indistinguishable. A 403 here would
    /// confirm the page exists, which is exactly the leak the tree and search avoid.
    /// </summary>
    public const string PageNotFound = "page.not_found";

    /// <summary>403 — readable but not writable.</summary>
    public const string PageForbidden = "page.forbidden";

    /// <summary>400 — failed <c>PathPolicy</c>. The detail names which rule.</summary>
    public const string PathInvalid = "path.invalid";

    public const string PathTooLong = "path.too_long";

    /// <summary>400 — a page or folder already exists at the target path.</summary>
    public const string PathExists = "path.exists";

    /// <summary>403 — write attempt inside a secure scope by a non-admin.</summary>
    public const string SecureAdminRequired = "secure.admin_required";

    /// <summary>503 — key missing or unwrappable. Non-secure content keeps serving.</summary>
    public const string SecureUnavailable = "secure.unavailable";

    /// <summary>422 — envelope failed authentication. Never partially rendered.</summary>
    public const string SecureTampered = "secure.tampered";

    /// <summary>400 — a secure scope inside a secure scope.</summary>
    public const string SecureNested = "secure.nested";

    public const string AttachmentTooLarge = "attachment.too_large";
    public const string AttachmentTypeNotAllowed = "attachment.type_not_allowed";
    public const string AttachmentLimitReached = "attachment.limit_reached";

    /// <summary>400 — unparseable query. Rare: the parser is forgiving by design.</summary>
    public const string SearchQueryInvalid = "search.query_invalid";

    /// <summary>400 — would remove or demote the last admin, by any path.</summary>
    public const string AclLastAdmin = "acl.last_admin";

    public const string AclInvalidSubject = "acl.invalid_subject";

    /// <summary>409 — setup attempted after an admin exists.</summary>
    public const string SetupCompleted = "setup.completed";

    /// <summary>400 — the request failed validation. The detail lists the fields.</summary>
    public const string ValidationFailed = "validation.failed";

    /// <summary>401 — not signed in, or the credentials were wrong.</summary>
    public const string AuthFailed = "auth.failed";

    /// <summary>400 — the new password is the one already in use.</summary>
    public const string AuthPasswordReused = "auth.password_reused";

    /// <summary>429 — rate limited.</summary>
    public const string RateLimited = "request.rate_limited";

    /// <summary>404 — the version does not exist, or its page is unreadable.</summary>
    public const string VersionNotFound = "version.not_found";

    // ---- v1: lifecycle -----------------------------------------------------------------------------

    /// <summary>400 — the page does not require acknowledgment, so there is nothing to confirm.</summary>
    public const string AcknowledgmentNotRequired = "ack.not_required";

    /// <summary>409 — the acknowledged version is no longer the one in force.</summary>
    public const string AcknowledgmentVersionMismatch = "ack.version_mismatch";

    // ---- v1: AI ------------------------------------------------------------------------------------

    /// <summary>
    /// 404 — no AI provider is configured. A 404 rather than a 501: with nothing configured the
    /// action genuinely does not exist, and the client renders no control that could reach it.
    /// </summary>
    public const string AiDisabled = "ai.disabled";

    /// <summary>502 — the provider errored or answered with something unusable. Never carries the key.</summary>
    public const string AiProviderError = "ai.provider_error";

    /// <summary>504 — the provider exceeded <c>Ai:TimeoutSeconds</c>.</summary>
    public const string AiTimeout = "ai.timeout";

    /// <summary>403 — outside the allowed spaces, or a secure scope that has not opted in.</summary>
    public const string AiNotAllowedHere = "ai.not_allowed_here";

    /// <summary>
    /// 429 — this person has spent their own daily AI budget.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="RateLimited"/>, which is a per-minute burst guard on every endpoint.
    /// This one is about money: it is measured over a rolling day and an administrator sets it.
    /// Sharing a code would leave the client unable to tell "slow down" from "come back later".
    /// </remarks>
    public const string AiQuotaExceeded = "ai.quota_exceeded";

    /// <summary>
    /// 429 — the whole instance has spent its daily AI budget.
    /// </summary>
    /// <remarks>
    /// Its own code because the user's options are different: nothing they do frees this up, and the
    /// message has to say so rather than implying they were greedy.
    /// </remarks>
    public const string AiQuotaExceededInstance = "ai.quota_exceeded_instance";

    // ---- v1: git mirror ----------------------------------------------------------------------------

    /// <summary>503 — <c>git</c> is not on <c>PATH</c>, or the remote rejected the push.</summary>
    public const string GitUnavailable = "git.unavailable";

    // ---- v1: backup --------------------------------------------------------------------------------

    /// <summary>
    /// 400 — a backup was requested without a passphrase while encrypted folders exist. Without one
    /// the archive would either omit the key (restores into garbage) or store it beside the
    /// ciphertext (gives away the encryption), so the request is refused.
    /// </summary>
    public const string BackupPassphraseRequired = "backup.passphrase_required";

    /// <summary>500 — the master key could not be read, so no restorable backup can be made.</summary>
    public const string BackupKeyUnavailable = "backup.key_unavailable";
}
