using Compendio.Domain.Content;

namespace Compendio.Application.Common;

/// <summary>
/// An expected failure that maps to a <c>ProblemDetails</c> response.
/// </summary>
/// <remarks>
/// Handlers throw these; the endpoint layer never decides status codes on its own. Carrying the
/// stable code plus loose arguments means the localized title and detail are looked up at the edge,
/// where the caller's language is known, rather than baked in here.
/// </remarks>
public class CompendioException(string code, int statusCode, params object[] arguments) : Exception(code)
{
    public string Code { get; } = code;

    public int StatusCode { get; } = statusCode;

    /// <summary>Format arguments for the localized detail string.</summary>
    public IReadOnlyList<object> Arguments { get; } = arguments;

    /// <summary>Extra members merged into the ProblemDetails body.</summary>
    public IDictionary<string, object?> Extensions { get; } = new Dictionary<string, object?>();

    public static CompendioException NotFound(ContentPath path) =>
        new(ProblemCodes.PageNotFound, StatusCodes.Status404NotFound, path.Value);

    public static CompendioException Forbidden(ContentPath path) =>
        new(ProblemCodes.PageForbidden, StatusCodes.Status403Forbidden, path.Value);

    public static CompendioException SecureAdminRequired(ContentPath path) =>
        new(ProblemCodes.SecureAdminRequired, StatusCodes.Status403Forbidden, path.Value);

    public static CompendioException SecureUnavailable(string scope) =>
        new(ProblemCodes.SecureUnavailable, StatusCodes.Status503ServiceUnavailable, scope);

    public static CompendioException Tampered(ContentPath path) =>
        new(ProblemCodes.SecureTampered, StatusCodes.Status422UnprocessableEntity, path.Value);

    public static CompendioException InvalidPath(PathRule rule) =>
        new(rule == PathRule.TooLong ? ProblemCodes.PathTooLong : ProblemCodes.PathInvalid,
            StatusCodes.Status400BadRequest,
            PathPolicy.RuleKey(rule));

    public static CompendioException Exists(ContentPath path) =>
        new(ProblemCodes.PathExists, StatusCodes.Status400BadRequest, path.Value);

    public static CompendioException LastAdmin() =>
        new(ProblemCodes.AclLastAdmin, StatusCodes.Status400BadRequest);

    public static CompendioException BadRequest(string code, params object[] arguments) =>
        new(code, StatusCodes.Status400BadRequest, arguments);
}

/// <summary>
/// A write lost a race with another writer or with the file system.
/// </summary>
/// <remarks>
/// Carries both versions, because the client turns this into a three-pane merge — the moment a user
/// could lose an hour's work is not a moment for an alert box.
/// </remarks>
public sealed class ContentConflictException(ContentPath path, string expectedHash, string actualHash, string currentContent)
    : CompendioException(ProblemCodes.PageConflict, StatusCodes.Status409Conflict, path.Value)
{
    public ContentPath Path { get; } = path;

    public string ExpectedHash { get; } = expectedHash;

    public string ActualHash { get; } = actualHash;

    public string CurrentContent { get; } = currentContent;
}

/// <summary>Field-level validation failure, produced by the validation pipeline behaviour.</summary>
public sealed class ValidationException(IReadOnlyDictionary<string, string[]> errors)
    : CompendioException(ProblemCodes.ValidationFailed, StatusCodes.Status400BadRequest)
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}
