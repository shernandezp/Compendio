using Common.Mediator;
using Compendio.Api.Common;
using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Application.Setup;
using Compendio.Domain;
using Compendio.Domain.Localization;
using Microsoft.AspNetCore.RateLimiting;

namespace Compendio.Api.Endpoints;

public static class AuthEndpoints
{
    public const string LoginRateLimitPolicy = "login";

    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("Auth");

        group.MapPost("/login", async (LoginRequest request, IAccountService accounts, CancellationToken ct) =>
            {
                var result = await accounts.SignInAsync(request.UserName, request.Password, request.Persistent, ct);

                if (!result.Succeeded)
                {
                    throw new CompendioException(result.FailureCode ?? ProblemCodes.AuthFailed, StatusCodes.Status401Unauthorized);
                }

                return Results.Ok(result.User);
            })
            .RequireRateLimiting(LoginRateLimitPolicy)
            .WithName("Login");

        group.MapPost("/logout", async (IAccountService accounts, CancellationToken ct) =>
            {
                await accounts.SignOutAsync(ct);
                return Results.NoContent();
            })
            .RequireAuthorization()
            .WithName("Logout");

        group.MapGet("/me", async (ICurrentUser currentUser, IAccountService accounts, ISender sender, CancellationToken ct) =>
            {
                if (!currentUser.IsAuthenticated)
                {
                    // Not an error: the SPA asks this on boot to decide between login and the app.
                    var state = await sender.Send(new GetSetupStateQuery(), ct);
                    return Results.Ok(new { authenticated = false, needsSetup = state.NeedsSetup, language = currentUser.Language });
                }

                var user = await accounts.GetAsync(currentUser.UserId, ct);
                return Results.Ok(new
                {
                    authenticated = true,
                    needsSetup = false,
                    language = currentUser.Language,
                    user,
                });
            })
            .WithName("GetCurrentUser");

        group.MapPut("/profile", async (
                UpdateProfileRequest request,
                ICurrentUser currentUser,
                IAccountService accounts,
                HttpContext http,
                CancellationToken ct) =>
            {
                var language = request.PreferredLanguage is null
                    ? null
                    : SupportedLanguages.TryResolve(request.PreferredLanguage, out var resolved) ? resolved : string.Empty;

                var updated = await accounts.UpdateAsync(
                    currentUser.UserId, request.DisplayName, request.Email,
                    role: null, active: null, preferredLanguage: language, ct);

                if (!string.IsNullOrEmpty(language))
                {
                    // Keep the cookie in step so the very next request is already in the new
                    // language, rather than the one after the auth cookie is reissued.
                    http.Response.Cookies.Append(CompendioConstants.LanguageCookieName, language, new CookieOptions
                    {
                        HttpOnly = false,
                        SameSite = SameSiteMode.Strict,
                        IsEssential = true,
                        MaxAge = TimeSpan.FromDays(365),
                        Path = "/",
                    });
                }

                return Results.Ok(updated);
            })
            .RequireAuthorization()
            .WithName("UpdateProfile");

        group.MapPost("/password", async (
                ChangePasswordRequest request,
                ICurrentUser currentUser,
                IAccountService accounts,
                CancellationToken ct) =>
            {
                await accounts.ChangePasswordAsync(
                    currentUser.UserId, request.CurrentPassword, request.NewPassword, requireCurrent: true, ct);

                return Results.NoContent();
            })
            .RequireAuthorization()
            .RequireRateLimiting(LoginRateLimitPolicy)
            .WithName("ChangePassword");
    }

    /// <summary>
    /// The setup wizard. Reachable only while no user exists.
    /// </summary>
    /// <remarks>
    /// Anonymous by necessity — there is nobody to authenticate as yet — and closed the moment an
    /// admin exists, with <c>setup.completed</c> rather than a silent no-op.
    /// </remarks>
    public static void MapSetup(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/setup").WithTags("Setup").AllowAnonymous();

        group.MapGet("/state", async (ISender sender, CancellationToken ct) =>
            Results.Ok(await sender.Send(new GetSetupStateQuery(), ct)));

        group.MapPost("/", async (CompleteSetupCommand command, ISender sender, CancellationToken ct) =>
                Results.Ok(await sender.Send(command, ct)))
            .RequireRateLimiting(LoginRateLimitPolicy);
    }
}

public sealed record LoginRequest(string UserName, string Password, bool Persistent = false);

public sealed record UpdateProfileRequest(string? DisplayName, string? Email, string? PreferredLanguage);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
