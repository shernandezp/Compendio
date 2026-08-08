using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;

namespace Compendio.Tests.Integration;

/// <summary>
/// The folder is the database of record — criteria 2, 9 and 13.
/// </summary>
/// <remarks>
/// Its own instance, because it edits the content folder from outside and forces reconciliation,
/// which would disturb assertions in the shared fixture.
/// </remarks>
[Collection(nameof(CompendioCollection))]
public sealed class ReconciliationTests : IAsyncLifetime
{
    private static JsonSerializerOptions Json => CompendioApplication.Json;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private CompendioApplication _app = null!;
    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync()
    {
        _app = new CompendioApplication();
        await _app.InitializeAsync();
        _admin = await _app.SignInAsAdminAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _admin.Dispose();
        await _app.DisposeAsync();
    }

    /// <summary>
    /// Criterion 2: a file written in the content folder shows up in the API.
    /// </summary>
    /// <remarks>
    /// Driven through an explicit reconciliation rather than by sleeping on the watcher. The watcher
    /// is an optimization and is allowed to be late; reconciliation is the guarantee, and it is the
    /// guarantee that is worth asserting deterministically.
    /// </remarks>
    [Fact]
    public async Task AFileWrittenOutsideCompendioBecomesAPage()
    {
        await _app.WriteFileAsync("Externo/manual.md",
            "---\ntitle: Manual externo\nlang: es\ntags: [externo]\n---\n\n# Manual\n\nEscrito en VS Code.\n");

        await ReconcileAsync();

        var page = await _admin.GetFromJsonAsync<JsonElement>("/api/v1/pages/Externo/manual.md", Json, Ct);

        page.GetProperty("title").GetString().ShouldBe("Manual externo");
        page.GetProperty("html").GetString()!.ShouldContain("Escrito en VS Code");

        // Attribution honesty: the edit came from the file system, so it is credited to nobody.
        page.GetProperty("lastEditWasExternal").GetBoolean().ShouldBeTrue();
        page.TryGetProperty("updatedBy", out var updatedBy).ShouldBeFalse($"got '{updatedBy}'");

        // And it is not canonical, because no human has saved it in the editor yet.
        page.GetProperty("isCanonical").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task AFileDeletedOutsideCompendioRemovesThePage()
    {
        await _app.WriteFileAsync("Externo/temporal.md", "---\ntitle: Temporal\n---\n\nBorrar.\n");
        await ReconcileAsync();

        (await _admin.GetAsync("/api/v1/pages/Externo/temporal.md", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        File.Delete(Path.Combine(_app.ContentRoot, "Externo", "temporal.md"));
        await ReconcileAsync();

        (await _admin.GetAsync("/api/v1/pages/Externo/temporal.md", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Criterion 9: a restricted folder that disappears and comes back stays restricted.
    /// </summary>
    /// <remarks>
    /// This is the mis-synced-backup-client scenario. Dropping the access rules when the folder
    /// vanished would mean the folder returns inheriting — that is, readable by everyone — and
    /// nobody would notice until somebody read something they should not have.
    /// </remarks>
    [Fact]
    public async Task AFolderDeletedAndRecreatedKeepsItsRestriction()
    {
        await _app.WriteFileAsync("Confidencial/acta.md", "---\ntitle: Acta\n---\n\nContenido reservado.\n");
        await ReconcileAsync();

        var acl = await _admin.PutAsJsonAsync("/api/v1/acl/Confidencial", new
        {
            inheritParent = false,
            entries = Array.Empty<object>(),
        }, Json, Ct);

        acl.EnsureSuccessStatusCode();

        var outsider = await CreateOutsiderAsync("dario", "Compendio!Test4");
        (await outsider.GetAsync("/api/v1/pages/Confidencial/acta.md", Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // The folder disappears from disk — a sync client, a restore, somebody's mistake.
        Directory.Delete(Path.Combine(_app.ContentRoot, "Confidencial"), recursive: true);
        await ReconcileAsync();

        // …and comes back, inside the tombstone window.
        await _app.WriteFileAsync("Confidencial/acta.md", "---\ntitle: Acta\n---\n\nContenido reservado.\n");
        await ReconcileAsync();

        // Still restricted. The rules were tombstoned, not dropped, and revived on the way back.
        (await outsider.GetAsync("/api/v1/pages/Confidencial/acta.md", Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var reloaded = await _admin.GetFromJsonAsync<JsonElement>("/api/v1/acl/Confidencial", Json, Ct);
        reloaded.GetProperty("inheritParent").GetBoolean().ShouldBeFalse();

        outsider.Dispose();
    }

    /// <summary>
    /// Criterion 9, first half: renaming a restricted folder on disk keeps it restricted.
    /// </summary>
    /// <remarks>
    /// Uncorrelated, a rename is a delete plus a create: the old path's rules are tombstoned, the
    /// new path inherits from its parent, and a folder three people could see is readable by
    /// everybody with the same documents still in it. Nobody gets an error, and nothing in the UI
    /// says anything happened. The pages lose their identity and their history at the same time.
    /// </remarks>
    [Fact]
    public async Task RenamingARestrictedFolderOnDiskKeepsItRestricted()
    {
        await _app.WriteFileAsync("Reservado/nomina.md", "---\ntitle: Nómina\n---\n\nSalarios.\n");
        await _app.WriteFileAsync("Reservado/sub/bonus.md", "---\ntitle: Bonus\n---\n\nMás salarios.\n");
        await ReconcileAsync();

        var acl = await _admin.PutAsJsonAsync("/api/v1/acl/Reservado", new
        {
            inheritParent = false,
            entries = Array.Empty<object>(),
        }, Json, Ct);

        acl.EnsureSuccessStatusCode();

        using var outsider = await CreateOutsiderAsync("fatima", "Compendio!Test6");
        (await outsider.GetAsync("/api/v1/pages/Reservado/nomina.md", Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var versionsBefore = await _admin.GetFromJsonAsync<JsonElement>(
            "/api/v1/versions?path=Reservado/nomina.md", Json, Ct);

        // Somebody renames it in Explorer.
        Directory.Move(
            Path.Combine(_app.ContentRoot, "Reservado"),
            Path.Combine(_app.ContentRoot, "Confidencial-RRHH"));

        await ReconcileAsync();

        // Still restricted, at the new path, all the way down.
        (await outsider.GetAsync("/api/v1/pages/Confidencial-RRHH/nomina.md", Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await outsider.GetAsync("/api/v1/pages/Confidencial-RRHH/sub/bonus.md", Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await outsider.GetStringAsync("/api/v1/tree", Ct)).ShouldNotContain("Confidencial-RRHH");

        var reloaded = await _admin.GetFromJsonAsync<JsonElement>("/api/v1/acl/Confidencial-RRHH", Json, Ct);
        reloaded.GetProperty("inheritParent").GetBoolean().ShouldBeFalse();

        // And the page kept its identity, so its history came with it.
        var versionsAfter = await _admin.GetFromJsonAsync<JsonElement>(
            "/api/v1/versions?path=Confidencial-RRHH/nomina.md", Json, Ct);

        versionsAfter.GetArrayLength().ShouldBeGreaterThanOrEqualTo(versionsBefore.GetArrayLength());
        (await _admin.GetAsync("/api/v1/pages/Reservado/nomina.md", Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// A folder whose name contains a dot is still a folder.
    /// </summary>
    /// <remarks>
    /// Regression test. The evaluator used to decide "is this a file?" by looking for an extension,
    /// so <c>Legal.2026</c> evaluated at its <em>parent</em> and its restriction was skipped
    /// entirely — a privilege escalation reachable by naming a folder after a year.
    /// </remarks>
    [Fact]
    public async Task AFolderWithADotInItsNameIsStillRestricted()
    {
        await _app.WriteFileAsync("Legal.2026/fusion.md", "---\ntitle: Fusión\n---\n\nMuy reservado.\n");
        await ReconcileAsync();

        var acl = await _admin.PutAsJsonAsync("/api/v1/acl/Legal.2026", new
        {
            inheritParent = false,
            entries = Array.Empty<object>(),
        }, Json, Ct);

        acl.EnsureSuccessStatusCode();

        using var outsider = await CreateOutsiderAsync("elena", "Compendio!Test5");

        (await outsider.GetAsync("/api/v1/pages/Legal.2026/fusion.md", Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var tree = await outsider.GetStringAsync("/api/v1/tree", Ct);
        tree.ShouldNotContain("Legal.2026");
    }

    /// <summary>
    /// Criterion 13: delete the index and rebuild it — the same query returns the same results.
    /// </summary>
    [Fact]
    public async Task ReindexingRestoresIdenticalResults()
    {
        await _app.WriteFileAsync("Busqueda/vpn.md",
            "---\ntitle: Configuración de sesión\ntags: [red, vpn]\n---\n\nConecta a 192.168.1.1 desde VPN-Site-A.\n");
        await _app.WriteFileAsync("Busqueda/otra.md",
            "---\ntitle: Otra página\ntags: [red]\n---\n\nServidores y más servidores.\n");

        await ReconcileAsync();
        await WaitForIndexAsync();

        var queries = new[] { "sesion", "192.168.1.1", "VPN-Site-A", "servidor", "tag:red" };

        var before = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var query in queries)
        {
            before[query] = await SearchPathsAsync(query);
        }

        before["sesion"].ShouldContain("Busqueda/vpn.md", "diacritic folding should match sesión");

        var reindex = await _admin.PostAsync("/api/v1/admin/reindex", content: null, Ct);
        reindex.EnsureSuccessStatusCode();

        await WaitForIndexAsync();

        foreach (var query in queries)
        {
            (await SearchPathsAsync(query)).ShouldBe(before[query], $"results for '{query}' changed after a reindex");
        }
    }

    private async Task<string[]> SearchPathsAsync(string query)
    {
        var results = await _admin.GetFromJsonAsync<JsonElement>(
            $"/api/v1/search?q={Uri.EscapeDataString(query)}&pageSize=100", Json, Ct);

        return results.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("path").GetString()!)
            .ToArray();
    }

    private async Task<HttpClient> CreateOutsiderAsync(string userName, string password)
    {
        var create = await _admin.PostAsJsonAsync("/api/v1/admin/users", new
        {
            userName,
            password,
            displayName = userName,
            role = "Editor",
        }, Json, Ct);

        create.EnsureSuccessStatusCode();
        return await _app.SignInAsync(userName, password);
    }

    private async Task ReconcileAsync()
    {
        var response = await _admin.PostAsync("/api/v1/admin/reconcile", content: null, Ct);
        response.EnsureSuccessStatusCode();
    }

    private async Task WaitForIndexAsync()
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            var ready = await _admin.GetFromJsonAsync<JsonElement>("/ready", Json, Ct);

            if (ready.GetProperty("queueDepth").GetInt32() == 0 && ready.GetProperty("index").GetString() == "ready")
            {
                await Task.Delay(200, Ct);
                return;
            }

            await Task.Delay(250, Ct);
        }

        throw new TimeoutException("The search index did not become ready within 20 seconds.");
    }
}
