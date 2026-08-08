using System.Net.Http.Json;
using System.Text.Json;
using Compendio.Hosting;
using Compendio.Hosting.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Compendio.Tests.Integration;

/// <summary>
/// Criterion 15 — backup, wipe, restore, and everything is still there.
/// </summary>
/// <remarks>
/// <para>
/// Restores into a <em>different</em> data directory rather than over the original, which is the
/// case that actually matters: an organization restoring onto a replacement machine. It is also the
/// case where the master key has to be re-protected on the way in, since DPAPI at
/// <c>LocalMachine</c> scope is bound to the machine that wrote it.
/// </para>
/// <para>
/// The backup runs under concurrent write load, because <c>VACUUM INTO</c> is chosen precisely so
/// that a backup taken while somebody is typing restores consistently — an assertion that only
/// means something if somebody is typing.
/// </para>
/// </remarks>
[Collection(nameof(CompendioCollection))]
public sealed class BackupRestoreTests : IAsyncLifetime
{
    private const string Passphrase = "correct horse battery staple";
    private const string Secret = "Kx4-BACKUP-ONLY-SECRET-5512";

    private static JsonSerializerOptions Json => CompendioApplication.Json;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private CompendioApplication _app = null!;
    private HttpClient _admin = null!;
    private string _archive = null!;

    // Captured rather than assumed: a page's file name is slugified from its title, so the path is
    // the API's answer and not something a caller gets to predict.
    private string _openPath = null!;
    private string _securePath = null!;

    public async ValueTask InitializeAsync()
    {
        _app = new CompendioApplication();
        await _app.InitializeAsync();
        _admin = await _app.SignInAsAdminAsync();

        _archive = Path.Combine(Path.GetTempPath(), $"compendio-backup-{Guid.CreateVersion7():N}.zip");
    }

    public async ValueTask DisposeAsync()
    {
        var directory = _app.DataDirectory;

        _admin.Dispose();
        await _app.DisposeAsync();

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        foreach (var path in new[] { _archive })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        if (Directory.Exists(directory))
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task BackupRefusesWithoutAPassphraseWhenAnythingIsEncrypted()
    {
        await CreateContentAsync(withSecureScope: true);

        var exitCode = await RunCliAsync(_app, ["backup", "--out", _archive]);

        // Not bureaucracy: without a passphrase the only two possible archives are one that cannot
        // be restored and one that gives away what the encryption was for.
        exitCode.ShouldBe(1);
        File.Exists(_archive).ShouldBeFalse();
    }

    [Fact]
    public async Task BackupEndpointWritesToTheServerBackupsFolder()
    {
        await CreateContentAsync(withSecureScope: false);

        var backupsBefore = BackupFiles();

        var response = await _admin.PostAsJsonAsync("/api/v1/admin/backup", new { }, Json, Ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        result.GetProperty("keyWrapped").GetBoolean().ShouldBeFalse();
        result.GetProperty("secureScopes").GetInt32().ShouldBe(0);
        var fileName = result.GetProperty("fileName").GetString()!;

        // The archive lands in the server's backups folder — a location the caller never chose.
        var written = BackupFiles();
        written.Length.ShouldBe(backupsBefore.Length + 1);
        written.ShouldContain(f => Path.GetFileName(f) == fileName);

        // And the status screen's last-backup time now has a value.
        var status = await _admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/status", Json, Ct);
        status.GetProperty("lastBackupAt").ValueKind.ShouldBe(JsonValueKind.String);
    }

    [Fact]
    public async Task BackupEndpointRefusesWithoutAPassphraseWhenAnythingIsEncrypted()
    {
        await CreateContentAsync(withSecureScope: true);

        var refused = await _admin.PostAsJsonAsync("/api/v1/admin/backup", new { }, Json, Ct);

        refused.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        problem.GetProperty("code").GetString().ShouldBe("backup.passphrase_required");

        // No archive is written when the request is refused.
        BackupFiles().ShouldBeEmpty();

        // With the passphrase supplied it succeeds and rewraps the key into the archive.
        var accepted = await _admin.PostAsJsonAsync("/api/v1/admin/backup", new { passphrase = Passphrase }, Json, Ct);
        accepted.EnsureSuccessStatusCode();

        var result = await accepted.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        result.GetProperty("keyWrapped").GetBoolean().ShouldBeTrue();
        BackupFiles().Length.ShouldBe(1);
    }

    private string[] BackupFiles()
    {
        var folder = Path.Combine(_app.DataDirectory, "backups");
        return Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*.zip")
            : [];
    }

    [Fact]
    public async Task BackupAndRestoreOntoADifferentMachineKeepsEverything()
    {
        await CreateContentAsync(withSecureScope: true);

        // Concurrent write load while the backup runs.
        using var writing = new CancellationTokenSource();
        var load = Task.Run(async () =>
        {
            var counter = 0;
            while (!writing.IsCancellationRequested)
            {
                try
                {
                    await _admin.PostAsJsonAsync("/api/v1/pages", new
                    {
                        folderPath = "Ruido",
                        title = $"Página {counter++}",
                        content = $"---\ntitle: Ruido {counter}\n---\n\nContenido {counter}.\n",
                    }, Json, writing.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }, Ct);

        var exitCode = await RunCliAsync(_app, ["backup", "--out", _archive, "--secure-passphrase", Passphrase]);

        await writing.CancelAsync();
        await load;

        exitCode.ShouldBe(0);
        File.Exists(_archive).ShouldBeTrue();

        // A different machine: a data directory that has never seen this instance's keys.
        var restoredDirectory = Path.Combine(Path.GetTempPath(), $"compendio-restored-{Guid.CreateVersion7():N}");

        try
        {
            var restoreCode = await RunCliAsync(
                dataDirectory: restoredDirectory,
                ["restore", "--in", _archive, "--secure-passphrase", Passphrase]);

            restoreCode.ShouldBe(0);

            await using var restored = CompendioApplication.StartingFrom(restoredDirectory);
            await restored.InitializeAsync();

            using var client = await restored.SignInAsync("admin", "Compendio!Test1");

            // Content.
            var page = await client.GetFromJsonAsync<JsonElement>($"/api/v1/pages/{_openPath}", Json, Ct);
            page.GetProperty("title").GetString().ShouldBe("Nota abierta");

            // Users and their roles.
            var me = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me", Json, Ct);
            me.GetProperty("authenticated").GetBoolean().ShouldBeTrue();
            me.GetProperty("user").GetProperty("role").GetString().ShouldBe("Admin");

            // Permissions.
            var acl = await client.GetFromJsonAsync<JsonElement>("/api/v1/acl/Restringido", Json, Ct);
            acl.GetProperty("inheritParent").GetBoolean().ShouldBeFalse();

            // History.
            var versions = await client.GetFromJsonAsync<JsonElement>(
                $"/api/v1/versions?path={_openPath}", Json, Ct);
            versions.GetArrayLength().ShouldBeGreaterThan(0);

            // And the encrypted page — the part that needs the rewrapped key to have survived.
            var secure = await client.GetFromJsonAsync<JsonElement>($"/api/v1/pages/{_securePath}", Json, Ct);
            secure.GetProperty("isSecure").GetBoolean().ShouldBeTrue();
            secure.GetProperty("content").GetString()!.ShouldContain(Secret);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            if (Directory.Exists(restoredDirectory))
            {
                try
                {
                    Directory.Delete(restoredDirectory, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    [Fact]
    public async Task RestoreRefusesTheWrongPassphrase()
    {
        await CreateContentAsync(withSecureScope: true);

        (await RunCliAsync(_app, ["backup", "--out", _archive, "--secure-passphrase", Passphrase])).ShouldBe(0);

        var target = Path.Combine(Path.GetTempPath(), $"compendio-wrong-{Guid.CreateVersion7():N}");

        try
        {
            var exitCode = await RunCliAsync(
                dataDirectory: target,
                ["restore", "--in", _archive, "--secure-passphrase", "not the passphrase"]);

            exitCode.ShouldBe(1);

            // It failed before writing anything, which is the point of unwrapping the key first.
            Directory.Exists(Path.Combine(target, "content")).ShouldBeTrue();
            Directory.EnumerateFiles(Path.Combine(target, "content"), "*", SearchOption.AllDirectories)
                .ShouldBeEmpty();
        }
        finally
        {
            if (Directory.Exists(target))
            {
                try
                {
                    Directory.Delete(target, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private async Task CreateContentAsync(bool withSecureScope)
    {
        var open = await _admin.PostAsJsonAsync("/api/v1/pages", new
        {
            folderPath = "Abierto",
            title = "Nota abierta",
            content = "---\ntitle: Nota abierta\n---\n\nVisible para todos.\n",
        }, Json, Ct);

        open.EnsureSuccessStatusCode();

        // A second version, so history has something to restore.
        var created = await open.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        _openPath = created.GetProperty("path").GetString()!;

        var update = await _admin.PutAsJsonAsync($"/api/v1/pages/{_openPath}", new
        {
            content = "---\ntitle: Nota abierta\n---\n\nVisible para todos, revisado.\n",
            expectedHash = created.GetProperty("contentHash").GetString(),
        }, Json, Ct);

        update.EnsureSuccessStatusCode();

        var restricted = await _admin.PostAsJsonAsync("/api/v1/pages", new
        {
            folderPath = "Restringido",
            title = "Interno",
            content = "---\ntitle: Interno\n---\n\nSolo para algunos.\n",
        }, Json, Ct);

        restricted.EnsureSuccessStatusCode();

        var acl = await _admin.PutAsJsonAsync("/api/v1/acl/Restringido", new
        {
            inheritParent = false,
            entries = Array.Empty<object>(),
        }, Json, Ct);

        acl.EnsureSuccessStatusCode();

        if (!withSecureScope)
        {
            return;
        }

        var secure = await _admin.PostAsJsonAsync("/api/v1/pages", new
        {
            folderPath = "Cifrado",
            title = "Clave",
            content = $"---\ntitle: Clave\n---\n\n{Secret}\n",
        }, Json, Ct);

        secure.EnsureSuccessStatusCode();
        _securePath = (await secure.Content.ReadFromJsonAsync<JsonElement>(Json, Ct))
            .GetProperty("path").GetString()!;

        var scope = await _admin.PostAsJsonAsync("/api/v1/admin/secure-scopes", new
        {
            path = "Cifrado",
            indexContent = false,
            allowAi = false,
        }, Json, Ct);

        scope.EnsureSuccessStatusCode();
    }

    private static Task<int> RunCliAsync(CompendioApplication app, string[] args) =>
        RunCliAsync(app.DataDirectory, args);

    /// <summary>
    /// Runs a CLI verb in-process against a data directory.
    /// </summary>
    /// <remarks>
    /// Wires the same services <c>CompendioCli</c> does rather than shelling out, so the test
    /// exercises the real command implementations and can be debugged.
    /// </remarks>
    private static async Task<int> RunCliAsync(string dataDirectory, string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DataDir"] = dataDirectory })
            .Build();

        var options = new CompendioOptions { DataDir = dataDirectory };

        var directory = DataDirectory.Resolve(options);
        directory.EnsureCreated();

        var services = new ServiceCollection();
        services.AddCompendioForCli(configuration, directory, options);

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        try
        {
            return args[0] switch
            {
                "backup" => await BackupCommand.RunAsync(scope.ServiceProvider, args),
                "restore" => await BackupCommand.RestoreAsync(scope.ServiceProvider, args),
                _ => throw new ArgumentException($"Unsupported verb '{args[0]}'."),
            };
        }
        catch (Exception)
        {
            // The CLI turns an exception into exit code 1 and a message; mirror that here so a
            // refusal reads the same way to the test as it does to an operator.
            return 1;
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }
}
