using System.IO.Compression;
using Compendio.Application.Abstractions;
using Compendio.Application.Common;
using Compendio.Domain.Entities;
using Compendio.Infrastructure.Crypto;
using Compendio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Compendio.Hosting;

/// <summary>
/// <c>compendio backup</c> and <c>compendio restore</c>.
/// </summary>
/// <remarks>
/// <para>
/// The archive holds the content folder as it sits on disk — ciphertext stays ciphertext — plus a
/// consistent <c>VACUUM INTO</c> copy of the database, which is why a backup taken under write load
/// restores cleanly.
/// </para>
/// <para>
/// The interesting decision is the key. Excluding <c>keys/</c> produces an archive that restores
/// into unreadable garbage, discovered months later; including it produces an archive where the key
/// sits next to the ciphertext, which means the encryption bought nothing. So: when any secure scope
/// exists, a passphrase is <em>required</em>, and the master key is rewrapped under it. Refusing the
/// passphrase refuses the backup, with a message that explains why.
/// </para>
/// </remarks>
public static class BackupCommand
{
    private const string DatabaseEntry = "db/compendio.db";
    private const string ContentPrefix = "content/";
    private const string KeyEntry = "keys/master.key.wrapped";
    private const string ManifestEntry = "compendio-backup.json";

    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        var output = CompendioCli.Value(args, "--out");
        if (string.IsNullOrWhiteSpace(output))
        {
            Console.Error.WriteLine("Usage: compendio backup --out <file> [--secure-passphrase <value>]");
            return 1;
        }

        await using var db = services.GetRequiredService<IDbContextFactory<CompendioDbContext>>().CreateDbContext();
        var scopeCount = await db.SecureScopes.CountAsync(s => s.RetiredAt == null);

        var passphrase = CompendioCli.Value(args, "--secure-passphrase")
                         ?? Environment.GetEnvironmentVariable("COMPENDIO_BACKUP_PASSPHRASE");

        if (scopeCount > 0 && string.IsNullOrWhiteSpace(passphrase))
        {
            Console.Error.WriteLine(
                $"""
                 This instance has {scopeCount} encrypted folder(s), so a backup passphrase is required.

                 Without it there are only two possible archives, and both are wrong: one that leaves out
                 the encryption key and restores into unreadable files, or one that stores the key beside
                 the ciphertext and gives away everything the encryption was for.

                 Re-run with --secure-passphrase <value>, or set COMPENDIO_BACKUP_PASSPHRASE.
                 Keep that passphrase somewhere other than this server — you will need it to restore.
                 """);

            return 1;
        }

        var result = await CreateAsync(services, output, passphrase);

        Console.WriteLine($"Wrote '{result.Path}': {result.Files} content file(s), the database, " +
                          $"{(result.KeyWrapped ? "and the master key rewrapped under your passphrase" : "and no keys (nothing is encrypted)")}.");

        if (result.KeyWrapped)
        {
            Console.WriteLine("Store the passphrase separately from this archive. Without it the archive cannot be restored.");
        }

        return 0;
    }

    /// <summary>
    /// Writes a backup archive to <paramref name="output"/> and records the moment it was taken.
    /// </summary>
    /// <remarks>
    /// Shared by <c>compendio backup</c> and the administration API. Throws
    /// <see cref="CompendioException"/> with <c>backup.passphrase_required</c> when encrypted folders
    /// exist and no passphrase was supplied, because the two archives possible without one are both
    /// wrong: one omits the key and restores into garbage, the other stores the key beside the
    /// ciphertext and gives away the encryption.
    /// </remarks>
    public static async Task<BackupResult> CreateAsync(
        IServiceProvider services, string output, string? passphrase, CancellationToken cancellationToken = default)
    {
        var dataDirectory = services.GetRequiredService<DataDirectory>();
        var masterKeys = services.GetRequiredService<MasterKeyStore>();

        await using var db = services.GetRequiredService<IDbContextFactory<CompendioDbContext>>().CreateDbContext();
        var scopeCount = await db.SecureScopes.CountAsync(s => s.RetiredAt == null, cancellationToken);

        if (scopeCount > 0 && string.IsNullOrWhiteSpace(passphrase))
        {
            throw new CompendioException(ProblemCodes.BackupPassphraseRequired, StatusCodes.Status400BadRequest, scopeCount);
        }

        var temporaryDatabase = Path.Combine(Path.GetTempPath(), $"compendio-backup-{Guid.CreateVersion7():N}.db");

        try
        {
            // VACUUM INTO gives a consistent copy without stopping writers, which is what makes a
            // backup-under-load restorable rather than merely successful.
            // VACUUM INTO takes a literal, not a parameter. The path is ours, from Path.GetTempPath.
            var vacuum = "VACUUM INTO '" + temporaryDatabase.Replace("'", "''", StringComparison.Ordinal) + "';";
            await db.Database.ExecuteSqlRawAsync(vacuum, cancellationToken);

            await using var stream = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

            archive.CreateEntryFromFile(temporaryDatabase, DatabaseEntry, CompressionLevel.Optimal);

            var files = 0;
            foreach (var file in Directory.EnumerateFiles(dataDirectory.Content, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(dataDirectory.Content, file).Replace(Path.DirectorySeparatorChar, '/');
                archive.CreateEntryFromFile(file, ContentPrefix + relative, CompressionLevel.Optimal);
                files++;
            }

            if (scopeCount > 0)
            {
                var master = masterKeys.TryGet()
                             ?? throw new CompendioException(
                                 ProblemCodes.BackupKeyUnavailable, StatusCodes.Status500InternalServerError);

                var wrapped = MasterKeyStore.WrapForExport(master, passphrase!);
                var entry = archive.CreateEntry(KeyEntry, CompressionLevel.NoCompression);

                await using var entryStream = entry.Open();
                await entryStream.WriteAsync(wrapped, cancellationToken);
            }

            var takenAt = DateTimeOffset.UtcNow;

            var manifest = archive.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
            await using (var manifestStream = manifest.Open())
            {
                await System.Text.Json.JsonSerializer.SerializeAsync(manifestStream, new
                {
                    product = Domain.CompendioConstants.ProductName,
                    version = Application.Admin.GetStatusHandler.BuildVersion,
                    takenAt,
                    files,
                    secureScopes = scopeCount,
                    keyWrapped = scopeCount > 0,
                }, cancellationToken: cancellationToken);
            }

            await RecordBackupAsync(db, services, cancellationToken);

            return new BackupResult(output, Path.GetFileName(output), files, scopeCount, scopeCount > 0, takenAt);
        }
        finally
        {
            if (File.Exists(temporaryDatabase))
            {
                File.Delete(temporaryDatabase);
            }
        }
    }

    public static Task<int> RestoreAsync(IServiceProvider services, string[] args)
    {
        var input = CompendioCli.Value(args, "--in");
        if (string.IsNullOrWhiteSpace(input) || !File.Exists(input))
        {
            Console.Error.WriteLine("Usage: compendio restore --in <file> [--secure-passphrase <value>]");
            return Task.FromResult(1);
        }

        var dataDirectory = services.GetRequiredService<DataDirectory>();

        if (Directory.EnumerateFileSystemEntries(dataDirectory.Content).Any() && !CompendioCli.HasFlag(args, "--force"))
        {
            Console.Error.WriteLine(
                $"'{dataDirectory.Content}' is not empty. Restoring would overwrite it. " +
                "Move it aside first, or pass --force if that is what you want.");
            return Task.FromResult(1);
        }

        var passphrase = CompendioCli.Value(args, "--secure-passphrase")
                         ?? Environment.GetEnvironmentVariable("COMPENDIO_BACKUP_PASSPHRASE");

        using var archive = ZipFile.OpenRead(input);

        var keyEntry = archive.GetEntry(KeyEntry);
        if (keyEntry is not null)
        {
            if (string.IsNullOrWhiteSpace(passphrase))
            {
                Console.Error.WriteLine(
                    "This archive contains an encryption key wrapped under a passphrase. " +
                    "Re-run with --secure-passphrase <value>, or set COMPENDIO_BACKUP_PASSPHRASE.");
                return Task.FromResult(1);
            }

            using var keyStream = keyEntry.Open();
            using var buffer = new MemoryStream();
            keyStream.CopyTo(buffer);

            // Unwrap before writing anything else: a wrong passphrase must fail before the restore
            // has half-replaced the data directory.
            var master = MasterKeyStore.UnwrapFromExport(buffer.ToArray(), passphrase);

            Directory.CreateDirectory(dataDirectory.Keys);
            RestoreMasterKey(services, master);
        }

        Directory.CreateDirectory(dataDirectory.Content);
        Directory.CreateDirectory(dataDirectory.Database);

        // Microsoft.Data.Sqlite pools connections, so a handle can outlive the context that opened
        // it and keep the database file locked against the overwrite below.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var restored = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName == ManifestEntry || entry.FullName == KeyEntry || entry.Name.Length == 0)
            {
                continue;
            }

            string target;
            if (entry.FullName == DatabaseEntry)
            {
                target = dataDirectory.DatabaseFile;
            }
            else if (entry.FullName.StartsWith(ContentPrefix, StringComparison.Ordinal))
            {
                var relative = entry.FullName[ContentPrefix.Length..];

                // Zip-slip: an archive entry is untrusted input like any other path.
                if (!Domain.Content.PathPolicy.Validate(relative, Domain.Content.PathKind.Any).IsValid)
                {
                    Console.Error.WriteLine($"Skipping an archive entry with an unsafe name: '{entry.FullName}'.");
                    continue;
                }

                target = Path.Combine(dataDirectory.Content, relative.Replace('/', Path.DirectorySeparatorChar));
            }
            else
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
            restored++;
        }

        Console.WriteLine($"Restored {restored} file(s) into '{dataDirectory.Root}'.");
        Console.WriteLine("Start Compendio; it will reconcile the content folder and rebuild the search index.");
        return Task.FromResult(0);
    }

    /// <summary>
    /// Writes the restored master key back out under this machine's protection.
    /// </summary>
    /// <remarks>
    /// Restoring on a different machine is the case that matters: DPAPI at <c>LocalMachine</c> scope
    /// is machine-bound, so the key has to be re-protected here rather than copied verbatim.
    /// </remarks>
    private static void RestoreMasterKey(IServiceProvider services, byte[] master)
    {
        var dataDirectory = services.GetRequiredService<DataDirectory>();
        var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Configuration.CompendioOptions>>();

        var store = new MasterKeyStore(dataDirectory, options,
            services.GetRequiredService<ILoggerFactory>().CreateLogger<MasterKeyStore>());

        store.Import(master);
    }

    private static async Task RecordBackupAsync(CompendioDbContext db, IServiceProvider services, CancellationToken cancellationToken)
    {
        var clock = services.GetRequiredService<IClock>();
        var setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == SettingKeys.LastBackupAt, cancellationToken);

        if (setting is null)
        {
            db.Settings.Add(new Setting { Key = SettingKeys.LastBackupAt, Value = clock.UtcNow.ToString("O"), UpdatedAt = clock.UtcNow });
        }
        else
        {
            setting.Value = clock.UtcNow.ToString("O");
            setting.UpdatedAt = clock.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// The outcome of a backup, returned to the CLI and the administration API.
/// </summary>
/// <param name="Path">The full path the archive was written to.</param>
/// <param name="FileName">The archive file name on its own, for display.</param>
/// <param name="Files">How many content files went into the archive.</param>
/// <param name="SecureScopes">How many encrypted folders existed at backup time.</param>
/// <param name="KeyWrapped">Whether the master key was rewrapped into the archive under the passphrase.</param>
/// <param name="TakenAt">When the backup was taken.</param>
public sealed record BackupResult(
    string Path, string FileName, int Files, int SecureScopes, bool KeyWrapped, DateTimeOffset TakenAt);
