using System.Globalization;
using System.Text.Json;
using Compendio.Application.Abstractions;
using Compendio.Domain;
using Compendio.Domain.Content;
using Compendio.Domain.Entities;
using Compendio.Domain.Security;
using Compendio.Hosting.Configuration;
using Compendio.Infrastructure.Crypto;
using Compendio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Compendio.Hosting;

public enum FindingLevel
{
    Ok = 0,
    Warning = 1,
    Error = 2,
}

public sealed record DoctorFinding(FindingLevel Level, string Check, string Message);

/// <summary>
/// <c>compendio doctor</c>: what is wrong with this instance, in plain language.
/// </summary>
/// <remarks>
/// <para>
/// Written for the person who has to fix it, not for us. Every finding names the path, the account
/// or the number involved, because "database error" is not something an IT admin at an SMB can act
/// on at nine on a Monday.
/// </para>
/// <para>
/// It never prints page content, a secret, or anything decrypted — including when reporting a file
/// that failed authentication. Exit codes: 0 clean, 1 the command itself failed, 2 findings.
/// </para>
/// </remarks>
public static class DoctorCommand
{
    public static async Task<int> RunAsync(IServiceProvider services, string[] args, string language)
    {
        var findings = await CollectAsync(services);

        if (CompendioCli.HasFlag(args, "--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(
                findings.Select(f => new { level = f.Level.ToString().ToLowerInvariant(), check = f.Check, message = f.Message }),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            Print(findings, language);
        }

        return findings.Any(f => f.Level != FindingLevel.Ok) ? 2 : 0;
    }

    public static async Task<IReadOnlyList<DoctorFinding>> CollectAsync(IServiceProvider services)
    {
        var findings = new List<DoctorFinding>();

        var dataDirectory = services.GetRequiredService<DataDirectory>();
        var options = services.GetRequiredService<IOptions<CompendioOptions>>().Value;
        var guards = services.GetRequiredService<StartupGuards>();

        CheckDataDirectory(findings, dataDirectory);
        CheckDiskSpace(findings, dataDirectory);
        CheckWatcher(findings, guards, options, dataDirectory);
        CheckTls(findings, dataDirectory);

        await CheckDatabaseAsync(findings, services, dataDirectory);
        await CheckContentAsync(findings, services);
        await CheckIndexAsync(findings, services);
        await CheckKeysAsync(findings, services, dataDirectory);
        await CheckAclsAsync(findings, services, options);
        await CheckBackupAsync(findings, services);
        await CheckAiAsync(findings, services);
        await CheckGitMirrorAsync(findings, services, options);

        findings.Add(new DoctorFinding(FindingLevel.Ok, "version",
            $"{CompendioConstants.ProductName} {Application.Admin.GetStatusHandler.BuildVersion}, " +
            $"installed as {Application.Admin.InstallMode.Detect()}."));

        return findings;
    }

    /// <summary>
    /// Whether an AI provider is configured, and whether it answers.
    /// </summary>
    /// <remarks>
    /// "Not configured" is an <c>Ok</c> finding, not a warning. The product is complete without AI,
    /// and a diagnostic that nagged about an optional feature being off would train people to ignore
    /// its output.
    /// </remarks>
    private static async Task CheckAiAsync(List<DoctorFinding> findings, IServiceProvider services)
    {
        var settings = services.GetRequiredService<Application.Abstractions.IAiSettings>();
        var configuration = await settings.GetAsync();

        if (!configuration.Enabled)
        {
            findings.Add(new DoctorFinding(FindingLevel.Ok, "ai", "No AI provider is configured."));
            return;
        }

        var provider = services.GetRequiredService<Application.Abstractions.IAiProvider>();
        var probe = await provider.ProbeAsync();

        findings.Add(new DoctorFinding(
            probe.Ok ? FindingLevel.Ok : FindingLevel.Warning,
            "ai",
            probe.Ok
                ? $"The AI provider at {configuration.EndpointLabel} answered as {probe.Model}."
                : $"The AI provider at {configuration.EndpointLabel} did not answer: {probe.Detail}"));

        // The probe above is not charged against the budget — it is a diagnostic, and one that
        // refused to run because the wiki had been busy would be a diagnostic nobody could trust.
        await CheckAiBudgetAsync(findings, services, configuration);
    }

    /// <summary>
    /// What the daily AI budget is and how much of it is gone.
    /// </summary>
    /// <remarks>
    /// "The AI buttons stopped working" is a support ticket whose most likely cause is a spent
    /// allowance, and the second most likely is a provider that has stopped answering. Doctor already
    /// answered the second; without this it would report a perfectly healthy AI setup to an
    /// administrator whose users cannot use it, which is the failure mode this whole command exists
    /// to prevent.
    /// </remarks>
    private static async Task CheckAiBudgetAsync(
        List<DoctorFinding> findings,
        IServiceProvider services,
        Application.Abstractions.AiConfiguration configuration)
    {
        if (configuration.DailyPerInstance <= 0)
        {
            findings.Add(new DoctorFinding(FindingLevel.Ok, "ai",
                configuration.DailyPerUser > 0
                    ? $"AI usage is capped at {configuration.DailyPerUser} requests per person per rolling 24 hours, with no instance-wide cap."
                    : "AI usage is not capped. On a metered endpoint, consider a cap under Administration, Integrations."));

            return;
        }

        var usage = await services.GetRequiredService<Application.Ai.AiBudget>()
            .ForInstanceAsync(configuration);

        findings.Add(new DoctorFinding(
            usage.Remaining == 0 ? FindingLevel.Warning : FindingLevel.Ok,
            "ai",
            usage.Remaining == 0
                ? $"The instance AI budget is spent: {usage.Used} of {usage.Limit} requests in the last 24 hours. AI actions refuse until it frees up."
                : $"AI usage is {usage.Used} of {usage.Limit} instance requests in the last 24 hours, and {configuration.DailyPerUser} per person."));
    }

    /// <summary>
    /// Whether the mirror is on, whether <c>git</c> exists, and when it last pushed.
    /// </summary>
    /// <remarks>
    /// The remote URL is never printed. It can carry an access token, and <c>doctor</c> output is
    /// designed to be pasted into a GitHub issue.
    /// </remarks>
    private static async Task CheckGitMirrorAsync(
        List<DoctorFinding> findings,
        IServiceProvider services,
        CompendioOptions options)
    {
        if (!options.GitMirror.Enabled)
        {
            findings.Add(new DoctorFinding(FindingLevel.Ok, "git-mirror", "The git mirror is disabled."));
            return;
        }

        var mirror = services.GetRequiredService<Application.Abstractions.IGitMirror>();

        if (!await mirror.IsGitAvailableAsync())
        {
            findings.Add(new DoctorFinding(FindingLevel.Warning, "git-mirror",
                "The git mirror is enabled but `git` is not on PATH. Everything else is unaffected."));
            return;
        }

        var db = services.GetRequiredService<Application.Abstractions.ICompendioDbContext>();
        var values = await db.Settings
            .AsNoTracking()
            .Where(s => s.Key.StartsWith("gitmirror."))
            .ToDictionaryAsync(s => s.Key, s => s.Value);

        var lastSuccess = values.GetValueOrDefault(Domain.Entities.SettingKeys.GitMirrorLastSuccessAt);
        var lastError = values.GetValueOrDefault(Domain.Entities.SettingKeys.GitMirrorLastError);

        if (!string.IsNullOrWhiteSpace(lastError))
        {
            findings.Add(new DoctorFinding(FindingLevel.Warning, "git-mirror",
                $"The last push failed: {lastError}"));
            return;
        }

        findings.Add(new DoctorFinding(FindingLevel.Ok, "git-mirror",
            string.IsNullOrWhiteSpace(lastSuccess)
                ? "The git mirror is enabled and has not pushed yet."
                : $"The git mirror last pushed at {lastSuccess}."));
    }

    private static void CheckDataDirectory(List<DoctorFinding> findings, DataDirectory dataDirectory)
    {
        var probe = Path.Combine(dataDirectory.Root, $".doctor-{Environment.ProcessId}");
        var account = OperatingSystem.IsWindows()
            ? $"{Environment.UserDomainName}\\{Environment.UserName}"
            : Environment.UserName;

        try
        {
            File.WriteAllBytes(probe, [0]);
            File.Delete(probe);
            findings.Add(new DoctorFinding(FindingLevel.Ok, "data-directory",
                $"'{dataDirectory.Root}' is writable by '{account}'."));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            findings.Add(new DoctorFinding(FindingLevel.Error, "data-directory",
                $"'{dataDirectory.Root}' is NOT writable by '{account}'. Grant that account write access."));
        }
    }

    private static void CheckDiskSpace(List<DoctorFinding> findings, DataDirectory dataDirectory)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(dataDirectory.Root));
            if (string.IsNullOrEmpty(root))
            {
                return;
            }

            var drive = new DriveInfo(root);
            var freeGb = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);

            findings.Add(new DoctorFinding(
                freeGb < 1 ? FindingLevel.Error : freeGb < 5 ? FindingLevel.Warning : FindingLevel.Ok,
                "disk-space",
                $"{freeGb:F1} GB free on '{root}'."));
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Not knowing the free space is not itself a problem worth reporting as one.
        }
    }

    private static void CheckWatcher(
        List<DoctorFinding> findings,
        StartupGuards guards,
        CompendioOptions options,
        DataDirectory dataDirectory)
    {
        var polling = guards.ShouldUsePolling();
        var why = options.Content.WatcherMode == WatcherMode.Poll
            ? "Content:WatcherMode is set to Poll"
            : polling
                ? "the content folder is on a network file system, where change notifications are unreliable"
                : "the content folder is on local storage";

        findings.Add(new DoctorFinding(FindingLevel.Ok, "watcher",
            $"Watching '{dataDirectory.Content}' by {(polling ? $"polling every {options.Content.PollSeconds} s" : "native notifications")} — {why}."));
    }

    private static void CheckTls(List<DoctorFinding> findings, DataDirectory dataDirectory)
    {
        var days = SelfSignedCertificates.DaysUntilExpiry(dataDirectory);

        if (days is null)
        {
            findings.Add(new DoctorFinding(FindingLevel.Ok, "tls",
                "No self-issued TLS certificate. Run 'compendio cert create' if you want HTTPS without a proxy."));
            return;
        }

        findings.Add(new DoctorFinding(
            days < 0 ? FindingLevel.Error : days < 30 ? FindingLevel.Warning : FindingLevel.Ok,
            "tls",
            days < 0
                ? $"The TLS certificate expired {-days.Value} day(s) ago. Run 'compendio cert create --renew'."
                : $"The TLS certificate expires in {days} day(s)."));
    }

    private static async Task CheckDatabaseAsync(List<DoctorFinding> findings, IServiceProvider services, DataDirectory dataDirectory)
    {
        await using var db = services.GetRequiredService<IDbContextFactory<CompendioDbContext>>().CreateDbContext();

        try
        {
            var integrity = await db.Database.SqlQueryRaw<string>("PRAGMA integrity_check;").ToListAsync();
            var ok = integrity.Count == 1 && integrity[0] == "ok";

            var size = File.Exists(dataDirectory.DatabaseFile) ? new FileInfo(dataDirectory.DatabaseFile).Length : 0;

            findings.Add(new DoctorFinding(
                ok ? FindingLevel.Ok : FindingLevel.Error,
                "database",
                ok
                    ? $"Integrity check passed. The database is {Bytes(size)}."
                    : $"Integrity check FAILED: {string.Join("; ", integrity)}. Restore from a backup."));

            var pending = await db.Database.GetPendingMigrationsAsync();
            if (pending.Any())
            {
                findings.Add(new DoctorFinding(FindingLevel.Warning, "database-schema",
                    $"{pending.Count()} migration(s) have not been applied. Restart to apply them."));
            }
        }
        catch (Exception e)
        {
            findings.Add(new DoctorFinding(FindingLevel.Error, "database", $"The database could not be opened: {e.Message}"));
        }
    }

    private static async Task CheckContentAsync(List<DoctorFinding> findings, IServiceProvider services)
    {
        var store = services.GetRequiredService<IContentStore>();
        var failures = new List<string>();
        var pages = 0;

        await foreach (var entry in store.EnumerateAsync(ContentPath.Root))
        {
            if (entry.IsFolder || entry.Path.Extension != CompendioConstants.MarkdownExtension)
            {
                continue;
            }

            pages++;

            try
            {
                var file = await store.ReadAsync(entry.Path);
                if (file is null)
                {
                    failures.Add(entry.Path.Value);
                    continue;
                }

                // Parsing must not throw, and the front-matter parser is forgiving by design, so a
                // failure here means something structural.
                MarkdownParser.Parse(file.Text);
            }
            catch (Exception)
            {
                // The path only. Never any part of the file's contents.
                failures.Add(entry.Path.Value);
            }
        }

        findings.Add(new DoctorFinding(
            failures.Count == 0 ? FindingLevel.Ok : FindingLevel.Warning,
            "content",
            failures.Count == 0
                ? $"{pages} page(s) read cleanly."
                : $"{failures.Count} of {pages} page(s) could not be read: {string.Join(", ", failures.Take(10))}" +
                  (failures.Count > 10 ? $" and {failures.Count - 10} more." : ".")));
    }

    private static async Task CheckIndexAsync(List<DoctorFinding> findings, IServiceProvider services)
    {
        var index = services.GetRequiredService<ISearchIndex>();
        var status = await index.StatusAsync();

        findings.Add(new DoctorFinding(
            status.QueueDepth > 100 ? FindingLevel.Warning : FindingLevel.Ok,
            "search-index",
            $"Index is '{status.State}' over {status.PageCount} page(s); {status.QueueDepth} item(s) queued."));
    }

    private static async Task CheckKeysAsync(List<DoctorFinding> findings, IServiceProvider services, DataDirectory dataDirectory)
    {
        var crypto = services.GetRequiredService<IContentCrypto>();
        var masterKeys = services.GetRequiredService<MasterKeyStore>();

        await using var db = services.GetRequiredService<IDbContextFactory<CompendioDbContext>>().CreateDbContext();
        var scopeCount = await db.SecureScopes.CountAsync(s => s.RetiredAt == null);

        if (scopeCount == 0)
        {
            findings.Add(new DoctorFinding(FindingLevel.Ok, "encryption", "No secure scopes on this instance."));
            return;
        }

        if (!masterKeys.Exists)
        {
            findings.Add(new DoctorFinding(FindingLevel.Error, "encryption",
                $"{scopeCount} secure scope(s) exist but '{dataDirectory.MasterKeyFile}' is missing. " +
                "Restore the keys directory from a backup; encrypted pages cannot be read without it."));
            return;
        }

        var health = await crypto.ProbeAsync();

        foreach (var (scope, availability) in health)
        {
            findings.Add(new DoctorFinding(
                availability == SecureAvailability.Available ? FindingLevel.Ok : FindingLevel.Error,
                "encryption",
                availability == SecureAvailability.Available
                    ? $"Scope '{scope}': key readable."
                    : $"Scope '{scope}': {availability}. Encrypted pages there cannot be read."));
        }

        await CheckEnvelopesAsync(findings, services);
    }

    /// <summary>
    /// Reads every envelope's header and counts the ones that will not authenticate.
    /// </summary>
    /// <remarks>
    /// Header parsing needs no key, so this works even when the master key is gone. It reports
    /// counts and paths and never a byte of plaintext.
    /// </remarks>
    private static async Task CheckEnvelopesAsync(List<DoctorFinding> findings, IServiceProvider services)
    {
        var store = services.GetRequiredService<IContentStore>();
        var registry = services.GetRequiredService<ISecureScopeRegistry>();
        var scopes = await registry.ScopesAsync();

        var unreadable = new List<string>();
        var total = 0;

        foreach (var scope in scopes)
        {
            await foreach (var entry in store.EnumerateAsync(scope))
            {
                if (entry.IsFolder || !entry.IsEncryptedOnDisk)
                {
                    continue;
                }

                total++;

                try
                {
                    _ = await store.ReadAsync(entry.Path);
                }
                catch (Exception)
                {
                    unreadable.Add(entry.Path.Value);
                }
            }
        }

        findings.Add(new DoctorFinding(
            unreadable.Count == 0 ? FindingLevel.Ok : FindingLevel.Error,
            "encrypted-files",
            unreadable.Count == 0
                ? $"{total} encrypted file(s) authenticate correctly."
                : $"{unreadable.Count} of {total} encrypted file(s) failed authentication: {string.Join(", ", unreadable.Take(10))}"));
    }

    private static async Task CheckAclsAsync(List<DoctorFinding> findings, IServiceProvider services, CompendioOptions options)
    {
        await using var db = services.GetRequiredService<IDbContextFactory<CompendioDbContext>>().CreateDbContext();

        var folderPaths = await db.Folders.Select(f => f.Path).ToHashSetAsync();
        var nodes = await db.AclNodes.Include(n => n.Entries).ToListAsync();

        var orphans = nodes.Where(n => n.TombstonedAt is null && !folderPaths.Contains(n.FolderPath)).ToList();

        findings.Add(new DoctorFinding(
            orphans.Count == 0 ? FindingLevel.Ok : FindingLevel.Warning,
            "access-rules",
            orphans.Count == 0
                ? $"{nodes.Count} folder(s) have explicit access rules; none orphaned."
                : $"{orphans.Count} access rule(s) point at folders that no longer exist: {string.Join(", ", orphans.Take(5).Select(o => o.FolderPath))}"));

        var expiring = nodes
            .Where(n => n.TombstonedAt is not null &&
                        n.TombstonedAt < DateTimeOffset.UtcNow.AddDays(-(options.Security.AclTombstoneDays - 7)))
            .ToList();

        if (expiring.Count > 0)
        {
            findings.Add(new DoctorFinding(FindingLevel.Warning, "access-rules",
                $"{expiring.Count} deleted folder(s) will lose their saved access rules within a week. " +
                "If they are meant to come back, restore them now."));
        }

        var userIds = await db.Users.Select(u => u.Id).ToHashSetAsync();
        var groupIds = await db.Groups.Select(g => g.Id).ToHashSetAsync();

        var dangling = nodes.SelectMany(n => n.Entries)
            .Count(e => e.SubjectType switch
            {
                AclSubjectType.User => e.SubjectId is not { } id || !userIds.Contains(id),
                AclSubjectType.Group => e.SubjectId is not { } id || !groupIds.Contains(id),
                _ => false,
            });

        if (dangling > 0)
        {
            findings.Add(new DoctorFinding(FindingLevel.Warning, "access-rules",
                $"{dangling} access rule entr(ies) name a person or group that no longer exists."));
        }

        var admins = await db.Users.CountAsync(u => u.Role == UserRole.Admin && u.Active);
        findings.Add(new DoctorFinding(
            admins == 0 ? FindingLevel.Error : FindingLevel.Ok,
            "administrators",
            admins == 0
                ? "There is no active administrator. Run 'compendio reset-admin-password'."
                : $"{admins} active administrator(s)."));
    }

    private static async Task CheckBackupAsync(List<DoctorFinding> findings, IServiceProvider services)
    {
        await using var db = services.GetRequiredService<IDbContextFactory<CompendioDbContext>>().CreateDbContext();

        var lastBackup = await db.Settings
            .Where(s => s.Key == SettingKeys.LastBackupAt)
            .Select(s => s.Value)
            .FirstOrDefaultAsync();

        if (!DateTimeOffset.TryParse(lastBackup, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
        {
            findings.Add(new DoctorFinding(FindingLevel.Warning, "backup",
                "No backup has ever been taken from this instance. Run 'compendio backup --out <file>'."));
            return;
        }

        var age = DateTimeOffset.UtcNow - at;

        // A backup that predates the newest secure scope restores into unreadable ciphertext, so
        // the age matters more here than it would for a plaintext-only wiki.
        var newestScope = await db.SecureScopes.OrderByDescending(s => s.CreatedAt).Select(s => s.CreatedAt).FirstOrDefaultAsync();

        if (newestScope != default && newestScope > at)
        {
            findings.Add(new DoctorFinding(FindingLevel.Warning, "backup",
                $"The last backup ({at:yyyy-MM-dd}) predates the newest secure scope ({newestScope:yyyy-MM-dd}). Take a new one."));
            return;
        }

        findings.Add(new DoctorFinding(
            age > TimeSpan.FromDays(30) ? FindingLevel.Warning : FindingLevel.Ok,
            "backup",
            $"The last backup was {(int)age.TotalDays} day(s) ago."));
    }

    private static void Print(IReadOnlyList<DoctorFinding> findings, string language)
    {
        var labels = new Dictionary<FindingLevel, string>
        {
            [FindingLevel.Ok] = Api.Common.LocalizedText.Get("doctor.ok", language),
            [FindingLevel.Warning] = Api.Common.LocalizedText.Get("doctor.warning", language),
            [FindingLevel.Error] = Api.Common.LocalizedText.Get("doctor.error", language),
        };

        var width = labels.Values.Max(l => l.Length);

        Console.WriteLine($"{CompendioConstants.ProductName} doctor");
        Console.WriteLine(new string('-', 60));

        foreach (var finding in findings)
        {
            Console.WriteLine($"[{labels[finding.Level].PadRight(width)}] {finding.Check,-16} {finding.Message}");
        }

        Console.WriteLine(new string('-', 60));

        var errors = findings.Count(f => f.Level == FindingLevel.Error);
        var warnings = findings.Count(f => f.Level == FindingLevel.Warning);

        Console.WriteLine(errors + warnings == 0
            ? "No problems found."
            : $"{errors} problem(s), {warnings} warning(s).");
        Console.WriteLine();
        Console.WriteLine("Paste this whole output into a GitHub issue if you need help — it contains no page content.");
    }

    private static string Bytes(long value) => value switch
    {
        < 1024 => $"{value} B",
        < 1024 * 1024 => $"{value / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{value / (1024.0 * 1024):F1} MB",
        _ => $"{value / (1024.0 * 1024 * 1024):F1} GB",
    };
}
