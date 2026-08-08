using System.Text;
using Common.Mediator;
using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Domain;
using Compendio.Domain.Content;
using Compendio.Domain.Entities;
using Compendio.Domain.Localization;
using Compendio.Domain.Security;
using Compendio.Hosting.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Compendio.Application.Setup;

public sealed record GetSetupStateQuery : IQuery<SetupStateDto>;

public sealed class GetSetupStateHandler(
    IAccountService accounts,
    IPathPolicy paths,
    IInstanceSettings instance) : IRequestHandler<GetSetupStateQuery, SetupStateDto>
{
    public async Task<SetupStateDto> Handle(GetSetupStateQuery request, CancellationToken cancellationToken = default) =>
        new(
            NeedsSetup: !await accounts.AnyUserExistsAsync(cancellationToken),
            DefaultLanguage: instance.DefaultLanguage,
            Languages: SupportedLanguages.Shipping
                .Select(l => new LanguageDto(l.Code, l.EnglishName, l.NativeName))
                .ToArray(),
            ContentRoot: paths.ContentRoot);
}

/// <param name="Language">
/// Chosen first, before the admin account, so the wizard itself is in the admin's language. A
/// wizard that is English-only sets the tone before the product is even installed.
/// </param>
/// <param name="DefaultAccess">
/// <c>Read</c> on a normal install — a fresh instance is readable by all authenticated users —
/// or <c>None</c> for a locked-down one.
/// </param>
public sealed record CompleteSetupCommand(
    string Language,
    string AdminUserName,
    string AdminPassword,
    string AdminDisplayName,
    string? AdminEmail,
    string? InstanceName,
    PermissionLevel DefaultAccess) : ICommand<UserDto>;

/// <summary>
/// The one-time first-run wizard.
/// </summary>
/// <remarks>
/// Reachable only while no user exists, and it says so with <c>setup.completed</c> rather than
/// quietly doing nothing. It also writes the first page, because an empty wiki gives a new admin
/// nothing to react to — and that page is a real file on disk, which is the fastest way to
/// demonstrate what the product actually is.
/// </remarks>
public sealed class CompleteSetupHandler(
    IAccountService accounts,
    ICompendioDbContext db,
    IContentPipeline pipeline,
    IContentStore store,
    IPathPolicy paths,
    IClock clock,
    IInstanceSettings instance,
    IPermissionEvaluator permissions,
    ILogger<CompleteSetupHandler> logger) : IRequestHandler<CompleteSetupCommand, UserDto>
{
    public async Task<UserDto> Handle(CompleteSetupCommand request, CancellationToken cancellationToken = default)
    {
        if (await accounts.AnyUserExistsAsync(cancellationToken))
        {
            throw new CompendioException(ProblemCodes.SetupCompleted, StatusCodes.Status409Conflict);
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(request.AdminUserName))
        {
            errors["adminUserName"] = ["required"];
        }

        if (string.IsNullOrWhiteSpace(request.AdminPassword) || request.AdminPassword.Length < 12)
        {
            errors["adminPassword"] = ["tooShort"];
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        var language = SupportedLanguages.ResolveOrFallback(request.Language, instance.DefaultLanguage);

        var admin = await accounts.CreateAsync(new CreateUserRequest(
            request.AdminUserName,
            request.AdminPassword,
            string.IsNullOrWhiteSpace(request.AdminDisplayName) ? request.AdminUserName : request.AdminDisplayName,
            request.AdminEmail,
            UserRole.Admin,
            language), cancellationToken);

        await SetAsync(SettingKeys.InstanceDefaultLanguage, language, cancellationToken);
        await SetAsync(SettingKeys.InstanceDefaultAccess, request.DefaultAccess.ToString(), cancellationToken);
        await SetAsync(SettingKeys.InstanceName, request.InstanceName ?? CompendioConstants.ProductName, cancellationToken);
        await SetAsync(SettingKeys.SetupCompletedAt, clock.UtcNow.ToString("O"), cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        // The wizard's answers become effective now, not at the next restart. Both caches read
        // these settings, and an admin who chose "nobody can read by default" must not get
        // "everyone" until somebody bounces the service.
        instance.Invalidate();
        permissions.Invalidate();

        await SeedFirstPageAsync(language, admin.Id, cancellationToken);

        logger.LogInformation("Setup completed. Instance language is {Language}.", language);
        return admin;
    }

    /// <summary>
    /// Writes a welcome page — a real Markdown file, which is the point it is making.
    /// </summary>
    private async Task SeedFirstPageAsync(string language, Guid adminId, CancellationToken cancellationToken)
    {
        var path = paths.Require("welcome.md", PathKind.Page);
        if (store.Exists(path))
        {
            return;
        }

        var frontMatter = new FrontMatter
        {
            Title = Api.Common.LocalizedText.Get("setup.firstPage.title", language),
            Lang = language,
            Tags = ["compendio"],
        };

        var body = Api.Common.LocalizedText.Get("setup.firstPage.body", language);
        var markdown = MarkdownParser.Compose(frontMatter, body + "\n", "\n");
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(markdown);

        await pipeline.SavePageAsync(path, bytes, expectedHash: null, adminId,
            VersionSource.Editor, note: null, cancellationToken);
    }

    private async Task SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        var existing = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (existing is null)
        {
            db.Settings.Add(new Setting { Key = key, Value = value, UpdatedAt = clock.UtcNow });
        }
        else
        {
            existing.Value = value;
            existing.UpdatedAt = clock.UtcNow;
        }
    }
}
