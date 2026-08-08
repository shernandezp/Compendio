using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Compendio.Api.Common;
using Compendio.Application.Abstractions;
using Compendio.Domain;
using Compendio.Domain.Localization;
using Compendio.Domain.Security;
using Compendio.Hosting.Configuration;
using Compendio.Infrastructure.Crypto;
using Compendio.Infrastructure.Identity;
using Compendio.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Compendio.Hosting;

/// <summary>
/// The command line.
/// </summary>
/// <remarks>
/// <para>
/// Every verb is scriptable: there is no interactive prompt that a flag or an environment variable
/// cannot satisfy, because half the point of these commands is to run them from a scheduled task.
/// </para>
/// <para>
/// User-facing output is localized from the OS locale with <c>--lang</c> to override. Logs stay in
/// English — ops greppability and a pasteable GitHub issue beat a localized log line.
/// </para>
/// </remarks>
public static class CompendioCli
{
    private static readonly string[] Verbs =
    [
        "install", "uninstall", "doctor", "backup", "restore", "reindex",
        "rekey", "secure", "cert", "reset-admin-password", "help", "--help", "-h", "--version",
    ];

    public static int ExitCode { get; private set; }

    public static bool IsCliVerb(string[] args) =>
        args.Length > 0 && Verbs.Contains(args[0], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Removes a leading <c>run</c>, which means "start the server" and is otherwise not an option
    /// the configuration binder knows how to read.
    /// </summary>
    public static string[] StripRunVerb(string[] args) =>
        args.Length > 0 && string.Equals(args[0], "run", StringComparison.OrdinalIgnoreCase)
            ? args[1..]
            : args;

    public static void Run(string[] args)
    {
        var language = ResolveLanguage(args);

        try
        {
            ExitCode = args[0].ToLowerInvariant() switch
            {
                "help" or "--help" or "-h" => PrintHelp(),
                "--version" => PrintVersion(),
                "install" => ServiceInstaller.Install(),
                "uninstall" => ServiceInstaller.Uninstall(),
                "doctor" => RunAsync(args, (sp, a) => DoctorCommand.RunAsync(sp, a, language)).GetAwaiter().GetResult(),
                "backup" => RunAsync(args, BackupCommand.RunAsync).GetAwaiter().GetResult(),
                // Restore replaces the database file, so it must not run against a schema this
                // process has just created and is still holding open.
                "restore" => RunAsync(args, BackupCommand.RestoreAsync, ensureSchema: false).GetAwaiter().GetResult(),
                "reindex" => RunAsync(args, ReindexAsync).GetAwaiter().GetResult(),
                "rekey" => RunAsync(args, RekeyAsync).GetAwaiter().GetResult(),
                "secure" => RunAsync(args, SecureAsync).GetAwaiter().GetResult(),
                "cert" => RunAsync(args, CertAsync).GetAwaiter().GetResult(),
                "reset-admin-password" => RunAsync(args, ResetAdminPasswordAsync).GetAwaiter().GetResult(),
                _ => PrintHelp(),
            };
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"{CompendioConstants.CommandName}: {e.Message}");
            ExitCode = 1;
        }
    }

    // ---- Verbs ------------------------------------------------------------------------------------

    private static async Task<int> ReindexAsync(IServiceProvider services, string[] args)
    {
        var dropSecure = HasFlag(args, "--drop-secure");
        var index = services.GetRequiredService<ISearchIndex>();

        var progress = new Progress<int>(percent => Console.Write($"\rReindexing… {percent}%"));
        await index.RebuildAsync(progress, dropSecure);

        Console.WriteLine("\rReindexing… done.   ");
        return 0;
    }

    /// <summary>
    /// Rotates a scope key or the master key.
    /// </summary>
    /// <remarks>
    /// A scope rotation rewrites every file in the scope; the old key stays in the table marked
    /// retired, so a kill part-way through leaves every file readable under one key or the other.
    /// A master rotation only rewraps the data keys, and is therefore cheap.
    /// </remarks>
    private static async Task<int> RekeyAsync(IServiceProvider services, string[] args)
    {
        var crypto = services.GetRequiredService<IContentCrypto>();

        if (HasFlag(args, "--master"))
        {
            await crypto.RotateMasterKeyAsync();
            Console.WriteLine("The instance master key was replaced and every scope key rewrapped.");
            return 0;
        }

        var scopePath = Value(args, "--scope");
        if (scopePath is null)
        {
            Console.Error.WriteLine("Specify --scope <folder> or --master.");
            return 1;
        }

        var paths = services.GetRequiredService<IPathPolicy>();
        var store = services.GetRequiredService<IContentStore>();
        var scope = paths.Require(scopePath, Domain.Content.PathKind.Folder);

        await crypto.RotateScopeKeyAsync(scope);

        var rewritten = 0;
        await foreach (var entry in store.EnumerateAsync(scope))
        {
            if (entry.IsFolder)
            {
                continue;
            }

            var file = await store.ReadAsync(entry.Path);
            if (file is null)
            {
                continue;
            }

            await store.WriteAsync(entry.Path, file.Bytes, file.ContentHash);
            rewritten++;
        }

        Console.WriteLine($"Rotated the key for '{scope.Value}' and rewrote {rewritten} file(s).");
        return 0;
    }

    /// <summary>
    /// The admin round-trip for a direct edit of an encrypted page.
    /// </summary>
    /// <remarks>
    /// Files-first is suspended inside a secure scope — <c>runbook.md.enc</c> does not open in
    /// VS Code, and the docs say exactly that. These two commands are the honest replacement.
    /// </remarks>
    private static async Task<int> SecureAsync(IServiceProvider services, string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: compendio secure export --path <page> --out <file>");
            Console.Error.WriteLine("       compendio secure import --path <page> --in <file>");
            return 1;
        }

        var paths = services.GetRequiredService<IPathPolicy>();
        var store = services.GetRequiredService<IContentStore>();

        var pagePath = Value(args, "--path") ?? throw new InvalidOperationException("--path is required.");
        var page = paths.Require(pagePath, Domain.Content.PathKind.Page);

        switch (args[1].ToLowerInvariant())
        {
            case "export":
            {
                var output = Value(args, "--out") ?? throw new InvalidOperationException("--out is required.");
                var file = await store.ReadAsync(page) ?? throw new InvalidOperationException($"'{page.Value}' does not exist.");

                await File.WriteAllBytesAsync(output, file.Bytes);
                Console.WriteLine($"Wrote plaintext to '{output}'. Delete it when you are done — it is not protected.");
                return 0;
            }

            case "import":
            {
                var input = Value(args, "--in") ?? throw new InvalidOperationException("--in is required.");
                var bytes = await File.ReadAllBytesAsync(input);
                var existing = await store.ReadAsync(page);

                await store.WriteAsync(page, bytes, existing?.ContentHash);
                Console.WriteLine($"Imported '{input}' into '{page.Value}'.");
                return 0;
            }

            default:
                Console.Error.WriteLine($"Unknown subcommand '{args[1]}'.");
                return 1;
        }
    }

    private static Task<int> CertAsync(IServiceProvider services, string[] args)
    {
        var dataDirectory = services.GetRequiredService<DataDirectory>();

        if (args.Length < 2 || !string.Equals(args[1], "create", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Usage: compendio cert create [--renew] [--dns <name>]…");
            return Task.FromResult(1);
        }

        var path = SelfSignedCertificates.PathFor(dataDirectory);

        if (File.Exists(path) && !HasFlag(args, "--renew"))
        {
            var days = SelfSignedCertificates.DaysUntilExpiry(dataDirectory);
            Console.WriteLine($"A certificate already exists at '{path}' and expires in {days} day(s).");
            Console.WriteLine("Use --renew to replace it.");
            return Task.FromResult(0);
        }

        var extraNames = Values(args, "--dns");
        using var certificate = SelfSignedCertificates.Create(dataDirectory, extraNames);

        Console.WriteLine($"Issued a self-signed certificate at '{path}'.");
        Console.WriteLine($"  Subject:    {certificate.Subject}");
        Console.WriteLine($"  Valid until {certificate.NotAfter:yyyy-MM-dd}");
        Console.WriteLine($"  Thumbprint  {certificate.Thumbprint}");
        Console.WriteLine();
        Console.WriteLine("Set Tls:Enabled=true and restart to serve HTTPS with it.");
        Console.WriteLine("Browsers will warn until the certificate is trusted. To trust it:");
        Console.WriteLine($"  Windows: Import-Certificate -FilePath '{path}' -CertStoreLocation Cert:\\LocalMachine\\Root");
        Console.WriteLine("  Linux:   copy the exported .crt into /usr/local/share/ca-certificates and run update-ca-certificates");

        return Task.FromResult(0);
    }

    /// <summary>
    /// Local-console recovery. There is no email, so this is the only way back in.
    /// </summary>
    private static async Task<int> ResetAdminPasswordAsync(IServiceProvider services, string[] args)
    {
        var userName = Value(args, "--user");
        var password = Value(args, "--password") ?? Environment.GetEnvironmentVariable("COMPENDIO_NEW_PASSWORD");

        if (string.IsNullOrWhiteSpace(password))
        {
            Console.Error.WriteLine("Provide --password <value> or set COMPENDIO_NEW_PASSWORD.");
            return 1;
        }

        await using var db = services.GetRequiredService<IDbContextFactory<CompendioDbContext>>().CreateDbContext();

        var admin = userName is null
            ? await db.Users.Where(u => u.Role == UserRole.Admin && u.Active).OrderBy(u => u.CreatedAt).FirstOrDefaultAsync()
            : await db.Users.FirstOrDefaultAsync(u => u.UserName == userName);

        if (admin is null)
        {
            Console.Error.WriteLine("No matching administrator account was found.");
            return 1;
        }

        var users = services.GetRequiredService<UserManager<CompendioUser>>();
        var token = await users.GeneratePasswordResetTokenAsync(admin);
        var result = await users.ResetPasswordAsync(admin, token, password);

        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                Console.Error.WriteLine($"  {error.Code}: {error.Description}");
            }

            return 1;
        }

        // Reactivate too: an admin who locked themselves out by deactivating the account needs the
        // one recovery path to actually recover them.
        admin.Active = true;
        await db.SaveChangesAsync();

        Console.WriteLine($"Reset the password for '{admin.UserName}'.");
        return 0;
    }

    // ---- Plumbing ---------------------------------------------------------------------------------

    private static async Task<int> RunAsync(
        string[] args,
        Func<IServiceProvider, string[], Task<int>> action,
        bool ensureSchema = true)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .AddCommandLine(args)
            .Build();

        var options = new CompendioOptions();
        configuration.Bind(options);

        var dataDirectory = DataDirectory.Resolve(options);
        dataDirectory.EnsureCreated();

        var services = new ServiceCollection();
        services.AddCompendioForCli(configuration, dataDirectory, options);

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        if (ensureSchema)
        {
            await EnsureSchemaAsync(scope.ServiceProvider);
        }

        return await action(scope.ServiceProvider, args);
    }

    /// <summary>
    /// Applies migrations and the FTS schema before a verb runs.
    /// </summary>
    /// <remarks>
    /// Idempotent, and necessary: <c>doctor</c> is exactly the command somebody runs against an
    /// instance that has never started, and it has to report on that instance rather than fall over
    /// on a missing table. A failure here is swallowed on purpose — <c>doctor</c>'s own database
    /// check is what should describe it, in a sentence the operator can act on.
    /// </remarks>
    private static async Task EnsureSchemaAsync(IServiceProvider services)
    {
        try
        {
            await using var db = services
                .GetRequiredService<IDbContextFactory<CompendioDbContext>>()
                .CreateDbContext();

            await db.Database.MigrateAsync();

            var connection = (Microsoft.Data.Sqlite.SqliteConnection)db.Database.GetDbConnection();
            if (connection.State != System.Data.ConnectionState.Open)
            {
                await connection.OpenAsync();
            }

            await Infrastructure.Search.SearchSchema.EnsureAsync(connection);
        }
        catch (Exception)
        {
            // Deliberately silent. Every verb that needs the schema will fail with its own message,
            // and `doctor` is designed to describe a database it cannot open.
        }
    }

    private static string ResolveLanguage(string[] args) =>
        SupportedLanguages.ResolveOrFallback(
            Value(args, "--lang") ?? CultureInfo.CurrentUICulture.Name,
            SupportedLanguages.Spanish);

    internal static bool HasFlag(string[] args, string flag) =>
        args.Contains(flag, StringComparer.OrdinalIgnoreCase);

    internal static string? Value(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    internal static IReadOnlyList<string> Values(string[] args, string name)
    {
        var values = new List<string>();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                values.Add(args[i + 1]);
            }
        }

        return values;
    }

    private static int PrintVersion()
    {
        Console.WriteLine($"{CompendioConstants.ProductName} {Application.Admin.GetStatusHandler.BuildVersion}");
        return 0;
    }

    private static int PrintHelp()
    {
        Console.WriteLine($"""
            {CompendioConstants.ProductName} — a Markdown folder that is the database of record.

            Usage: {CompendioConstants.CommandName} [verb] [options]

            With no verb, the server starts.

              run                           Start the server (the same as no verb at all)
              install                       Register as a Windows Service or a systemd unit
              uninstall                     Remove the service registration. Data is left untouched
              doctor [--json]               Check this instance and report what is wrong
              backup --out <file>           Content plus a consistent database copy
                     [--secure-passphrase]  Required when a secure scope exists
              restore --in <file>           Restore an archive into the data directory
                      [--secure-passphrase]
              reindex [--drop-secure]       Rebuild the search index from the content folder
              rekey --scope <folder>        New data key for a scope; rewrites its files
              rekey --master                New master key; rewraps the scope keys only
              secure export --path <page> --out <file>
              secure import --path <page> --in <file>
              cert create [--renew] [--dns <name>]…
                                            Issue a self-signed TLS certificate
              reset-admin-password --password <value> [--user <name>]

            Global options:
              --lang <es|en>                Language for this command's output
              --DataDir <path>              Override the data directory

            Every verb is scriptable: no prompt that a flag or environment variable cannot answer.
            """);

        return 0;
    }
}
