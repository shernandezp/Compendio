using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;

namespace Compendio.Tests.Integration;

/// <summary>
/// The full page lifecycle, asserted against the file system.
/// </summary>
/// <remarks>
/// Every assertion here has a matching claim in the product: the folder is the database of record,
/// a conflicting write returns both versions rather than overwriting, and a page nobody may read
/// looks exactly like a page that does not exist.
/// </remarks>
[Collection(nameof(CompendioCollection))]
public sealed class PageLifecycleTests(CompendioApplication app)
{
    private static JsonSerializerOptions Json => CompendioApplication.Json;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CreatingAPageWritesARealMarkdownFile()
    {
        var client = await app.SignInAsAdminAsync();

        var response = await client.PostAsJsonAsync("/api/v1/pages", new
        {
            folderPath = "IT",
            title = "Política de teletrabajo",
            content = "---\ntitle: Política de teletrabajo\nlang: es\n---\n\n# Política\n\nContenido.\n",
        }, Json, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var page = await response.Content.ReadFromJsonAsync<PageResponse>(Json, Ct);

        // The accented title lives in front matter; the file name is ASCII-slugified.
        page!.Path.ShouldBe("IT/politica-de-teletrabajo.md");
        page.Title.ShouldBe("Política de teletrabajo");

        app.FileExists("IT/politica-de-teletrabajo.md").ShouldBeTrue();
        app.ReadFile("IT/politica-de-teletrabajo.md").ShouldContain("Contenido.");
    }

    /// <summary>
    /// The title reaches the file, even when the caller sends a body with no front matter.
    /// </summary>
    /// <remarks>
    /// The editor sends canonical body text and the title as a separate field — it has no YAML
    /// emitter and should not need one. If the server does not compose the front matter, the file
    /// has no <c>title:</c> at all and the title is reconstructed from the ASCII slug on the next
    /// read: "Política de teletrabajo" comes back as "Politica De Teletrabajo". The accented title
    /// living in front matter is the whole reason the file name is allowed to be a slug.
    /// </remarks>
    [Fact]
    public async Task ATitleWithNoFrontMatterStillLandsInTheFile()
    {
        var client = await app.SignInAsAdminAsync();

        var created = await CreateAsync(client, "IT", "Configuración de sesión", "Cuerpo sin portada.\n");

        created.Title.ShouldBe("Configuración de sesión");
        created.Path.ShouldBe("IT/configuracion-de-sesion.md");

        var file = app.ReadFile(created.Path);
        file.ShouldContain("title: Configuración de sesión");
        file.ShouldContain("Cuerpo sin portada.");

        // And it survives a read back, which is where the slug would otherwise take over.
        var reread = await client.GetFromJsonAsync<PageResponse>($"/api/v1/pages/{created.Path}", Json, Ct);
        reread!.Title.ShouldBe("Configuración de sesión");
    }

    /// <summary>
    /// A title that slugifies into a name the file system will not take is nudged, not refused.
    /// </summary>
    /// <remarks>
    /// <c>PathPolicy</c> rejects Windows device names and <c>..</c> in a segment — on Linux too, so
    /// content stays portable. A user who titles a page "Con" or "Versión 1..2" has done nothing
    /// wrong and cannot act on <c>path.invalid</c>.
    /// </remarks>
    [Theory]
    [InlineData("Con", "IT/_con.md")]
    [InlineData("Nul", "IT/_nul.md")]
    [InlineData("Versión 1..2", "IT/version-1.2.md")]
    public async Task ATitleThatWouldSlugifyIntoAnUnusableNameIsStillCreated(string title, string expectedPath)
    {
        var client = await app.SignInAsAdminAsync();

        var created = await CreateAsync(client, "IT", title, "Cuerpo.\n");

        created.Path.ShouldBe(expectedPath);
        created.Title.ShouldBe(title);
        app.FileExists(expectedPath).ShouldBeTrue();
    }

    /// <summary>
    /// Deleting a folder that has folders in it.
    /// </summary>
    /// <remarks>
    /// <c>Folders.ParentId</c> is <c>ON DELETE RESTRICT</c> and SQLite enforces that per row inside a
    /// single statement, so removing the rows in one <c>DELETE</c> fails as soon as a parent is
    /// reached before its child. Nothing else in the suite deletes a nested folder, so the whole
    /// operation was one query away from being permanently broken.
    /// </remarks>
    [Fact]
    public async Task DeletingANestedFolderRemovesTheWholeSubtree()
    {
        var client = await app.SignInAsAdminAsync();

        await CreateAsync(client, "Doomed/inner/deeper", "Deep page", "Adiós.\n");
        await CreateAsync(client, "Doomed", "Shallow page", "Adiós también.\n");

        var response = await client.DeleteAsync("/api/v1/folders/Doomed", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        Directory.Exists(Path.Combine(app.ContentRoot, "Doomed")).ShouldBeFalse();

        var tree = await client.GetStringAsync("/api/v1/tree", Ct);
        tree.ShouldNotContain("Doomed");
    }

    /// <summary>
    /// <c>assets/</c> holds a page's images. It is not a place in the wiki, so it is not a node.
    /// </summary>
    [Fact]
    public async Task TheAssetsFolderIsNotANodeInTheTree()
    {
        var client = await app.SignInAsAdminAsync();
        var created = await CreateAsync(client, "Adjuntos", "With an image", "Cuerpo.\n");

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(created.Path), "pagePath");

        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        var image = new ByteArrayContent(png);
        image.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(image, "file", "captura.png");

        var upload = await client.PostAsync("/api/v1/attachments", form, Ct);
        upload.StatusCode.ShouldBe(HttpStatusCode.OK, await upload.Content.ReadAsStringAsync(Ct));

        Directory.Exists(Path.Combine(app.ContentRoot, "Adjuntos", "assets")).ShouldBeTrue();

        var tree = await client.GetStringAsync("/api/v1/tree", Ct);
        tree.ShouldNotContain("assets");
    }

    [Fact]
    public async Task SavingRequiresTheHashTheCallerRead()
    {
        var client = await app.SignInAsAdminAsync();

        var created = await CreateAsync(client, "IT", "Conflict demo", "Original.\n");

        // Somebody else saves first.
        var first = await client.PutAsJsonAsync($"/api/v1/pages/{created.Path}", new
        {
            content = "Their version.\n",
            expectedHash = created.ContentHash,
        }, Json, Ct);

        first.EnsureSuccessStatusCode();

        // We save against the hash we read before they did.
        var second = await client.PutAsJsonAsync($"/api/v1/pages/{created.Path}", new
        {
            content = "My version.\n",
            expectedHash = created.ContentHash,
        }, Json, Ct);

        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var problem = await second.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        problem.GetProperty("code").GetString().ShouldBe("page.conflict");

        // Both versions travel with the error, because the client turns this into a merge.
        problem.GetProperty("currentContent").GetString().ShouldBe("Their version.\n");
        problem.GetProperty("expectedHash").GetString().ShouldBe(created.ContentHash);

        // And nothing was overwritten.
        app.ReadFile(created.Path).ShouldBe("Their version.\n");
    }

    [Fact]
    public async Task DeletingRemovesTheFile()
    {
        var client = await app.SignInAsAdminAsync();
        var created = await CreateAsync(client, "IT", "Delete me", "Body.\n");

        var response = await client.DeleteAsync($"/api/v1/pages/{created.Path}", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        app.FileExists(created.Path).ShouldBeFalse();

        var read = await client.GetAsync($"/api/v1/pages/{created.Path}", Ct);
        read.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task MovingAPageKeepsItsHistory()
    {
        var client = await app.SignInAsAdminAsync();
        var created = await CreateAsync(client, "IT", "Move me", "Version one.\n");

        var updated = await client.PutAsJsonAsync($"/api/v1/pages/{created.Path}", new
        {
            content = "Version two.\n",
            expectedHash = created.ContentHash,
        }, Json, Ct);

        updated.EnsureSuccessStatusCode();

        var target = "Archive/moved.md";
        var move = await client.PostAsJsonAsync("/api/v1/pages/move", new { path = created.Path, targetPath = target }, Json, Ct);
        move.EnsureSuccessStatusCode();

        app.FileExists(created.Path).ShouldBeFalse();
        app.FileExists(target).ShouldBeTrue();

        // History travels with the page's identity, not with its path.
        var versions = await client.GetFromJsonAsync<VersionSummary[]>($"/api/v1/versions?path={target}", Json, Ct);
        versions!.Length.ShouldBeGreaterThanOrEqualTo(3);
        versions.ShouldContain(v => v.Source == "Move");
    }

    [Fact]
    public async Task RestoringWritesANewVersionRatherThanRewinding()
    {
        var client = await app.SignInAsAdminAsync();
        var created = await CreateAsync(client, "IT", "Restore me", "First.\n");

        var second = await client.PutAsJsonAsync($"/api/v1/pages/{created.Path}", new
        {
            content = "Second.\n",
            expectedHash = created.ContentHash,
        }, Json, Ct);

        second.EnsureSuccessStatusCode();

        var versions = await client.GetFromJsonAsync<VersionSummary[]>($"/api/v1/versions?path={created.Path}", Json, Ct);
        var first = versions!.Single(v => v.Sequence == 1);

        var restore = await client.PostAsJsonAsync($"/api/v1/versions/{first.Id}/restore", new { path = created.Path }, Json, Ct);
        restore.EnsureSuccessStatusCode();

        // The file matches the old version byte for byte — front matter included, which is what
        // makes this a restore rather than a re-creation.
        var original = await client.GetFromJsonAsync<JsonElement>($"/api/v1/versions/{first.Id}", Json, Ct);
        app.ReadFile(created.Path).ShouldBe(original.GetProperty("content").GetString());
        app.ReadFile(created.Path).ShouldContain("First.");

        // …and the restore is itself a version, so a mistaken restore is undoable.
        var after = await client.GetFromJsonAsync<VersionSummary[]>($"/api/v1/versions?path={created.Path}", Json, Ct);
        after!.Length.ShouldBeGreaterThan(versions!.Length);
    }

    [Fact]
    public async Task TickingACheckboxChangesTwoCharacters()
    {
        var client = await app.SignInAsAdminAsync();

        const string body = "# Runbook\n\n- [ ] Check the certificate\n- [ ] Restart the service\n";
        var created = await CreateAsync(client, "IT", "Runbook checkbox", body);

        var content = app.ReadFile(created.Path);
        var offset = System.Text.Encoding.UTF8.GetByteCount(content[..content.IndexOf("[ ]", StringComparison.Ordinal)]);

        var response = await client.PostAsJsonAsync("/api/v1/pages/checkbox", new
        {
            path = created.Path,
            offset,
            @checked = true,
            expectedHash = created.ContentHash,
        }, Json, Ct);

        response.EnsureSuccessStatusCode();

        var updated = app.ReadFile(created.Path);
        updated.ShouldContain("- [x] Check the certificate");

        // The second item is untouched: this is a substitution, not a re-serialization.
        updated.ShouldContain("- [ ] Restart the service");
        updated.Length.ShouldBe(content.Length);
    }

    [Fact]
    public async Task RejectsPathsThatWouldEscapeTheContentFolder()
    {
        var client = await app.SignInAsAdminAsync();

        var response = await client.GetAsync("/api/v1/pages/..%2F..%2Fsecrets.md", Ct);

        response.StatusCode.ShouldBeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.NotFound);

        // Whatever the status, nothing outside the content folder was read.
        Directory.GetFiles(app.ContentRoot, "secrets.md", SearchOption.AllDirectories).ShouldBeEmpty();
    }

    /// <summary>
    /// The Data Protection key ring must land in <c>&lt;data&gt;/keys</c>.
    /// </summary>
    /// <remarks>
    /// The failure mode this guards is silent and severe: a service account with no home directory
    /// makes ASP.NET Core fall back to an in-memory ring, so every restart logs every user out —
    /// and it only ever appears in the deployed configurations, never in development.
    /// </remarks>
    [Fact]
    public async Task DataProtectionKeysPersistToTheDataDirectory()
    {
        _ = await app.SignInAsAdminAsync();

        var keyRing = Path.Combine(app.KeysRoot, "dataprotection");

        Directory.Exists(keyRing).ShouldBeTrue($"'{keyRing}' should hold the Data Protection key ring");
        Directory.GetFiles(keyRing, "key-*.xml").ShouldNotBeEmpty();
    }

    [Fact]
    public async Task UnauthenticatedCallersGetA401RatherThanALoginRedirect()
    {
        using var anonymous = app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await anonymous.GetAsync("/api/v1/tree", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ErrorsComeBackInTheCallersLanguage()
    {
        var client = await app.SignInAsAdminAsync();

        var spanish = await client.GetAsync("/api/v1/pages/missing.md?lang=es", Ct);
        var spanishBody = await spanish.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);

        var english = await client.GetAsync("/api/v1/pages/missing.md?lang=en", Ct);
        var englishBody = await english.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);

        // The code is stable across languages; only the prose changes.
        spanishBody.GetProperty("code").GetString().ShouldBe("page.not_found");
        englishBody.GetProperty("code").GetString().ShouldBe("page.not_found");

        spanishBody.GetProperty("title").GetString().ShouldBe("Página no encontrada");
        englishBody.GetProperty("title").GetString().ShouldBe("Page not found");
    }

    [Fact]
    public async Task SecurityHeadersAreSetAndTheCspForbidsInlineScript()
    {
        using var client = app.CreateClient();
        var response = await client.GetAsync("/health", Ct);

        var csp = response.Headers.GetValues("Content-Security-Policy").Single();

        csp.ShouldContain("script-src 'self'");
        csp.ShouldContain("frame-ancestors 'none'");
        csp.ShouldContain("object-src 'none'");

        // Mermaid injects styles, so style-src carries a per-response nonce rather than
        // 'unsafe-inline'.
        csp.ShouldContain("style-src 'self' 'nonce-");
        csp.ShouldContain("style-src-elem 'self' 'nonce-");

        // No directive that can introduce executable code or a stylesheet allows inline content.
        // `style-src-attr` is the single exception and is named explicitly: a nonce cannot be
        // carried by a `style="…"` attribute, which is how Mantine sets its CSS variables, and
        // page content reaches the browser with its style attributes already stripped by the
        // sanitizer. Everything else must stay strict.
        foreach (var directive in csp.Split(';', StringSplitOptions.TrimEntries))
        {
            if (directive.StartsWith("style-src-attr", StringComparison.Ordinal))
            {
                continue;
            }

            directive.ShouldNotContain("unsafe-inline", Case.Sensitive, directive);
            directive.ShouldNotContain("unsafe-eval", Case.Sensitive, directive);
        }

        response.Headers.GetValues("X-Content-Type-Options").Single().ShouldBe("nosniff");
    }

    /// <summary>
    /// The nonce reaches the page it is for.
    /// </summary>
    /// <remarks>
    /// A nonce that is generated and never delivered is the same as no nonce at all, and the
    /// failure is silent: the header looks correct, and the browser blocks the theme paint,
    /// Mantine's variables and every Mermaid diagram. The shell therefore carries the same value
    /// the header does, and never a stale one from a cache.
    /// </remarks>
    [Fact]
    public async Task TheSpaShellCarriesTheSameNonceAsTheHeader()
    {
        using var client = app.CreateClient();
        var response = await client.GetAsync("/", Ct);

        response.EnsureSuccessStatusCode();

        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        var nonce = csp.Split("'nonce-")[1].Split('\'')[0];

        nonce.ShouldNotBeNullOrWhiteSpace();

        var html = await response.Content.ReadAsStringAsync(Ct);

        html.ShouldContain($"content=\"{nonce}\"");
        html.ShouldContain($"<style nonce=\"{nonce}\">");
        html.ShouldNotContain("__CSP_NONCE__");

        response.Headers.CacheControl!.NoStore.ShouldBeTrue();

        // And a second request gets a different one, or it is not a nonce.
        using var again = await client.GetAsync("/", Ct);
        again.Headers.GetValues("Content-Security-Policy").Single().ShouldNotContain($"'nonce-{nonce}'");
    }

    /// <summary>
    /// Changing a title is a front-matter edit, not a move: the <c>title:</c> key changes and the
    /// file name, path and body do not — so every link and bookmark to the page keeps working.
    /// </summary>
    [Fact]
    public async Task ChangingATitleRewritesFrontMatterButKeepsTheFileAndBody()
    {
        var client = await app.SignInAsAdminAsync();

        var page = await CreateAsync(client, "Docs", "Old title",
            "---\ntitle: Old title\n---\n\n# Heading\n\nThe body stays.\n");

        var response = await client.PostAsJsonAsync("/api/v1/pages/title",
            new { path = page.Path, title = "Brand new title" }, Json, Ct);

        response.EnsureSuccessStatusCode();
        var updated = (await response.Content.ReadFromJsonAsync<PageResponse>(Json, Ct))!;

        // The title changed, but the file name / path did not.
        updated.Title.ShouldBe("Brand new title");
        updated.Path.ShouldBe(page.Path);

        var after = app.ReadFile(page.Path);
        after.ShouldContain("Brand new title");
        after.ShouldNotContain("Old title");
        // The body is left intact — only the front matter block was rewritten.
        after.ShouldContain("# Heading");
        after.ShouldContain("The body stays.");

        // A fresh read agrees, and the file still exists at the original slug.
        app.FileExists(page.Path).ShouldBeTrue();
        var reread = await client.GetFromJsonAsync<PageResponse>($"/api/v1/pages/{page.Path}", Json, Ct);
        reread!.Title.ShouldBe("Brand new title");
    }

    [Fact]
    public async Task AnEmptyTitleIsRejectedAndTheOldOneKept()
    {
        var client = await app.SignInAsAdminAsync();
        var page = await CreateAsync(client, "Docs", "Keep me", "---\ntitle: Keep me\n---\n\nBody.\n");

        var response = await client.PostAsJsonAsync("/api/v1/pages/title",
            new { path = page.Path, title = "   " }, Json, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        body.GetProperty("code").GetString().ShouldBe("validation.failed");

        var reread = await client.GetFromJsonAsync<PageResponse>($"/api/v1/pages/{page.Path}", Json, Ct);
        reread!.Title.ShouldBe("Keep me");
    }

    private async Task<PageResponse> CreateAsync(HttpClient client, string folder, string title, string content)
    {
        var response = await client.PostAsJsonAsync("/api/v1/pages", new { folderPath = folder, title, content }, Json, Ct);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<PageResponse>(Json, Ct))!;
    }

    public sealed record PageResponse(string Path, string Title, string ContentHash, string? Content, string? Html, string Level);

    public sealed record VersionSummary(Guid Id, int Sequence, string Source, string ContentHash);
}
