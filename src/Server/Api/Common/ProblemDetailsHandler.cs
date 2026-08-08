using Compendio.Application.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Compendio.Api.Common;

/// <summary>
/// Turns every exception into a <c>ProblemDetails</c> with a stable machine code and localized text.
/// </summary>
/// <remarks>
/// One handler rather than try/catch in endpoints: endpoints bind and dispatch, they do not decide.
/// The <c>code</c> extension is what clients and logs key on, and it never changes with the
/// caller's language — the title and detail beside it always do.
/// </remarks>
public sealed class ProblemDetailsHandler(ILogger<ProblemDetailsHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var language = context.Language();

        var problem = exception switch
        {
            ContentConflictException conflict => Conflict(conflict, language),
            ValidationException validation => Validation(validation, language),
            CompendioException known => Known(known, language),
            BadHttpRequestException bad => new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = LocalizedText.Get("validation.failed.title", language),
                Detail = bad.Message,
                Extensions = { ["code"] = ProblemCodes.ValidationFailed },
            },
            OperationCanceledException => null,
            _ => null,
        };

        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
        {
            // The caller went away. Not an error, and not worth a log line.
            return true;
        }

        if (problem is null)
        {
            // Unexpected: log with the full exception, return nothing that describes the internals.
            logger.LogError(exception, "Unhandled exception on {Method} {Path}.", context.Request.Method, context.Request.Path);

            problem = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = LocalizedText.Get("error.unexpected.title", language),
                Detail = LocalizedText.Get("error.unexpected.detail", language),
                Extensions = { ["code"] = "error.unexpected" },
            };
        }
        else
        {
            // Expected failures are logged by code, never by localized text.
            logger.LogInformation("{Code} on {Method} {Path}.", problem.Extensions["code"], context.Request.Method, context.Request.Path);
        }

        problem.Instance = context.Request.Path;
        context.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(problem, cancellationToken);
        return true;
    }

    private static ProblemDetails Known(CompendioException exception, string language)
    {
        var problem = new ProblemDetails
        {
            Status = exception.StatusCode,
            Title = LocalizedText.Get($"{exception.Code}.title", language),
            Detail = LocalizedText.Get($"{exception.Code}.detail", language, [.. exception.Arguments]),
            Extensions = { ["code"] = exception.Code },
        };

        foreach (var (key, value) in exception.Extensions)
        {
            problem.Extensions[key] = value;
        }

        return problem;
    }

    /// <summary>
    /// A conflict carries both versions, because the client turns it into a three-pane merge rather
    /// than an alert box. This is the moment a user could lose an hour's work.
    /// </summary>
    private static ProblemDetails Conflict(ContentConflictException exception, string language) =>
        new()
        {
            Status = StatusCodes.Status409Conflict,
            Title = LocalizedText.Get("page.conflict.title", language),
            Detail = LocalizedText.Get("page.conflict.detail", language),
            Extensions =
            {
                ["code"] = ProblemCodes.PageConflict,
                ["path"] = exception.Path.Value,
                ["expectedHash"] = exception.ExpectedHash,
                ["actualHash"] = exception.ActualHash,
                ["currentContent"] = exception.CurrentContent,
            },
        };

    private static ProblemDetails Validation(ValidationException exception, string language) =>
        new()
        {
            Status = StatusCodes.Status400BadRequest,
            Title = LocalizedText.Get("validation.failed.title", language),
            Detail = LocalizedText.Get("validation.failed.detail", language),
            Extensions =
            {
                ["code"] = ProblemCodes.ValidationFailed,
                ["errors"] = exception.Errors,
            },
        };
}
