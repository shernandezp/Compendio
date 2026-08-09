using System.Security.Cryptography.X509Certificates;
using Compendio.Api.Common;
using Compendio.Api.Endpoints;
using Compendio.Application.Abstractions;
using Compendio.Domain;
using Compendio.Domain.Security;
using Compendio.Engine;
using Compendio.Hosting.Configuration;
using Compendio.Infrastructure.Identity;
using Compendio.Infrastructure.Persistence;
using Compendio.Infrastructure.Search;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace Compendio.Hosting;

/// <summary>
/// Builds the application, or runs a CLI verb and returns null.
/// </summary>
/// <remarks>
/// One binary, three install modes, and the same behaviour in all of them. Everything that differs
/// between a console run, a Windows Service and a systemd unit is decided here, so nothing further
/// down has to know which it is.
/// </remarks>
public static class CompendioHost
{
    public static WebApplication? Build(string[] args)
    {
        if (CompendioCli.IsCliVerb(args))
        {
            CompendioCli.Run(args);
            return null;
        }

        // `compendio run` is the documented spelling of "start the server", alongside install and
        // uninstall. It is what no arguments already does, so the word is simply dropped — left in,
        // the configuration binder rejects it as an unrecognized argument and the server refuses to
        // start for somebody who typed exactly what the docs told them to.
        args = CompendioCli.StripRunVerb(args);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        // Hosting integration is additive and safe when the environment is not that host, so both
        // are always registered rather than branching on a detected mode.
        builder.Host.UseWindowsService(service => service.ServiceName = CompendioConstants.ServiceName);
        builder.Host.UseSystemd();

        var options = new CompendioOptions();
        builder.Configuration.Bind(options);

        var dataDirectory = DataDirectory.Resolve(options);
        dataDirectory.EnsureCreated();

        ConfigureKestrel(builder, dataDirectory, options);
        builder.AddCompendio(dataDirectory, options);

        var app = builder.Build();

        RunStartupGuards(app, dataDirectory);
        ConfigurePipeline(app, dataDirectory, options);

        // Schema first, synchronously, before anything can run. The background services start with
        // the host, and an indexer that wakes up before the migration has created its queue table
        // spends its first seconds logging "no such table" — which it did, until this moved here.
        PrepareDatabaseAsync(app).GetAwaiter().GetResult();

        // Reconciliation is the slow part and it is safe to be late: the folder is the source of
        // truth, so a pass that finishes a few seconds after the first request still converges.
        app.Lifetime.ApplicationStarted.Register(() => _ = ReconcileOnStartAsync(app));
        app.Lifetime.ApplicationStopping.Register(() => app.Services.GetRequiredService<StartupGuards>().Release());

        return app;
    }

    /// <summary>
    /// Refuses to start on anything that produces late, silent damage, and warns on the rest.
    /// </summary>
    private static void RunStartupGuards(WebApplication app, DataDirectory dataDirectory)
    {
        var guards = app.Services.GetRequiredService<StartupGuards>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Compendio.Startup");

        var findings = guards.Run(ConfiguredPort(app));

        foreach (var finding in findings.Where(f => f.Severity == GuardSeverity.Warning))
        {
            logger.LogWarning("{Code}: {Message}", finding.Code, finding.Message);
        }

        var fatal = findings.Where(f => f.Severity == GuardSeverity.Fatal).ToList();
        if (fatal.Count == 0)
        {
            return;
        }

        foreach (var finding in fatal)
        {
            logger.LogCritical("{Code}: {Message}", finding.Code, finding.Message);
            Console.Error.WriteLine($"Compendio cannot start: {finding.Message}");
        }

        throw new InvalidOperationException(
            $"Startup checks failed: {string.Join("; ", fatal.Select(f => f.Code))}. See the messages above.");
    }

    /// <summary>
    /// The port the guard should test, taken from the same configuration Kestrel will use.
    /// </summary>
    /// <remarks>
    /// Without this the port check never ran and a busy port surfaced as a Kestrel bind exception —
    /// technically informative, but not the sentence the spec asks for, which names the port and
    /// tells the reader what to change.
    /// </remarks>
    private static int? ConfiguredPort(WebApplication app)
    {
        var urls = app.Configuration["Urls"] ?? app.Configuration["ASPNETCORE_URLS"];

        foreach (var candidate in (urls ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Uri.TryCreate(candidate.Replace("*", "0.0.0.0").Replace("+", "0.0.0.0"), UriKind.Absolute, out var uri))
            {
                return uri.Port;
            }
        }

        return null;
    }

    private static void ConfigureKestrel(WebApplicationBuilder builder, DataDirectory dataDirectory, CompendioOptions options)
    {
        builder.Services.Configure<FormOptions>(form =>
        {
            form.MultipartBodyLengthLimit = options.Attachments.MaxSizeBytes + (1024 * 1024);
        });

        if (!options.Tls.Enabled)
        {
            return;
        }

        var certificate = LoadCertificate(dataDirectory, options.Tls);

        if (certificate is null)
        {
            // Listening on the TLS port in plain HTTP would be worse than not starting: the operator
            // asked for TLS, the address would still answer, and every browser and every script
            // would go on working — in the clear, with nothing to notice.
            throw new InvalidOperationException(
                $"Tls:Enabled is true but there is no certificate to serve. Run '{CompendioConstants.CommandName} cert create' " +
                $"to have the instance issue one, or set Tls:CertificatePath to a PFX or PEM you already have. " +
                "Set Tls:Enabled=false to serve plain HTTP, or put a reverse proxy in front.");
        }

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.ListenAnyIP(options.Tls.Port, listen => listen.UseHttps(certificate));
        });
    }

    /// <summary>
    /// A supplied PFX or PEM if there is one, otherwise the certificate the instance issued itself.
    /// </summary>
    /// <remarks>
    /// An SMB with no PKI and no public hostname has no certificate to supply, so "TLS without a
    /// proxy" would otherwise be a feature only for organizations that already had one.
    /// <c>compendio cert create</c> closes that gap with no CA, no internet and no purchase.
    /// </remarks>
    private static X509Certificate2? LoadCertificate(DataDirectory dataDirectory, TlsOptions tls)
    {
        if (!string.IsNullOrWhiteSpace(tls.CertificatePath) && File.Exists(tls.CertificatePath))
        {
            return string.IsNullOrWhiteSpace(tls.CertificateKeyPath)
                ? X509CertificateLoader.LoadPkcs12FromFile(tls.CertificatePath, tls.CertificatePassword)
                : X509Certificate2.CreateFromPemFile(tls.CertificatePath, tls.CertificateKeyPath);
        }

        return SelfSignedCertificates.TryLoad(dataDirectory);
    }

    private static void ConfigurePipeline(WebApplication app, DataDirectory dataDirectory, CompendioOptions options)
    {
        app.UseExceptionHandler();

        app.UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
        });

        if (!string.IsNullOrWhiteSpace(options.App.BasePath))
        {
            app.UsePathBase(options.App.BasePath);
        }

        if (options.Security.RequireHttps)
        {
            app.UseHsts();
            app.UseHttpsRedirection();
        }

        app.UseResponseCompression();
        app.UseMiddleware<SecurityHeadersMiddleware>();

        AssertNoStaticProviderOverContent(app, dataDirectory);

        // The shell is served by SpaShell, never by the static-file middleware — it has to carry
        // this response's CSP nonce, and a static file cannot. UseDefaultFiles is deliberately
        // absent for the same reason: it would rewrite "/" to "/index.html" and hand it to the
        // static handler, which would serve the shell with the placeholder still in it.
        app.Use(async (context, next) =>
        {
            if (HttpMethods.IsGet(context.Request.Method) &&
                (context.Request.Path == "/" ||
                 context.Request.Path.Equals("/index.html", StringComparison.OrdinalIgnoreCase)))
            {
                await context.RequestServices.GetRequiredService<SpaShell>().WriteAsync(context);
                return;
            }

            await next(context);
        });

        app.UseStaticFiles();

        app.UseAuthentication();
        app.UseAuthorization();

        // Both of these have to come *after* authentication, and both were wrong before it.
        //
        // Language resolution reads the signed-in user's preference from a claim, which is step 1 of
        // the resolution chain and the answer to "the browser is in English but I want Spanish".
        // Ahead of UseAuthentication the principal is anonymous, so that step silently never fired.
        //
        // The rate limiter partitions writes and searches per user. Ahead of authentication there is
        // no user, so every caller shared one address-based partition and one busy editor could rate
        // -limit everybody else.
        app.UseMiddleware<RequestLanguageMiddleware>();
        app.UseRateLimiter();

        app.MapProbes();
        app.MapMeta();
        app.MapSetup();
        app.MapAuth();
        app.MapTree();
        app.MapPages();
        app.MapFolders();
        app.MapAttachments();
        app.MapSearch();
        app.MapHistory();
        app.MapLifecycle();
        app.MapNotifications();
        app.MapAcknowledgments();
        app.MapAi();
        app.MapHelp();
        app.MapAdmin();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        // An unmatched API route is a 404, not the SPA shell. Without this the fallback answers
        // /api/v1/typo with 200 and a page of HTML, which a client cannot tell from success.
        app.Map("/api/{**rest}", () => Results.NotFound());

        // The SPA owns client-side routing; anything else unmatched is its problem, not a 404 here.
        // Served through SpaShell rather than as a static file, because the shell has to carry this
        // response's CSP nonce — see SecurityHeadersMiddleware.
        app.MapFallback((HttpContext http, SpaShell shell) => shell.WriteAsync(http))
            .ExcludeFromDescription();
    }

    /// <summary>
    /// Asserts that no static file provider is mapped over the content folder.
    /// </summary>
    /// <remarks>
    /// Serving <c>content/</c> statically would bypass the entire permission layer in one line of
    /// somebody's future PR, and it would do so silently. This turns that into a startup failure.
    /// </remarks>
    private static void AssertNoStaticProviderOverContent(WebApplication app, DataDirectory dataDirectory)
    {
        var provider = app.Environment.WebRootFileProvider;
        var webRoot = app.Environment.WebRootPath;

        if (string.IsNullOrEmpty(webRoot))
        {
            return;
        }

        var content = Path.GetFullPath(dataDirectory.Content);
        var root = Path.GetFullPath(webRoot);

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (root.Equals(content, comparison) ||
            content.StartsWith(root + Path.DirectorySeparatorChar, comparison) ||
            root.StartsWith(content + Path.DirectorySeparatorChar, comparison))
        {
            throw new InvalidOperationException(
                $"The static file root ('{root}') overlaps the content folder ('{content}'). Serving content " +
                "statically would bypass every permission check. Move the content folder, or the web root.");
        }

        if (provider is PhysicalFileProvider physical &&
            Path.GetFullPath(physical.Root).TrimEnd(Path.DirectorySeparatorChar).Equals(content, comparison))
        {
            throw new InvalidOperationException("A static file provider is mapped over the content folder.");
        }
    }

    /// <summary>
    /// Migrations, the search schema and the optional bootstrap admin.
    /// </summary>
    /// <remarks>
    /// All of it idempotent and safe to repeat on every start — a fresh install with no
    /// configuration at all must come up, and a restart must not be a risk. Failures here are fatal
    /// on purpose: serving requests against a half-migrated database is worse than not starting.
    /// </remarks>
    private static async Task PrepareDatabaseAsync(WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Compendio.Startup");

        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var options = services.GetRequiredService<IOptions<CompendioOptions>>().Value;
        var dataDirectory = services.GetRequiredService<DataDirectory>();

        // From the factory, not from the scope. A scoped DbContext belongs to the container, and
        // disposing it here left the scope's IAccountService holding a disposed context — which
        // crashed startup, but only when Bootstrap:AdminUser was set, which is to say only in
        // Development.
        await using (var db = await services.GetRequiredService<IDbContextFactory<CompendioDbContext>>()
                         .CreateDbContextAsync())
        {
            await RefuseIfSchemaIsNewerAsync(db, logger);

            if (options.Database.AutoMigrate)
            {
                // VACUUM INTO before migrating, so a failed upgrade is recoverable rather than a
                // restore-from-backup conversation.
                if (options.Database.BackupBeforeMigrate)
                {
                    await SnapshotDatabaseAsync(db, dataDirectory, logger);
                }

                await db.Database.MigrateAsync();
            }

            await ApplyPragmasAsync(db);

            var connection = (SqliteConnection)db.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open)
            {
                await connection.OpenAsync();
            }

            // Outside migrations by design: the index is a cache, and its correct response to damage
            // is "rebuild from the files", not "migrate".
            await SearchSchema.EnsureAsync(connection);
        }

        await BootstrapAdminAsync(services, options, logger);

        logger.LogInformation(
            "{Product} {Version} is ready. Content folder: {Content}",
            CompendioConstants.ProductName,
            Application.Admin.GetStatusHandler.BuildVersion,
            dataDirectory.Content);
    }

    private static async Task ReconcileOnStartAsync(WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Compendio.Startup");

        try
        {
            using var scope = app.Services.CreateScope();
            var report = await scope.ServiceProvider.GetRequiredService<Reconciler>().RunAsync();

            if (report.ParseFailures.Count > 0)
            {
                logger.LogWarning(
                    "{Count} file(s) could not be read during startup reconciliation. Run 'compendio doctor' for the list.",
                    report.ParseFailures.Count);
            }
        }
        catch (Exception e)
        {
            // Not fatal: the watcher and the next scheduled pass will converge, and refusing to
            // serve because one file is locked would be the wrong trade.
            logger.LogError(e, "Startup reconciliation failed. Content changes will still be picked up by the watcher.");
        }
    }

    /// <summary>
    /// The two pragmas that persist in the database file itself.
    /// </summary>
    /// <remarks>
    /// WAL, so a reader never blocks the watcher's writer, and <c>NORMAL</c> sync, which is the
    /// correct pairing with it. Both are stored in the file and apply to every future connection.
    /// <para>
    /// <c>foreign_keys</c> and <c>busy_timeout</c> are deliberately absent: they are <em>per
    /// connection</em>, so setting them here would configure exactly one connection out of a pool
    /// and quietly leave the rest without them. They come from the connection string instead.
    /// </para>
    /// </remarks>
    private static async Task ApplyPragmasAsync(CompendioDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;");
    }

    /// <summary>
    /// Refuses to start against a database written by a newer build.
    /// </summary>
    /// <remarks>
    /// An accidental downgrade — an operator copying last month's binary over a running install —
    /// must not write. The older build does not know the newer schema, so it would drop columns it
    /// cannot see and corrupt data nobody notices until the newer build is restored.
    /// </remarks>
    private static async Task RefuseIfSchemaIsNewerAsync(CompendioDbContext db, ILogger logger)
    {
        if (!await db.Database.CanConnectAsync())
        {
            return;
        }

        IEnumerable<string> applied;
        try
        {
            applied = await db.Database.GetAppliedMigrationsAsync();
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // No migrations history table yet: a fresh database, which is fine.
            return;
        }

        var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        var unknown = applied.Where(m => !known.Contains(m)).ToList();

        if (unknown.Count == 0)
        {
            return;
        }

        var message =
            $"This database was created by a newer version of {CompendioConstants.ProductName} " +
            $"(it has {unknown.Count} migration(s) this build does not know: {string.Join(", ", unknown.Take(3))}). " +
            "Running an older build against it would damage data. Restore the newer binary, or restore a backup " +
            "taken before the upgrade.";

        logger.LogCritical("guard.schema_newer: {Message}", message);
        Console.Error.WriteLine($"Compendio cannot start: {message}");

        throw new InvalidOperationException(message);
    }

    private static async Task SnapshotDatabaseAsync(CompendioDbContext db, DataDirectory dataDirectory, ILogger logger)
    {
        if (!File.Exists(dataDirectory.DatabaseFile))
        {
            return;
        }

        var pending = await db.Database.GetPendingMigrationsAsync();
        if (!pending.Any())
        {
            return;
        }

        var target = Path.Combine(dataDirectory.Database, $"compendio.pre-migration.db");

        try
        {
            if (File.Exists(target))
            {
                File.Delete(target);
            }

            // VACUUM INTO takes a literal, not a parameter, so the path is escaped by hand. It is
            // built from the data directory, never from user input.
            var sql = "VACUUM INTO '" + target.Replace("'", "''", StringComparison.Ordinal) + "';";
            await db.Database.ExecuteSqlRawAsync(sql);
            logger.LogInformation("Copied the database to {Target} before applying migrations.", target);
        }
        catch (SqliteException e)
        {
            logger.LogWarning(e, "Could not snapshot the database before migrating; continuing.");
        }
    }

    /// <summary>
    /// Creates the configured bootstrap admin if there is no user at all.
    /// </summary>
    /// <remarks>
    /// For unattended installs. The setup wizard is the normal path, and this deliberately does
    /// nothing once any account exists.
    /// </remarks>
    private static async Task BootstrapAdminAsync(IServiceProvider services, CompendioOptions options, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(options.Bootstrap.AdminUser) || string.IsNullOrWhiteSpace(options.Bootstrap.AdminPassword))
        {
            return;
        }

        var accounts = services.GetRequiredService<IAccountService>();
        if (await accounts.AnyUserExistsAsync())
        {
            return;
        }

        await accounts.CreateAsync(new CreateUserRequest(
            options.Bootstrap.AdminUser,
            options.Bootstrap.AdminPassword,
            options.Bootstrap.AdminUser,
            options.Bootstrap.AdminEmail,
            UserRole.Admin,
            options.Instance.DefaultLanguage));

        logger.LogWarning(
            "Created the bootstrap administrator '{User}' from configuration. Change its password.",
            options.Bootstrap.AdminUser);
    }
}
