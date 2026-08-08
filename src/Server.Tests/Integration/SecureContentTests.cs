using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Shouldly;

namespace Compendio.Tests.Integration;

/// <summary>
/// Acceptance criteria 10 and 11 — the two that decide whether "encrypted folder" means anything.
/// </summary>
/// <remarks>
/// This class runs against its own instance, because it deletes the encryption keys and restarts.
/// It is in the shared collection so it never runs concurrently with anything else: the fixture
/// configures itself through environment variables, which are process-global.
/// </remarks>
[Collection(nameof(CompendioCollection))]
public sealed class SecureContentTests : IAsyncLifetime
{
    /// <summary>The needle. If this string turns up anywhere it should not, the feature is a lie.</summary>
    private const string Secret = "Zx7-QUETZAL-ROUTER-PASSPHRASE-9931";

    private const string SecureFolder = "Secretos";
    private const string SecurePage = "Secretos/router.md";
    private const string PublicPage = "Publico/aviso.md";

    private static JsonSerializerOptions Json => CompendioApplication.Json;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private CompendioApplication _app = null!;
    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync()
    {
        _app = new CompendioApplication();
        await _app.InitializeAsync();

        _admin = await _app.SignInAsAdminAsync();

        var secure = await _admin.PostAsJsonAsync("/api/v1/pages", new
        {
            folderPath = SecureFolder,
            title = "Router",
            content = $"---\ntitle: Router\ntags: [credenciales]\n---\n\n# Router\n\nadmin / {Secret}\n",
        }, Json, Ct);

        secure.EnsureSuccessStatusCode();

        var open = await _admin.PostAsJsonAsync("/api/v1/pages", new
        {
            folderPath = "Publico",
            title = "Aviso",
            content = "---\ntitle: Aviso\n---\n\nNada sensible aquí.\n",
        }, Json, Ct);

        open.EnsureSuccessStatusCode();

        var scope = await _admin.PostAsJsonAsync("/api/v1/admin/secure-scopes", new
        {
            path = SecureFolder,
            indexContent = false,
            allowAi = false,
        }, Json, Ct);

        scope.EnsureSuccessStatusCode();
    }

    public async ValueTask DisposeAsync()
    {
        var directory = _app.DataDirectory;

        _admin.Dispose();
        await _app.DisposeAsync();

        // The restart test replaces _app with one that does not own the directory, so clean up here
        // rather than relying on whichever instance happens to be current.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // A handle can outlive the host on Windows; a leftover temp folder is not worth failing.
        }
    }

    [Fact]
    public void TheFileOnDiskIsAnEnvelope()
    {
        // Files-first is suspended inside a secure scope, and this is what that means concretely.
        _app.FileExists(SecurePage).ShouldBeFalse();
        _app.FileExists(SecurePage + ".enc").ShouldBeTrue();
    }

    /// <summary>
    /// Criterion 10, the byte scan: the plaintext appears in none of the content folder, the
    /// database, or the logs.
    /// </summary>
    [Fact]
    public void ThePlaintextAppearsNowhereItShouldNot()
    {
        var needle = Encoding.UTF8.GetBytes(Secret);
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(_app.DataDirectory, "*", SearchOption.AllDirectories))
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(file);
            }
            catch (IOException)
            {
                // Held open by the running host; the interesting files are all readable.
                continue;
            }

            if (bytes.AsSpan().IndexOf(needle) >= 0)
            {
                offenders.Add(Path.GetRelativePath(_app.DataDirectory, file));
            }
        }

        offenders.ShouldBeEmpty(
            $"the secret must not appear in the content folder, the database (including -wal), or the logs; " +
            $"found in: {string.Join(", ", offenders)}");
    }

    /// <summary>
    /// History travels with the page, so marking a folder secure has to encrypt what is already
    /// stored for it.
    /// </summary>
    /// <remarks>
    /// The page in this fixture existed <em>before</em> the folder became a scope, so its first
    /// versions were written in the clear. Encrypting only new snapshots would leave every earlier
    /// revision of the document sitting in <c>compendio.db</c> — the whole page, not a fragment, and
    /// readable by anyone holding the file. The byte scan does not catch this on its own because the
    /// stored blob is compressed, which is exactly why this assertion looks at the envelope instead.
    /// </remarks>
    [Fact]
    public async Task HistoryWrittenBeforeTheScopeExistedIsEncryptedToo()
    {
        var versions = await _admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/versions?path={SecurePage}", Json, Ct);

        versions.GetArrayLength().ShouldBeGreaterThan(0);

        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_app.DatabaseFile}");
        await connection.OpenAsync(Ct);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT IsEncrypted, Content FROM PageVersions WHERE Path LIKE 'Secretos/%'";

        var rows = 0;
        await using var reader = await command.ExecuteReaderAsync(Ct);

        while (await reader.ReadAsync(Ct))
        {
            rows++;
            reader.GetBoolean(0).ShouldBeTrue("a version stored under a secure scope must be encrypted");

            var blob = (byte[])reader["Content"];
            Encoding.ASCII.GetString(blob, 0, 8).ShouldBe("CMPDENC1");
        }

        rows.ShouldBeGreaterThan(0, "the secure page must have stored history to encrypt");

        // And it still reads back — encrypting it must not have made it unrecoverable.
        var first = versions[versions.GetArrayLength() - 1].GetProperty("id").GetGuid();
        var content = await _admin.GetFromJsonAsync<JsonElement>($"/api/v1/versions/{first}", Json, Ct);

        content.GetProperty("content").GetString()!.ShouldContain(Secret);
    }

    /// <summary>An unopted-in secure scope is not indexed, so its text is not in the database at all.</summary>
    [Fact]
    public async Task SecurePagesAreNotIndexedByDefault()
    {
        var results = await _admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/search?q=QUETZAL", Json, Ct);

        results.GetProperty("totalCount").GetInt32().ShouldBe(0);
    }

    /// <summary>
    /// A scope covers everything below it, opting in included.
    /// </summary>
    /// <remarks>
    /// The query predicate has to test the page's folder against the scope as a <em>prefix</em>. A
    /// straight comparison against the scope list matches only pages sitting directly in the scope
    /// folder, so an admin who deliberately opted a scope into search would still find nothing in
    /// any subfolder of it — failing closed, but silently, and with no way to tell it from an index
    /// that had not caught up.
    /// </remarks>
    [Fact]
    public async Task OptingAScopeIntoSearchCoversItsSubfolders()
    {
        var nested = await _admin.PostAsJsonAsync("/api/v1/pages", new
        {
            folderPath = $"{SecureFolder}/interno",
            title = "Switch",
            content = "---\ntitle: Switch\n---\n\nclave TUCAN-SWITCH-4417\n",
        }, Json, Ct);

        nested.EnsureSuccessStatusCode();

        var optIn = await _admin.PutAsJsonAsync($"/api/v1/admin/secure-scopes/{SecureFolder}", new
        {
            indexContent = true,
        }, Json, Ct);

        optIn.EnsureSuccessStatusCode();
        await WaitForIndexAsync();

        var results = await _admin.GetFromJsonAsync<JsonElement>("/api/v1/search?q=TUCAN", Json, Ct);

        results.GetProperty("totalCount").GetInt32().ShouldBeGreaterThan(0);
        results.GetProperty("items").EnumerateArray()
            .ShouldContain(hit => hit.GetProperty("path").GetString() == $"{SecureFolder}/interno/switch.md");
    }

    private async Task WaitForIndexAsync()
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var ready = await _admin.GetFromJsonAsync<JsonElement>("/ready", Json, Ct);

            if (ready.GetProperty("queueDepth").GetInt32() == 0 && ready.GetProperty("index").GetString() == "ready")
            {
                await Task.Delay(200, Ct);
                return;
            }

            await Task.Delay(250, Ct);
        }

        throw new TimeoutException("The search index did not become ready within 15 seconds.");
    }

    [Fact]
    public async Task AnAdministratorCanStillReadAndWriteIt()
    {
        var page = await _admin.GetFromJsonAsync<JsonElement>($"/api/v1/pages/{SecurePage}", Json, Ct);

        page.GetProperty("isSecure").GetBoolean().ShouldBeTrue();
        page.GetProperty("content").GetString()!.ShouldContain(Secret);

        var save = await _admin.PutAsJsonAsync($"/api/v1/pages/{SecurePage}", new
        {
            content = $"---\ntitle: Router\n---\n\nadmin / {Secret}\n\nRevisado.\n",
            expectedHash = page.GetProperty("contentHash").GetString(),
        }, Json, Ct);

        save.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Criterion 10's second half: a non-admin with explicit read can read, and gets
    /// <c>secure.admin_required</c> on write.
    /// </summary>
    [Fact]
    public async Task ANonAdminWithReadCanReadButNotWrite()
    {
        var create = await _admin.PostAsJsonAsync("/api/v1/admin/users", new
        {
            userName = "carmen",
            password = "Compendio!Test3",
            displayName = "Carmen Ruiz",
            role = "Editor",
        }, Json, Ct);

        create.EnsureSuccessStatusCode();
        var carmen = await create.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        var carmenId = carmen.GetProperty("id").GetGuid();

        // Marking a folder secure cuts inheritance, so she has to be listed explicitly.
        var acl = await _admin.PutAsJsonAsync($"/api/v1/acl/{SecureFolder}", new
        {
            inheritParent = false,
            entries = new[] { new { subjectType = "User", subjectId = carmenId, level = "Manage" } },
        }, Json, Ct);

        acl.EnsureSuccessStatusCode();

        using var client = await _app.SignInAsync("carmen", "Compendio!Test3");

        var read = await client.GetAsync($"/api/v1/pages/{SecurePage}", Ct);
        read.StatusCode.ShouldBe(HttpStatusCode.OK);

        var page = await read.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);

        // Granted Manage, and still capped at Read, because the evaluator enforces it rather than
        // the UI.
        page.GetProperty("level").GetString().ShouldBe("Read");

        var write = await client.PutAsJsonAsync($"/api/v1/pages/{SecurePage}", new
        {
            content = "cambiado\n",
            expectedHash = page.GetProperty("contentHash").GetString(),
        }, Json, Ct);

        write.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var problem = await write.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        problem.GetProperty("code").GetString().ShouldBe("secure.admin_required");
    }

    /// <summary>Criterion 11, second half: a flipped byte is refused, never partially rendered.</summary>
    [Fact]
    public async Task AFlippedByteIsReportedAsTampered()
    {
        var page = await _admin.PostAsJsonAsync("/api/v1/pages", new
        {
            folderPath = SecureFolder,
            title = "Tamper me",
            content = "---\ntitle: Tamper me\n---\n\nIntacto.\n",
        }, Json, Ct);

        page.EnsureSuccessStatusCode();
        var created = await page.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        var path = created.GetProperty("path").GetString()!;

        var onDisk = Path.Combine(_app.ContentRoot, path.Replace('/', Path.DirectorySeparatorChar) + ".enc");
        File.Exists(onDisk).ShouldBeTrue();

        var bytes = await File.ReadAllBytesAsync(onDisk, Ct);
        bytes[^5] ^= 0x01;
        await File.WriteAllBytesAsync(onDisk, bytes, Ct);

        var read = await _admin.GetAsync($"/api/v1/pages/{path}", Ct);

        read.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var problem = await read.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        problem.GetProperty("code").GetString().ShouldBe("secure.tampered");

        // And nothing of the plaintext leaked into the error.
        problem.GetRawText().ShouldNotContain("Intacto");
    }

    /// <summary>
    /// Criterion 11, first half: delete <c>keys/</c> and restart. The service starts, non-secure
    /// content works, secure scopes report unavailable, nothing crashes and nothing is emptied.
    /// </summary>
    [Fact]
    public async Task WithTheKeysDeletedTheServiceStillStarts()
    {
        var dataDirectory = _app.DataDirectory;

        // Stop the instance so the key files are not held open, but keep its data — that data is
        // exactly what the restart has to come back to.
        _admin.Dispose();
        await _app.ShutDownKeepingDataAsync();

        var keys = Path.Combine(dataDirectory, "keys");
        var rescued = Path.Combine(dataDirectory, "keys-moved-aside");
        Directory.Move(keys, rescued);

        var restarted = CompendioApplication.StartingFrom(dataDirectory);
        await restarted.InitializeAsync();

        using (var client = await restarted.SignInAsAdminAsync())
        {
            // Non-secure content is unaffected. This is the "must not take the whole wiki down" half.
            var open = await client.GetAsync($"/api/v1/pages/{PublicPage}", Ct);
            open.StatusCode.ShouldBe(HttpStatusCode.OK);

            // The secure page reports unavailable rather than serving anything. This is the "must
            // not fail open" half.
            var secure = await client.GetAsync($"/api/v1/pages/{SecurePage}", Ct);
            secure.StatusCode.ShouldBeOneOf(HttpStatusCode.ServiceUnavailable, HttpStatusCode.UnprocessableEntity);

            var problem = await secure.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
            problem.GetProperty("code").GetString().ShouldStartWith("secure.");
            problem.GetRawText().ShouldNotContain(Secret);

            // And the ciphertext is still on disk — nothing was silently emptied.
            File.Exists(Path.Combine(restarted.ContentRoot,
                SecurePage.Replace('/', Path.DirectorySeparatorChar) + ".enc")).ShouldBeTrue();
        }

        await restarted.ShutDownKeepingDataAsync();

        // Starting without keys recreated an empty keys/ (with a fresh Data Protection ring), so it
        // has to go before the real one can come back.
        Directory.Delete(keys, recursive: true);
        Directory.Move(rescued, keys);

        // With the keys back, the same instance reads the secure page again — the ciphertext was
        // never damaged, only unreadable.
        _app = CompendioApplication.StartingFrom(dataDirectory);
        await _app.InitializeAsync();
        _admin = await _app.SignInAsAdminAsync();

        var recovered = await _admin.GetAsync($"/api/v1/pages/{SecurePage}", Ct);
        recovered.StatusCode.ShouldBe(HttpStatusCode.OK);

        var page = await recovered.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        page.GetProperty("content").GetString()!.ShouldContain(Secret);
    }
}
