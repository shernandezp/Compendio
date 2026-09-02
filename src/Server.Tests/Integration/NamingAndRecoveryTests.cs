using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;

namespace Compendio.Tests.Integration;

/// <summary>
/// File naming as the person typed it, and the recovery path the tombstones exist for.
/// </summary>
/// <remarks>
/// Both came out of the same audit. A page titled "Index" was being saved as <c>index.md</c>, and the
/// guide's promise that an administrator can bring a deleted page back had nothing behind it. The
/// tests here pin the behaviour end to end, through the API, on the disk.
/// </remarks>
[Collection(nameof(CompendioCollection))]
public sealed class NamingAndRecoveryTests(CompendioApplication app)
{
    private static JsonSerializerOptions Json => CompendioApplication.Json;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Naming ---------------------------------------------------------------------------------

    [Fact]
    public async Task ThePageFileNameKeepsTheTitleCase()
    {
        var client = await app.SignInAsAdminAsync();

        var created = await CreateAsync(client, "Naming", "Index", "Cuerpo.\n");

        created.Path.ShouldBe("Naming/Index.md");
        OnDiskName("Naming", "Index.md").ShouldBe("Index.md");
    }

    [Fact]
    public async Task TheFolderNameKeepsItsCase()
    {
        var client = await app.SignInAsAdminAsync();

        var response = await client.PostAsJsonAsync("/api/v1/folders", new { parentPath = "Naming", name = "Infrastructure" }, Json, Ct);
        response.EnsureSuccessStatusCode();

        var node = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        node.GetProperty("path").GetString().ShouldBe("Naming/Infrastructure");
        node.GetProperty("name").GetString().ShouldBe("Infrastructure");

        Directory.Exists(Path.Combine(app.ContentRoot, "Naming", "Infrastructure")).ShouldBeTrue();
    }

    /// <summary>
    /// Two titles that differ only by case are one file on Windows and two on Linux. The second gets
    /// a suffix on both, so the folder survives being copied between them.
    /// </summary>
    [Fact]
    public async Task ATitleDifferingOnlyByCaseIsDisambiguated()
    {
        var client = await app.SignInAsAdminAsync();

        var first = await CreateAsync(client, "Naming", "Readme", "Uno.\n");
        var second = await CreateAsync(client, "Naming", "readme", "Dos.\n");

        first.Path.ShouldBe("Naming/Readme.md");
        second.Path.ShouldBe("Naming/readme-2.md");
    }

    /// <summary>
    /// The rename people most obviously want after case started being kept: fixing an old lower-case
    /// name. On a case-insensitive disk the destination "exists", because it is the source.
    /// </summary>
    [Fact]
    public async Task APageCanBeRenamedByCaseAlone()
    {
        var client = await app.SignInAsAdminAsync();
        var created = await CreateAsync(client, "Naming", "guide", "Guía.\n");
        created.Path.ShouldBe("Naming/guide.md");

        var move = await client.PostAsJsonAsync("/api/v1/pages/move", new { path = created.Path, targetPath = "Naming/Guide.md" }, Json, Ct);
        move.StatusCode.ShouldBe(HttpStatusCode.OK, await move.Content.ReadAsStringAsync(Ct));

        OnDiskName("Naming", "Guide.md").ShouldBe("Guide.md");

        var read = await client.GetFromJsonAsync<PageResponse>("/api/v1/pages/Naming/Guide.md", Json, Ct);
        read!.Path.ShouldBe("Naming/Guide.md");
        read.Content!.ShouldContain("Guía.");
    }

    [Fact]
    public async Task AFolderCanBeRenamedByCaseAlone()
    {
        var client = await app.SignInAsAdminAsync();
        var created = await CreateAsync(client, "ops", "Inside", "Dentro.\n");
        created.Path.ShouldBe("ops/Inside.md");

        var move = await client.PostAsJsonAsync("/api/v1/folders/move", new { path = "ops", targetPath = "Ops" }, Json, Ct);
        move.StatusCode.ShouldBe(HttpStatusCode.NoContent, await move.Content.ReadAsStringAsync(Ct));

        OnDiskName(string.Empty, "Ops").ShouldBe("Ops");

        var read = await client.GetAsync("/api/v1/pages/Ops/Inside.md", Ct);
        read.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ---- Templates ------------------------------------------------------------------------------

    /// <summary>
    /// The guide has always offered templates on the new-page screen. The server accepted the id and
    /// ignored it; now it is the starting body when the caller sends none.
    /// </summary>
    [Fact]
    public async Task ANewPageCanStartFromATemplate()
    {
        var client = await app.SignInAsAdminAsync();

        var response = await client.PostAsJsonAsync("/api/v1/pages", new
        {
            folderPath = "Naming",
            title = "Restart the VPN",
            templateId = "runbook",
        }, Json, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));

        var page = await response.Content.ReadFromJsonAsync<PageResponse>(Json, Ct);
        page!.Title.ShouldBe("Restart the VPN");
        page.Content!.ShouldContain("## Rollback");
        page.Content!.ShouldContain("title: Restart the VPN");
    }

    [Fact]
    public async Task ContentTheCallerWroteWinsOverATemplate()
    {
        var client = await app.SignInAsAdminAsync();

        var response = await client.PostAsJsonAsync("/api/v1/pages", new
        {
            folderPath = "Naming",
            title = "Written",
            content = "Mi propio texto.\n",
            templateId = "runbook",
        }, Json, Ct);

        response.EnsureSuccessStatusCode();

        var page = await response.Content.ReadFromJsonAsync<PageResponse>(Json, Ct);
        page!.Content!.ShouldContain("Mi propio texto.");
        page.Content!.ShouldNotContain("## Rollback");
    }

    // ---- Deleted pages --------------------------------------------------------------------------

    [Fact]
    public async Task AnAdministratorCanBringADeletedPageBackWithItsHistory()
    {
        var client = await app.SignInAsAdminAsync();
        var created = await CreateAsync(client, "Recovery", "Recover me", "Primera versión.\n");

        var updated = await client.PutAsJsonAsync($"/api/v1/pages/{created.Path}", new
        {
            content = "---\ntitle: Recover me\n---\n\nSegunda versión.\n",
            expectedHash = created.ContentHash,
        }, Json, Ct);
        updated.EnsureSuccessStatusCode();

        var deleted = await client.DeleteAsync($"/api/v1/pages/{created.Path}", Ct);
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // The instance is shared with every other test in the collection, so other deleted pages are
        // in this list too; only this one is asserted on.
        var listed = await client.GetFromJsonAsync<DeletedPage[]>("/api/v1/admin/deleted-pages", Json, Ct);
        var entry = listed!.Single(d => d.Path == created.Path);
        entry.Title.ShouldBe("Recover me");
        entry.Versions.ShouldBeGreaterThanOrEqualTo(3); // create, save, delete

        var restore = await client.PostAsJsonAsync($"/api/v1/admin/deleted-pages/{entry.PageId}/restore", new { }, Json, Ct);
        restore.StatusCode.ShouldBe(HttpStatusCode.OK, await restore.Content.ReadAsStringAsync(Ct));

        app.FileExists(created.Path).ShouldBeTrue();
        app.ReadFile(created.Path).ShouldContain("Segunda versión.");

        // The whole history came back, with the delete and the restore both on the record.
        var versions = await client.GetFromJsonAsync<VersionSummary[]>($"/api/v1/versions?path={created.Path}", Json, Ct);
        versions!.Length.ShouldBeGreaterThanOrEqualTo(4);
        versions.ShouldContain(v => v.Source == "Delete");
        versions.ShouldContain(v => v.Source == "Restore");
        versions.ShouldContain(v => v.Sequence == 1);

        // And it is no longer deleted.
        var after = await client.GetFromJsonAsync<DeletedPage[]>("/api/v1/admin/deleted-pages", Json, Ct);
        after!.ShouldNotContain(d => d.PageId == entry.PageId);
    }

    /// <summary>
    /// Something else may now live where the page was. The restore says so rather than overwriting,
    /// and takes another path instead.
    /// </summary>
    [Fact]
    public async Task RestoringOverAnOccupiedPathNeedsATarget()
    {
        var client = await app.SignInAsAdminAsync();
        var original = await CreateAsync(client, "Recovery2", "Occupied", "Original.\n");

        (await client.DeleteAsync($"/api/v1/pages/{original.Path}", Ct)).EnsureSuccessStatusCode();

        var replacement = await CreateAsync(client, "Recovery2", "Occupied", "Replacement.\n");
        replacement.Path.ShouldBe(original.Path);

        var listed = await client.GetFromJsonAsync<DeletedPage[]>("/api/v1/admin/deleted-pages", Json, Ct);
        var entry = listed!.Single(d => d.Path == original.Path);

        var refused = await client.PostAsJsonAsync($"/api/v1/admin/deleted-pages/{entry.PageId}/restore", new { }, Json, Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refused.Content.ReadFromJsonAsync<JsonElement>(Json, Ct)).GetProperty("code").GetString().ShouldBe("path.exists");

        var elsewhere = await client.PostAsJsonAsync($"/api/v1/admin/deleted-pages/{entry.PageId}/restore",
            new { targetPath = "Recovery2/Occupied-old.md" }, Json, Ct);
        elsewhere.StatusCode.ShouldBe(HttpStatusCode.OK, await elsewhere.Content.ReadAsStringAsync(Ct));

        app.ReadFile("Recovery2/Occupied-old.md").ShouldContain("Original.");
        app.ReadFile(original.Path).ShouldContain("Replacement.");
    }

    [Fact]
    public async Task OnlyAnAdministratorSeesDeletedPages()
    {
        var admin = await app.SignInAsAdminAsync();

        var create = await admin.PostAsJsonAsync("/api/v1/admin/users", new
        {
            userName = "editor-recovery",
            password = "Editor!Recovery1",
            displayName = "Editor",
            role = "Editor",
        }, Json, Ct);
        create.EnsureSuccessStatusCode();

        var editor = await app.SignInAsync("editor-recovery", "Editor!Recovery1");

        var response = await editor.GetAsync("/api/v1/admin/deleted-pages", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // ---- Translations ---------------------------------------------------------------------------

    /// <summary>
    /// "The source page has changed since this translation was written" belongs on the translation.
    /// It was being computed the other way round, so the banner sat on the source — the one page
    /// whose reader did not need telling.
    /// </summary>
    [Fact]
    public async Task TheStaleFlagLandsOnTheTranslationWhenTheSourceMovesOn()
    {
        var client = await app.SignInAsAdminAsync();

        var source = await CreateAsync(client, "Bilingual", "Remote work",
            "---\ntitle: Remote work\nlang: en\ntranslationKey: remote-work\n---\n\nOriginal.\n");
        var translation = await CreateAsync(client, "Bilingual", "Teletrabajo",
            "---\ntitle: Teletrabajo\nlang: es\ntranslationKey: remote-work\n---\n\nTraducción.\n");

        // Freshly translated: nothing is stale in either direction.
        (await ReadAsync(client, translation.Path)).Translations.ShouldAllBe(t => !t.IsStale);
        (await ReadAsync(client, source.Path)).Translations.ShouldAllBe(t => !t.IsStale);

        var edited = await client.PutAsJsonAsync($"/api/v1/pages/{source.Path}", new
        {
            content = "---\ntitle: Remote work\nlang: en\ntranslationKey: remote-work\n---\n\nOriginal, revised.\n",
            expectedHash = source.ContentHash,
        }, Json, Ct);
        edited.EnsureSuccessStatusCode();

        var stale = await ReadAsync(client, translation.Path);
        stale.Translations.ShouldHaveSingleItem().IsStale.ShouldBeTrue();

        var fresh = await ReadAsync(client, source.Path);
        fresh.Translations.ShouldHaveSingleItem().IsStale.ShouldBeFalse();
    }

    // ---- Attachments ----------------------------------------------------------------------------

    /// <summary>
    /// Attachments have their own size limit, larger than a page's. The store was applying the page
    /// budget to every write and read, so a 5 MB file the upload rules accepted was refused as an
    /// invalid path.
    /// </summary>
    [Fact]
    public async Task AnAttachmentLargerThanThePageBudgetIsAccepted()
    {
        var client = await app.SignInAsAdminAsync();
        var created = await CreateAsync(client, "Large", "Big attachment", "Cuerpo.\n");

        // A PNG signature followed by five megabytes of nothing: the sniffer only reads the header.
        var bytes = new byte[5 * 1024 * 1024];
        Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==")
            .CopyTo(bytes, 0);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(created.Path), "pagePath");
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "grande.png");

        var upload = await client.PostAsync("/api/v1/attachments", form, Ct);
        upload.StatusCode.ShouldBe(HttpStatusCode.OK, await upload.Content.ReadAsStringAsync(Ct));

        var path = (await upload.Content.ReadFromJsonAsync<JsonElement>(Json, Ct)).GetProperty("path").GetString()!;

        var download = await client.GetAsync($"/api/v1/attachments/{path}", Ct);
        download.StatusCode.ShouldBe(HttpStatusCode.OK, await download.Content.ReadAsStringAsync(Ct));
        (await download.Content.ReadAsByteArrayAsync(Ct)).Length.ShouldBe(bytes.Length);
    }

    // ---- Helpers --------------------------------------------------------------------------------

    private async Task<PageResponse> CreateAsync(HttpClient client, string folder, string title, string content)
    {
        var response = await client.PostAsJsonAsync("/api/v1/pages", new { folderPath = folder, title, content }, Json, Ct);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<PageResponse>(Json, Ct))!;
    }

    private async Task<PageResponse> ReadAsync(HttpClient client, string path) =>
        (await client.GetFromJsonAsync<PageResponse>($"/api/v1/pages/{path}", Json, Ct))!;

    /// <summary>The name as the disk spells it, which a case-insensitive <c>File.Exists</c> cannot tell you.</summary>
    private string OnDiskName(string folder, string name)
    {
        var directory = Path.Combine(app.ContentRoot, folder.Replace('/', Path.DirectorySeparatorChar));
        return Directory.EnumerateFileSystemEntries(directory, name).Select(Path.GetFileName).Single()!;
    }

    public sealed record PageResponse(string Path, string Title, string ContentHash, string? Content, TranslationResponse[] Translations);

    public sealed record TranslationResponse(string Path, string Lang, string Title, bool IsStale);

    public sealed record VersionSummary(Guid Id, int Sequence, string Source, string ContentHash);

    public sealed record DeletedPage(Guid PageId, string Path, string Title, DateTimeOffset DeletedAt, DateTimeOffset LastVersionAt, int Versions);
}
