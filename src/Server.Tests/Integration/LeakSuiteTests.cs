using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Compendio.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Compendio.Tests.Integration;

/// <summary>
/// One row per read surface: a restricted page is invisible to an unauthorized user and visible to
/// an authorized one.
/// </summary>
/// <remarks>
/// <para>
/// Every surface here is a search index in disguise, and every one of them has leaked in some
/// shipped wiki: a title in a search result, a count that is one too high, an autocomplete
/// suggestion, a backlink from a page you cannot open. Adding a surface to the product means adding
/// a row to this class.
/// </para>
/// <para>
/// The second half of each assertion matters as much as the first. A suite that only checks
/// invisibility passes trivially if the feature is broken for everyone.
/// </para>
/// </remarks>
[Collection(nameof(CompendioCollection))]
public sealed class LeakSuiteTests(CompendioApplication app) : IAsyncLifetime
{
    private const string RestrictedFolder = "Legal";
    private const string RestrictedPage = "Legal/acquisition-northwind.md";
    private const string PublicPage = "Public/announcement.md";
    private const string Secret = "Northwind";

    private static JsonSerializerOptions Json => CompendioApplication.Json;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient _admin = null!;
    private HttpClient _outsider = null!;

    public async ValueTask InitializeAsync()
    {
        _admin = await app.SignInAsAdminAsync();

        // Somebody who is not on the list. An Editor, so that anything they cannot see is the ACL's
        // doing rather than their role's.
        var create = await _admin.PostAsJsonAsync("/api/v1/admin/users", new
        {
            userName = "bruno",
            password = "Compendio!Test2",
            displayName = "Bruno Díaz",
            role = "Editor",
        }, Json, Ct);

        if (create.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.BadRequest))
        {
            create.EnsureSuccessStatusCode();
        }

        await EnsurePageAsync(RestrictedFolder, "Acquisition Northwind",
            $"---\ntitle: Acquisition Northwind\ntags: [legal, confidential]\n---\n\n# {Secret}\n\nDue diligence for {Secret}.\n",
            RestrictedPage);

        await EnsurePageAsync("Public", "Announcement",
            $"---\ntitle: Announcement\ntags: [legal]\n---\n\nSee [[Acquisition Northwind]] for details about {Secret}.\n",
            PublicPage);

        // Restrict the folder: inheritance cut, and only the administrator listed.
        var acl = await _admin.PutAsJsonAsync($"/api/v1/acl/{RestrictedFolder}", new
        {
            inheritParent = false,
            entries = Array.Empty<object>(),
        }, Json, Ct);

        acl.EnsureSuccessStatusCode();

        _outsider = await app.SignInAsync("bruno", "Compendio!Test2");

        // Let the indexer drain: the leak assertions are about what search returns, so they have to
        // run against an index that has actually seen the pages.
        await WaitForIndexAsync();
    }

    public ValueTask DisposeAsync()
    {
        _admin.Dispose();
        _outsider.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Tree()
    {
        var outsiderTree = await _outsider.GetStringAsync("/api/v1/tree", Ct);
        var adminTree = await _admin.GetStringAsync("/api/v1/tree", Ct);

        // Absent, not greyed out: the folder name itself is the information.
        outsiderTree.ShouldNotContain(RestrictedFolder);
        outsiderTree.ShouldNotContain(Secret);
        adminTree.ShouldContain(RestrictedFolder);
    }

    [Fact]
    public async Task PageRead()
    {
        var outsider = await _outsider.GetAsync($"/api/v1/pages/{RestrictedPage}", Ct);
        var admin = await _admin.GetAsync($"/api/v1/pages/{RestrictedPage}", Ct);

        // 404, never 403 — a 403 confirms the page exists.
        outsider.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        admin.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Search()
    {
        var outsider = await _outsider.GetFromJsonAsync<Paged<Hit>>($"/api/v1/search?q={Secret}", Json, Ct);
        var admin = await _admin.GetFromJsonAsync<Paged<Hit>>($"/api/v1/search?q={Secret}", Json, Ct);

        outsider!.Items.ShouldNotContain(h => h.Path == RestrictedPage);
        admin!.Items.ShouldContain(h => h.Path == RestrictedPage);
    }

    /// <summary>
    /// Totals use the same predicate, so "12 results" means twelve results <em>you</em> can see. A
    /// count computed without it leaks the existence of everything it counted.
    /// </summary>
    [Fact]
    public async Task SearchTotals()
    {
        var outsider = await _outsider.GetFromJsonAsync<Paged<Hit>>($"/api/v1/search?q={Secret}", Json, Ct);
        var admin = await _admin.GetFromJsonAsync<Paged<Hit>>($"/api/v1/search?q={Secret}", Json, Ct);

        outsider!.TotalCount.ShouldBe(outsider.Items.Count);
        admin!.TotalCount.ShouldBeGreaterThan(outsider.TotalCount);
    }

    [Fact]
    public async Task SearchSnippets()
    {
        var outsider = await _outsider.GetFromJsonAsync<Paged<Hit>>($"/api/v1/search?q=diligence", Json, Ct);

        // Not just the path: a snippet is page content, and a snippet from a page you cannot read
        // is the content leaking without the path.
        outsider!.Items.ShouldAllBe(h => !h.Excerpt.Contains(Secret));
    }

    [Fact]
    public async Task QuickSwitcher()
    {
        var outsider = await _outsider.GetFromJsonAsync<Hit[]>($"/api/v1/search/suggest?q=acquisition", Json, Ct);
        var admin = await _admin.GetFromJsonAsync<Hit[]>($"/api/v1/search/suggest?q=acquisition", Json, Ct);

        outsider!.ShouldNotContain(h => h.Path == RestrictedPage);
        admin!.ShouldContain(h => h.Path == RestrictedPage);
    }

    /// <summary>Otherwise the editor becomes a page-name oracle.</summary>
    [Fact]
    public async Task LinkAutocomplete()
    {
        var outsider = await _outsider.GetFromJsonAsync<Hit[]>($"/api/v1/links/suggest?q=acquisition", Json, Ct);
        var admin = await _admin.GetFromJsonAsync<Hit[]>($"/api/v1/links/suggest?q=acquisition", Json, Ct);

        outsider!.ShouldNotContain(h => h.Path == RestrictedPage);
        admin!.ShouldContain(h => h.Path == RestrictedPage);
    }

    [Fact]
    public async Task Backlinks()
    {
        // The public page links to the restricted one. The admin sees the relationship; the
        // outsider must not learn the target exists.
        var outsiderTarget = await _outsider.GetAsync($"/api/v1/pages/backlinks?path={RestrictedPage}", Ct);
        outsiderTarget.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var adminTarget = await _admin.GetAsync($"/api/v1/pages/backlinks?path={RestrictedPage}", Ct);
        adminTarget.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TagCounts()
    {
        var outsider = await _outsider.GetFromJsonAsync<TagCount[]>("/api/v1/tags", Json, Ct);
        var admin = await _admin.GetFromJsonAsync<TagCount[]>("/api/v1/tags", Json, Ct);

        // "confidential" is only on the restricted page.
        outsider!.ShouldNotContain(t => t.Tag == "confidential");
        admin!.ShouldContain(t => t.Tag == "confidential");

        // And the shared tag's count differs, because counts are per user rather than global.
        var outsiderLegal = outsider!.SingleOrDefault(t => t.Tag == "legal")?.Count ?? 0;
        var adminLegal = admin!.Single(t => t.Tag == "legal").Count;

        adminLegal.ShouldBeGreaterThan(outsiderLegal);
    }

    [Fact]
    public async Task RecentlyUpdated()
    {
        var outsider = await _outsider.GetFromJsonAsync<Hit[]>("/api/v1/recent?limit=50", Json, Ct);
        var admin = await _admin.GetFromJsonAsync<Hit[]>("/api/v1/recent?limit=50", Json, Ct);

        outsider!.ShouldNotContain(h => h.Path == RestrictedPage);
        admin!.ShouldContain(h => h.Path == RestrictedPage);
    }

    /// <summary>
    /// A rendered page must not become a link oracle either: the public page links to the
    /// restricted one by name, and for the outsider that link has to render as unresolved.
    /// </summary>
    [Fact]
    public async Task RenderedWikiLinks()
    {
        var outsider = await _outsider.GetFromJsonAsync<JsonElement>($"/api/v1/pages/{PublicPage}", Json, Ct);
        var admin = await _admin.GetFromJsonAsync<JsonElement>($"/api/v1/pages/{PublicPage}", Json, Ct);

        var outsiderHtml = outsider.GetProperty("html").GetString()!;
        var adminHtml = admin.GetProperty("html").GetString()!;

        outsiderHtml.ShouldNotContain(RestrictedPage);
        adminHtml.ShouldContain("acquisition-northwind");
    }

    /// <summary>
    /// History is a read surface too, and it is authorized by a different key than it reads.
    /// </summary>
    /// <remarks>
    /// Every history endpoint takes a <em>path</em>, which is what the permission check sees, and a
    /// <em>version id</em>, which is what it acts on. Nothing in the request ties the two together,
    /// so a caller can name a page they can read and version ids belonging to one they cannot — and
    /// the diff hands back its content, in full, rendered.
    /// </remarks>
    [Fact]
    public async Task DiffCannotReadAnotherPagesVersions()
    {
        var restricted = await _admin.GetFromJsonAsync<Version[]>(
            $"/api/v1/versions?path={RestrictedPage}", Json, Ct);

        restricted!.Length.ShouldBeGreaterThan(0, "the restricted page needs history to steal");

        // The outsider can read the public page and holds a version id from the restricted one.
        var response = await _outsider.GetAsync(
            $"/api/v1/diff?path={PublicPage}&from={restricted[0].Id}&to={restricted[0].Id}", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldNotContain(Secret);
    }

    /// <summary>
    /// The same key confusion, with a write on the end of it.
    /// </summary>
    /// <remarks>
    /// Restoring copies a version's content into a page. Unbound, it copies the content of a page
    /// the caller cannot read into one they can — the permission check having asked only "may you
    /// write here", which they may.
    /// </remarks>
    [Fact]
    public async Task RestoreCannotCopyAnotherPagesVersion()
    {
        // A page the outsider genuinely may write, so the write check passes and the only thing
        // standing between them and the restricted content is the version-to-page binding.
        const string ownPage = "Sandbox/bruno-notes.md";
        await EnsurePageAsync("Sandbox", "Bruno notes", "---\ntitle: Bruno notes\n---\n\nMine.\n", ownPage);

        var users = await _admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/users", Json, Ct);
        var bruno = users.EnumerateArray().Single(u => u.GetProperty("userName").GetString() == "bruno");

        var grant = await _admin.PutAsJsonAsync("/api/v1/acl/Sandbox", new
        {
            inheritParent = true,
            entries = new[]
            {
                new { subjectType = "User", subjectId = bruno.GetProperty("id").GetString(), level = "Write" },
            },
        }, Json, Ct);

        grant.EnsureSuccessStatusCode();

        var restricted = await _admin.GetFromJsonAsync<Version[]>(
            $"/api/v1/versions?path={RestrictedPage}", Json, Ct);

        var before = app.ReadFile(ownPage);

        var response = await _outsider.PostAsJsonAsync(
            $"/api/v1/versions/{restricted![0].Id}/restore", new { path = ownPage }, Json, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // And nothing was written, which is the half that would have been permanent.
        app.ReadFile(ownPage).ShouldBe(before);
        app.ReadFile(ownPage).ShouldNotContain("Due diligence");
    }

    [Fact]
    public async Task WritingIntoARestrictedFolderIsRefused()
    {
        var response = await _outsider.PostAsJsonAsync("/api/v1/pages", new
        {
            folderPath = RestrictedFolder,
            title = "Sneaky",
            content = "x\n",
        }, Json, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The stale report is a list of paths and titles, so it leaks like any other list.
    /// </summary>
    /// <remarks>
    /// The total matters as much as the rows: a count computed without the predicate tells an
    /// outsider how many pages exist that they cannot see, which is the same leak in one number.
    /// </remarks>
    [Fact]
    public async Task StaleReport()
    {
        await MakeRestrictedPageStaleAsync();

        var outsider = await _outsider.GetFromJsonAsync<Paged<StaleRow>>(
            "/api/v1/lifecycle/stale?pageSize=100", Json, Ct);

        var admin = await _admin.GetFromJsonAsync<Paged<StaleRow>>(
            "/api/v1/lifecycle/stale?pageSize=100", Json, Ct);

        outsider!.Items.ShouldNotContain(r => r.Path == RestrictedPage);
        outsider.Items.ShouldAllBe(r => !r.Title.Contains(Secret));
        outsider.TotalCount.ShouldBe(outsider.Items.Count);

        admin!.Items.ShouldContain(r => r.Path == RestrictedPage);
    }

    /// <summary>The CSV export runs the same query, so widening it there would be a leak with a filename.</summary>
    [Fact]
    public async Task StaleReportCsv()
    {
        await MakeRestrictedPageStaleAsync();

        var outsider = await _outsider.GetStringAsync("/api/v1/lifecycle/stale.csv", Ct);
        var admin = await _admin.GetStringAsync("/api/v1/lifecycle/stale.csv", Ct);

        outsider.ShouldNotContain(RestrictedPage);
        outsider.ShouldNotContain(Secret);
        admin.ShouldContain(RestrictedPage);
    }

    /// <summary>The dashboard is a stale report with a different name on it.</summary>
    [Fact]
    public async Task Dashboard()
    {
        await MakeRestrictedPageStaleAsync();

        var outsider = await _outsider.GetStringAsync("/api/v1/dashboard", Ct);

        outsider.ShouldNotContain(RestrictedPage);
        outsider.ShouldNotContain(Secret);
    }

    /// <summary>
    /// An acknowledgment report is a list of who has and has not read a page — different information
    /// from the page, and gated by <c>manage</c> rather than <c>read</c>.
    /// </summary>
    [Fact]
    public async Task AcknowledgmentReport()
    {
        var outsider = await _outsider.GetAsync(
            $"/api/v1/acknowledgments/page?path={Uri.EscapeDataString(RestrictedPage)}", Ct);

        outsider.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Not asserted against the secret here: the 404's detail echoes the path the caller asked
        // for, and a path the caller supplied is not something they learned. What must be absent is
        // the report itself — who was required, and who has read it.
        var body = await outsider.Content.ReadAsStringAsync(Ct);
        body.ShouldNotContain("\"people\"");
        body.ShouldNotContain("requiredCount");

        var admin = await _admin.GetAsync(
            $"/api/v1/acknowledgments/page?path={Uri.EscapeDataString(RestrictedPage)}", Ct);

        admin.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>Asking what somebody else owes is a management question, and needs the Admin role.</summary>
    [Fact]
    public async Task AnotherPersonsOutstandingAcknowledgments()
    {
        var users = await _admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/users", Json, Ct);
        var adminId = users.EnumerateArray().First(u => u.GetProperty("userName").GetString() == "admin")
            .GetProperty("id").GetString();

        var outsider = await _outsider.GetAsync($"/api/v1/acknowledgments/user/{adminId}", Ct);
        outsider.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var admin = await _admin.GetAsync($"/api/v1/acknowledgments/user/{adminId}", Ct);
        admin.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// A notification is written when something happens and read later, so access can change in
    /// between — and the row still names a path.
    /// </summary>
    /// <remarks>
    /// Set up the hard way round on purpose: the restricted page is given an owner who cannot read
    /// it, and the review scan writes them a notification about it. Filtering only at write time
    /// would leave that row in their inbox as a way to learn the page exists. The list and the count
    /// are asserted separately, because a badge that counts rows the list then drops is the same
    /// leak in one number.
    /// </remarks>
    [Fact]
    public async Task NotificationsAndTheirCount()
    {
        var users = await _admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/users", Json, Ct);
        var bruno = users.EnumerateArray().Single(u => u.GetProperty("userName").GetString() == "bruno");

        // Owned by somebody the folder's rules shut out, and overdue, so the scan notifies them.
        var lifecycle = await _admin.PutAsJsonAsync("/api/v1/pages/lifecycle", new
        {
            path = RestrictedPage,
            owner = "bruno",
            reviewIntervalDays = 30,
            nextReviewDate = DateTimeOffset.UtcNow.AddDays(-120),
        }, Json, Ct);

        lifecycle.EnsureSuccessStatusCode();

        using (var scope = app.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<Compendio.Engine.ReviewScan>().RunAsync(Ct);
        }

        // The row exists and is addressed to Bruno — otherwise this test proves nothing.
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ICompendioDbContext>();
            var brunoId = bruno.GetProperty("id").GetGuid();

            db.Notifications
                .Any(n => n.UserId == brunoId && n.TargetPath == RestrictedPage)
                .ShouldBeTrue("the scan must have written the notification this test is about");
        }

        var list = await _outsider.GetStringAsync("/api/v1/notifications?pageSize=100", Ct);
        list.ShouldNotContain(RestrictedPage);
        list.ShouldNotContain(Secret);

        var count = await _outsider.GetFromJsonAsync<JsonElement>("/api/v1/notifications/count", Json, Ct);
        count.GetProperty("count").GetInt32().ShouldBe(0);

        // And the dashboard, which reads the same rows through the same filter.
        var dashboard = await _outsider.GetStringAsync("/api/v1/dashboard", Ct);
        dashboard.ShouldNotContain(RestrictedPage);
    }

    /// <summary>
    /// Gives the restricted page a review date in the past, so it appears on the lifecycle surfaces
    /// for anyone entitled to see it — and, if the predicate is missing, for everyone else too.
    /// </summary>
    private async Task MakeRestrictedPageStaleAsync()
    {
        var response = await _admin.PutAsJsonAsync("/api/v1/pages/lifecycle", new
        {
            path = RestrictedPage,
            owner = "admin",
            reviewIntervalDays = 30,
            nextReviewDate = DateTimeOffset.UtcNow.AddDays(-90),
        }, Json, Ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Setting lifecycle returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}\n" +
                string.Join("\n", app.Errors));
        }
    }

    private async Task EnsurePageAsync(string folder, string title, string content, string expectedPath)
    {
        if (app.FileExists(expectedPath))
        {
            return;
        }

        var response = await _admin.PostAsJsonAsync("/api/v1/pages", new { folderPath = folder, title, content }, Json, Ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Waits for the durable index queue to drain. The queue is the seam that makes this
    /// deterministic — without it a test would have to sleep and hope.
    /// </summary>
    private async Task WaitForIndexAsync()
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var ready = await _admin.GetFromJsonAsync<JsonElement>("/ready", Json, Ct);

            if (ready.GetProperty("queueDepth").GetInt32() == 0 &&
                ready.GetProperty("index").GetString() == "ready")
            {
                // One more beat so the FTS write behind the last queue item has landed.
                await Task.Delay(200, Ct);
                return;
            }

            await Task.Delay(250, Ct);
        }

        throw new TimeoutException("The search index did not become ready within 15 seconds.");
    }

    private sealed record Paged<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize);

    private sealed record Hit(string Path, string Title, string Excerpt);

    private sealed record TagCount(string Tag, int Count);

    private sealed record Version(Guid Id, int Sequence);

    private sealed record StaleRow(string Path, string Title, string? Owner, bool Unassigned);
}
