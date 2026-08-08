using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Common.Mediator;
using Compendio.Api.Common;
using Compendio.Api.Endpoints;
using Compendio.Application.Abstractions;
using Compendio.Application.Pages;
using Compendio.Domain;
using Compendio.Domain.Security;
using Compendio.Engine;
using Compendio.Hosting.Configuration;
using Compendio.Infrastructure.Common;
using Compendio.Infrastructure.Content;
using Compendio.Infrastructure.Crypto;
using Compendio.Infrastructure.History;
using Compendio.Infrastructure.Identity;
using Compendio.Infrastructure.Persistence;
using Compendio.Infrastructure.Search;
using Compendio.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Compendio.Hosting;

/// <summary>
/// Everything the container needs. Kept out of <c>Program.cs</c> so that file stays a wiring file.
/// </summary>
public static class ServiceRegistration
{
    public static void AddCompendio(this WebApplicationBuilder builder, DataDirectory dataDirectory, CompendioOptions options)
    {
        var services = builder.Services;

        services.Configure<CompendioOptions>(builder.Configuration);
        services.AddSingleton(dataDirectory);
        services.AddSingleton(sp => new StartupGuards(dataDirectory, sp.GetRequiredService<IOptions<CompendioOptions>>().Value));

        AddPersistence(services, dataDirectory, options);
        AddIdentity(services, options);
        AddDataProtection(services, dataDirectory);
        AddDomainServices(services, dataDirectory);
        AddEngine(services);
        AddApi(services, options);
    }

    /// <summary>
    /// The same services minus the web pipeline and the background workers.
    /// </summary>
    /// <remarks>
    /// CLI verbs share the real implementations rather than a parallel set — <c>doctor</c> reporting
    /// something different from what the running service would do defeats the point of running it.
    /// The engine is left out because a CLI verb must not start a watcher.
    /// </remarks>
    public static void AddCompendioForCli(
        this IServiceCollection services,
        IConfiguration configuration,
        DataDirectory dataDirectory,
        CompendioOptions options)
    {
        services.Configure<CompendioOptions>(configuration);
        services.AddSingleton(dataDirectory);
        services.AddSingleton(sp => new StartupGuards(dataDirectory, sp.GetRequiredService<IOptions<CompendioOptions>>().Value));
        services.AddLogging();

        AddPersistence(services, dataDirectory, options);
        AddIdentity(services, options);
        AddDataProtection(services, dataDirectory);
        AddDomainServices(services, dataDirectory);

        // No HttpContext outside a request, so the CLI acts as an unauthenticated system caller and
        // every verb that touches content does so through an explicitly system-level path.
        services.AddSingleton<ICurrentUser, SystemUser>();
        services.AddMediator(mediator => mediator.RegisterServicesFromAssembly(typeof(ServiceRegistration).Assembly));
    }

    private static void AddPersistence(IServiceCollection services, DataDirectory dataDirectory, CompendioOptions options)
    {
        var connectionString = dataDirectory.ConnectionString(options.Database);

        // A factory as well as a scoped context: background services have no request scope, and
        // handing them a scoped context is how DbContext concurrency bugs start.
        services.AddDbContextFactory<CompendioDbContext>(db =>
        {
            db.UseSqlite(connectionString, sqlite => sqlite.CommandTimeout(30));
            db.EnableDetailedErrors();
        });

        services.AddScoped<CompendioDbContext>(sp =>
            sp.GetRequiredService<IDbContextFactory<CompendioDbContext>>().CreateDbContext());

        services.AddScoped<ICompendioDbContext>(sp => sp.GetRequiredService<CompendioDbContext>());
    }

    private static void AddIdentity(IServiceCollection services, CompendioOptions options)
    {
        services.AddIdentityCore<CompendioUser>(identity =>
            {
                identity.User.RequireUniqueEmail = false;
                identity.Password.RequiredLength = 12;
                identity.Password.RequireNonAlphanumeric = false;
                identity.Password.RequireUppercase = false;
                identity.Password.RequireLowercase = false;
                identity.Password.RequireDigit = false;
                identity.Lockout.MaxFailedAccessAttempts = 10;
                identity.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
                identity.SignIn.RequireConfirmedAccount = false;
            })
            .AddRoles<CompendioIdentityRole>()
            .AddEntityFrameworkStores<CompendioDbContext>()
            .AddClaimsPrincipalFactory<CompendioClaimsFactory>()
            .AddSignInManager()
            .AddDefaultTokenProviders();

        // PBKDF2-HMAC-SHA256 at the configured iteration count. No Argon2 package: a native
        // dependency here would fight single-file publishing for a marginal gain.
        services.Configure<PasswordHasherOptions>(hasher =>
        {
            hasher.IterationCount = options.Security.PasswordIterations;
            hasher.CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3;
        });

        services.AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddCookie(IdentityConstants.ApplicationScheme, cookie =>
            {
                cookie.Cookie.Name = CompendioConstants.AuthenticationCookieName;
                cookie.Cookie.HttpOnly = true;
                // SameSite=Strict with a same-origin SPA and CORS disabled *is* the CSRF posture.
                // There is deliberately no anti-forgery token machinery on top.
                cookie.Cookie.SameSite = SameSiteMode.Strict;
                cookie.Cookie.SecurePolicy = options.Security.RequireHttps
                    ? CookieSecurePolicy.Always
                    : CookieSecurePolicy.SameAsRequest;
                cookie.SlidingExpiration = true;
                cookie.ExpireTimeSpan = TimeSpan.FromDays(14);

                // An API returns 401/403, never a redirect to an HTML login page.
                cookie.Events.OnRedirectToLogin = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };

                cookie.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(AdminEndpoints.AdminPolicy, policy =>
                policy.RequireClaim(CompendioClaims.Role, nameof(UserRole.Admin)));

        services.AddScoped<IAccountService, AccountService>();
    }

    /// <summary>
    /// Persists the Data Protection key ring to <c>&lt;data&gt;/keys</c>.
    /// </summary>
    /// <remarks>
    /// Not optional and not incidental. A service account with no home directory makes ASP.NET Core
    /// fall back to an in-memory key ring, which signs every user out on every restart — and it
    /// shows up only in the deployed configurations, never in development. There is a test that
    /// asserts the files land here.
    /// </remarks>
    private static void AddDataProtection(IServiceCollection services, DataDirectory dataDirectory) =>
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(dataDirectory.DataProtectionKeys))
            .SetApplicationName(CompendioConstants.ProductName);

    private static void AddDomainServices(IServiceCollection services, DataDirectory dataDirectory)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IPathPolicy>(new PathPolicyService(dataDirectory.Content));
        services.AddSingleton<Application.Abstractions.IMarkdownRenderer, MarkdownRenderer>();
        services.AddSingleton<ITextExtractor, TextExtractor>();

        services.AddSingleton<IInstanceSettings, InstanceSettings>();
        services.AddSingleton<MasterKeyStore>();
        services.AddSingleton<ISecureScopeRegistry, SecureScopeRegistry>();
        services.AddSingleton<IContentCrypto, ContentCrypto>();

        // Singletons because they own caches that must be shared: the store's own-write window and
        // the evaluator's snapshot are worthless if every request gets a fresh copy.
        services.AddSingleton<IContentStore, ContentStore>();
        services.AddSingleton<IPermissionEvaluator, PermissionEvaluator>();

        services.AddScoped<IUserDirectory, UserDirectory>();
        services.AddScoped<INotificationWriter, Infrastructure.Notifications.NotificationWriter>();

        // Lifecycle. Small collaborators rather than one service with twelve methods: each is used
        // by two or three handlers, and sharing them is what stops the stale banner, the report and
        // the dashboard from developing three different opinions about what "stale" means.
        services.AddScoped<Application.Common.ReadablePages>();
        services.AddScoped<Application.Lifecycle.OwnerResolver>();
        services.AddScoped<Application.Lifecycle.LifecycleProjection>();
        services.AddScoped<Application.Lifecycle.PageMetadataWriter>();
        services.AddScoped<Application.Notifications.NotificationAccessFilter>();
        services.AddScoped<Application.Acknowledgments.AcknowledgmentRounds>();
        services.AddScoped<Application.Acknowledgments.OutstandingAcknowledgments>();

        // AI. Every one of these is inert until an admin configures a base URL and a model: the
        // settings store reports disabled, the guard turns that into 404 ai.disabled, and the client
        // renders no control. Registering them unconditionally is what lets configuration take
        // effect without a restart.
        services.AddSingleton<IAiSettings, Infrastructure.Ai.AiSettingsStore>();
        services.AddScoped<IAiProvider, Infrastructure.Ai.OpenAiCompatibleClient>();
        services.AddScoped<IAiRetrieval, Infrastructure.Ai.FtsRetrieval>();
        services.AddScoped<Application.Ai.AiGuard>();
        services.AddScoped<Application.Ai.AiBudget>();
        services.AddScoped<Application.Ai.AiTextActions>();
        services.AddHttpClient(Infrastructure.Ai.OpenAiCompatibleClient.HttpClientName);

        // Git mirror. Registered whether or not it is enabled so the admin screen can report that
        // it is off, and whether git is even on PATH, without a restart.
        services.AddSingleton<Infrastructure.GitMirror.GitCli>();
        services.AddScoped<Infrastructure.GitMirror.GitMirrorRunner>();
        services.AddScoped<IGitMirror>(sp => sp.GetRequiredService<Infrastructure.GitMirror.GitMirrorRunner>());
        // Singleton, not scoped. It holds one IDataProtector built from a singleton provider and has
        // no per-request state, and the AI settings cache above it is a singleton — a scoped
        // dependency there is a captive dependency that fails container validation, which is on by
        // default in Development. That is a crash on `dotnet run` and nowhere else.
        services.AddSingleton<ISecretProtector, SecretProtector>();
        services.AddScoped<ISearchIndex, SearchIndex>();
        services.AddScoped<IPageHistory, PageHistory>();
        services.AddScoped<IContentPipeline, ContentPipeline>();
        services.AddScoped<Reconciler>();

        // Registered here rather than with the engine so a CLI verb and a test can run one pass
        // directly. The hosted service is only the clock.
        services.AddScoped<ReviewScan>();
        services.AddScoped<AcknowledgmentScan>();
        services.AddScoped<ChangeNotifier>();
        services.AddScoped<PageProjection>();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
    }

    /// <summary>
    /// The identity CLI verbs run as: administrator, so a console operator with the data directory
    /// is not blocked by ACLs they could edit anyway.
    /// </summary>
    private sealed class SystemUser : ICurrentUser
    {
        public bool IsAuthenticated => true;

        public Guid UserId => Guid.Empty;

        public string? UserName => "system";

        public UserRole Role => UserRole.Admin;

        public IReadOnlySet<Guid> GroupIds { get; } = new HashSet<Guid>();

        public string Language => Domain.Localization.SupportedLanguages.Fallback;
    }

    private static void AddEngine(IServiceCollection services)
    {
        services.AddHostedService<ContentWatcher>();
        services.AddHostedService<IndexerService>();
        services.AddHostedService<MaintenanceService>();
        services.AddHostedService<LifecycleService>();
        services.AddHostedService<GitMirrorService>();
    }

    private static void AddApi(IServiceCollection services, CompendioOptions options)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<SpaShell>();
        services.AddExceptionHandler<ProblemDetailsHandler>();
        services.AddProblemDetails();
        services.AddResponseCompression(compression => compression.EnableForHttps = options.Security.RequireHttps);
        services.AddOpenApi();

        services.AddMediator(mediator => mediator.RegisterServicesFromAssembly(typeof(ServiceRegistration).Assembly));

        services.ConfigureHttpJsonOptions(json =>
        {
            // Enums travel as strings, and System.Text.Json matches them case-insensitively on the
            // way in — so a hand-typed or lower-cased value is accepted rather than becoming a
            // bodyless 400.
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            json.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });

        // CORS is not configured at all. The SPA is same-origin; enabling CORS would only widen the
        // surface for no functional gain.
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            limiter.AddPolicy(AuthEndpoints.LoginRateLimitPolicy, context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = options.Security.LoginAttemptsPerMinute,
                        Window = TimeSpan.FromMinutes(1),
                    }));

            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                if (!context.Request.Path.StartsWithSegments("/api"))
                {
                    return RateLimitPartition.GetNoLimiter("static");
                }

                var isWrite = context.Request.Method is "POST" or "PUT" or "DELETE" or "PATCH";
                var key = context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";

                return RateLimitPartition.GetFixedWindowLimiter($"{(isWrite ? "w" : "r")}:{key}",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = isWrite ? options.Security.WritesPerMinute : options.Security.SearchesPerMinute * 4,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    });
            });
        });
    }
}
