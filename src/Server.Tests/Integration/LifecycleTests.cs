using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Compendio.Application.Abstractions;
using Compendio.Domain.Entities;
using Compendio.Engine;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Compendio.Tests.Integration;

/// <summary>
/// Review dates, the stale surfaces, notification dedup and versioned acknowledgment.
/// </summary>
/// <remarks>
/// The assertions that matter are the negative ones: that an ordinary edit does <em>not</em> reset
/// the review clock, and does <em>not</em> re-open an acknowledgment. Both are behaviours the
/// feature is worthless without, and both are easy to break with a one-line change that looks like
/// a simplification.
/// </remarks>
[Collection(nameof(CompendioCollection))]
public sealed class LifecycleTests(CompendioApplication app) : IAsyncLifetime
{
    private static JsonSerializerOptions Json => CompendioApplication.Json;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync() => _admin = await app.SignInAsAdminAsync();

    public ValueTask DisposeAsync()
    {
        _admin.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Criterion 1, the half about the review clock.</summary>
    [Fact]
    public async Task AnOrdinaryEditDoesNotResetTheReviewClockButConfirmingAReviewDoes()
    {
        var path = await CreatePageAsync("Lifecycle", "Backup procedure", "Run the backup.\n");

        await SetLifecycleAsync(path, owner: "admin", reviewIntervalDays: 30,
            nextReviewDate: DateTimeOffset.UtcNow.AddDays(-5));

        var stale = await GetLifecycleAsync(path);
        stale.GetProperty("isStale").GetBoolean().ShouldBeTrue();

        // An ordinary save. Fixing a typo is not a review, and if it cleared the flag then "stale"
        // would mean "recently touched" rather than "recently checked".
        await SaveAsync(path, "Run the backup twice.\n");

        var afterEdit = await GetLifecycleAsync(path);
        afterEdit.GetProperty("isStale").GetBoolean().ShouldBeTrue("an edit is not a review");

        var confirm = await _admin.PostAsJsonAsync("/api/v1/pages/review-confirm", new { path }, Json, Ct);
        confirm.EnsureSuccessStatusCode();

        var afterReview = await confirm.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        afterReview.GetProperty("isStale").GetBoolean().ShouldBeFalse();

        // Measured from now, not from the date it was already overdue by.
        afterReview.GetProperty("nextReviewDate").GetDateTimeOffset()
            .ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddDays(29));
    }

    /// <summary>Criterion 1, the half about the three surfaces — and that no mail client exists.</summary>
    [Fact]
    public async Task AStalePageAppearsInTheReportAndOnItsOwnersDashboard()
    {
        var path = await CreatePageAsync("Lifecycle", "Fire drill", "Assemble outside.\n");
        await SetLifecycleAsync(path, owner: "admin", reviewIntervalDays: 90,
            nextReviewDate: DateTimeOffset.UtcNow.AddDays(-400));

        var report = await _admin.GetFromJsonAsync<JsonElement>("/api/v1/lifecycle/stale?pageSize=100", Json, Ct);
        var rows = report.GetProperty("items").EnumerateArray().ToArray();

        var row = rows.Single(r => r.GetProperty("path").GetString() == path);
        row.GetProperty("daysOverdue").GetInt32().ShouldBeGreaterThan(390);
        row.GetProperty("unassigned").GetBoolean().ShouldBeFalse();

        var dashboard = await _admin.GetFromJsonAsync<JsonElement>("/api/v1/dashboard", Json, Ct);
        dashboard.GetProperty("myStalePages").EnumerateArray()
            .ShouldContain(p => p.GetProperty("path").GetString() == path);
    }

    /// <summary>Criterion 2: an owner nobody matches is reported, and the front matter is untouched.</summary>
    [Fact]
    public async Task AnUnresolvableOwnerIsReportedAsUnassignedAndLeftInTheFile()
    {
        var path = await CreatePageAsync("Lifecycle", "Orphan runbook", "Nobody owns this.\n");

        await SetLifecycleAsync(path, owner: "someone.who.left", reviewIntervalDays: 1,
            nextReviewDate: DateTimeOffset.UtcNow.AddDays(-10));

        var lifecycle = await GetLifecycleAsync(path);
        lifecycle.GetProperty("owner").GetString().ShouldBe("someone.who.left");
        lifecycle.TryGetProperty("ownerUserId", out var ownerId).ShouldBeFalse("nothing resolved, so nothing is reported");

        // The value a human typed survives verbatim. Eating it would break the promise that the file
        // is the source of truth.
        app.ReadFile(path).ShouldContain("owner: someone.who.left");

        var report = await _admin.GetFromJsonAsync<JsonElement>("/api/v1/lifecycle/stale?owner=-&pageSize=100", Json, Ct);
        report.GetProperty("items").EnumerateArray()
            .ShouldContain(r => r.GetProperty("path").GetString() == path && r.GetProperty("unassigned").GetBoolean());
    }

    /// <summary>Criterion 3: a condition that persists produces one row, not one per scan.</summary>
    [Fact]
    public async Task APersistentlyStalePageProducesExactlyOneUnreadNotification()
    {
        var path = await CreatePageAsync("Lifecycle", "Quarterly review", "Check the figures.\n");
        await SetLifecycleAsync(path, owner: "admin", reviewIntervalDays: 30,
            nextReviewDate: DateTimeOffset.UtcNow.AddDays(-100));

        // Ten scans, as ten nights would be.
        for (var night = 0; night < 10; night++)
        {
            await RunReviewScanAsync();
        }

        var mine = await UnreadForAsync(path, NotificationKind.PageStale);
        mine.Count.ShouldBe(1, "the unread dedup index is what stops ninety rows for one stale page");

        // Read it, and the same condition may speak again — which is what makes the inbox useful
        // rather than a wall that only ever grows.
        var read = await _admin.PostAsync($"/api/v1/notifications/{mine[0]}/read", null, Ct);
        read.EnsureSuccessStatusCode();

        await RunReviewScanAsync();
        (await UnreadForAsync(path, NotificationKind.PageStale)).Count.ShouldBe(1);
    }

    /// <summary>Confirming a review withdraws the reminder rather than leaving it to be re-read.</summary>
    [Fact]
    public async Task ConfirmingAReviewWithdrawsTheStaleNotification()
    {
        var path = await CreatePageAsync("Lifecycle", "Access review", "Check who has access.\n");
        await SetLifecycleAsync(path, owner: "admin", reviewIntervalDays: 30,
            nextReviewDate: DateTimeOffset.UtcNow.AddDays(-40));

        await RunReviewScanAsync();
        (await UnreadForAsync(path, NotificationKind.PageStale)).ShouldNotBeEmpty();

        var confirm = await _admin.PostAsJsonAsync("/api/v1/pages/review-confirm", new { path }, Json, Ct);
        confirm.EnsureSuccessStatusCode();

        (await UnreadForAsync(path, NotificationKind.PageStale)).ShouldBeEmpty();
    }

    /// <summary>
    /// Criterion 4: acknowledgment is versioned, and only an explicitly material revision re-opens it.
    /// </summary>
    [Fact]
    public async Task AcknowledgmentSurvivesAnOrdinaryEditAndReOpensOnAMaterialOne()
    {
        var path = await CreatePageAsync("HR", "Teleworking policy", "Work from home two days a week.\n");
        await SetLifecycleAsync(path, owner: "admin", requiresAcknowledgment: true);

        var acknowledge = await _admin.PostAsJsonAsync("/api/v1/acknowledgments", new { path }, Json, Ct);
        acknowledge.EnsureSuccessStatusCode();

        (await ReportAsync(path)).GetProperty("acknowledgedCount").GetInt32().ShouldBe(1);

        // An ordinary save. Nobody is asked to read it again — re-asking two hundred people to
        // re-read a typo fix is how this feature gets switched off.
        await SaveAsync(path, "Work from home two days per week.\n");
        (await ReportAsync(path)).GetProperty("acknowledgedCount").GetInt32()
            .ShouldBe(1, "an ordinary edit leaves acknowledgments standing");

        // The editor's explicit answer to "does everyone need to read this again?".
        await SaveAsync(path, "Work from home four days a week.\n", materialRevision: true);

        var reopened = await ReportAsync(path);
        reopened.GetProperty("acknowledgedCount").GetInt32().ShouldBe(0, "a material revision re-opens it");
        reopened.GetProperty("requiredCount").GetInt32().ShouldBeGreaterThan(0);
    }

    /// <summary>The compliance case: the record outlives the page it is about.</summary>
    [Fact]
    public async Task AnAcknowledgmentSurvivesItsPageBeingDeleted()
    {
        var path = await CreatePageAsync("HR", "Expenses policy", "Keep your receipts.\n");
        await SetLifecycleAsync(path, owner: "admin", requiresAcknowledgment: true);

        (await _admin.PostAsJsonAsync("/api/v1/acknowledgments", new { path }, Json, Ct)).EnsureSuccessStatusCode();

        var delete = await _admin.DeleteAsync($"/api/v1/pages/{path}", Ct);
        delete.EnsureSuccessStatusCode();

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ICompendioDbContext>();

        // No foreign keys, deliberately: "who signed off on the policy we deleted in March" is
        // exactly the question this table exists to answer.
        db.Acknowledgments.Any(a => a.Path == path).ShouldBeTrue();
    }

    /// <summary>Clicking twice is not a second acknowledgment, and is not an error either.</summary>
    [Fact]
    public async Task AcknowledgingTwiceIsIdempotent()
    {
        var path = await CreatePageAsync("HR", "Safety policy", "Wear the boots.\n");
        await SetLifecycleAsync(path, owner: "admin", requiresAcknowledgment: true);

        var first = await _admin.PostAsJsonAsync("/api/v1/acknowledgments", new { path }, Json, Ct);
        var second = await _admin.PostAsJsonAsync("/api/v1/acknowledgments", new { path }, Json, Ct);

        first.EnsureSuccessStatusCode();
        second.EnsureSuccessStatusCode();

        (await ReportAsync(path)).GetProperty("acknowledgedCount").GetInt32().ShouldBe(1);
    }

    /// <summary>A page that does not require it has nothing to confirm.</summary>
    [Fact]
    public async Task AcknowledgingAPageThatDoesNotRequireItIsRefused()
    {
        var path = await CreatePageAsync("HR", "Kitchen rota", "Wash your mug.\n");

        var response = await _admin.PostAsJsonAsync("/api/v1/acknowledgments", new { path }, Json, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("ack.not_required");
    }

    /// <summary>
    /// Criterion 5's second half, asserted rather than eyeballed in Excel.
    /// </summary>
    /// <remarks>
    /// The BOM is the whole point. Excel opens a BOM-less UTF-8 file as the system code page, which
    /// turns "Política" into mojibake — and the first thing anybody does with a compliance export is
    /// open it in Excel.
    /// </remarks>
    [Fact]
    public async Task TheAcknowledgmentCsvIsUtf8WithABomSoExcelReadsAccentsCorrectly()
    {
        var path = await CreatePageAsync("HR", "Política de teletrabajo", "Trabajo en casa dos días.\n");
        await SetLifecycleAsync(path, owner: "admin", requiresAcknowledgment: true);

        (await _admin.PostAsJsonAsync("/api/v1/acknowledgments", new { path }, Json, Ct)).EnsureSuccessStatusCode();

        var response = await _admin.GetAsync(
            $"/api/v1/acknowledgments/report.csv?path={Uri.EscapeDataString(path)}", Ct);

        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(Ct);

        bytes.Take(3).ShouldBe(new byte[] { 0xEF, 0xBB, 0xBF }, "Excel needs the BOM to read this as UTF-8");

        var text = System.Text.Encoding.UTF8.GetString(bytes);
        text.ShouldContain("Política de teletrabajo");
        text.ShouldContain("Ana Rodríguez");

        // RFC 4180 line endings, which is what the specification asks for and what Excel prefers.
        text.ShouldContain("\r\n");
    }

    /// <summary>A field containing the delimiter is quoted rather than splitting the row.</summary>
    [Fact]
    public async Task TheStaleCsvQuotesFieldsContainingCommas()
    {
        var path = await CreatePageAsync("Lifecycle", "Backups, restores and drills", "Test them.\n");
        await SetLifecycleAsync(path, owner: "admin", reviewIntervalDays: 30,
            nextReviewDate: DateTimeOffset.UtcNow.AddDays(-10));

        var csv = await _admin.GetStringAsync("/api/v1/lifecycle/stale.csv", Ct);

        csv.ShouldContain("\"Backups, restores and drills\"");
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private async Task<string> CreatePageAsync(string folder, string title, string body)
    {
        var response = await _admin.PostAsJsonAsync("/api/v1/pages",
            new { folderPath = folder, title, content = body }, Json, Ct);

        response.EnsureSuccessStatusCode();

        var page = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        return page.GetProperty("path").GetString()!;
    }

    private async Task SaveAsync(string path, string body, bool materialRevision = false)
    {
        var current = await _admin.GetFromJsonAsync<JsonElement>($"/api/v1/pages/{path}", Json, Ct);
        var hash = current.GetProperty("contentHash").GetString();
        var content = current.GetProperty("content").GetString()!;

        // Keep the front matter, replace the body — the same shape the editor sends.
        var frontMatterEnd = content.IndexOf("---", 3, StringComparison.Ordinal);
        var header = frontMatterEnd > 0 ? content[..(frontMatterEnd + 4)] : string.Empty;

        var response = await _admin.PutAsJsonAsync($"/api/v1/pages/{path}",
            new { content = header + "\n" + body, expectedHash = hash, materialRevision }, Json, Ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Saving '{path}' returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}\n" +
                string.Join("\n", app.Errors));
        }
    }

    private async Task SetLifecycleAsync(
        string path,
        string? owner = null,
        int? reviewIntervalDays = null,
        DateTimeOffset? nextReviewDate = null,
        bool? requiresAcknowledgment = null)
    {
        var response = await _admin.PutAsJsonAsync("/api/v1/pages/lifecycle",
            new { path, owner, reviewIntervalDays, nextReviewDate, requiresAcknowledgment }, Json, Ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Setting lifecycle on '{path}' returned {(int)response.StatusCode}: " +
                $"{await response.Content.ReadAsStringAsync(Ct)}\n" + string.Join("\n", app.Errors));
        }
    }

    private Task<JsonElement> GetLifecycleAsync(string path) =>
        _admin.PutAsJsonAsync("/api/v1/pages/lifecycle", new { path }, Json, Ct)
            .ContinueWith(t => t.Result.Content.ReadFromJsonAsync<JsonElement>(Json, Ct).Result, Ct);

    private async Task<JsonElement> ReportAsync(string path) =>
        await _admin.GetFromJsonAsync<JsonElement>($"/api/v1/acknowledgments/page?path={Uri.EscapeDataString(path)}", Json, Ct);

    /// <summary>
    /// Runs one scan directly instead of waiting for the hosted service.
    /// </summary>
    /// <remarks>
    /// The scans are separate classes from the timer for exactly this: a test that sleeps and hopes
    /// is not a test of the behaviour, it is a test of the machine it runs on.
    /// </remarks>
    private async Task RunReviewScanAsync()
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ReviewScan>().RunAsync(Ct);
    }

    private async Task<IReadOnlyList<Guid>> UnreadForAsync(string path, NotificationKind kind)
    {
        var response = await _admin.GetFromJsonAsync<JsonElement>("/api/v1/notifications?pageSize=100", Json, Ct);

        return response.GetProperty("items").EnumerateArray()
            .Where(n => n.GetProperty("targetPath").GetString() == path
                        && n.GetProperty("kind").GetString() == kind.ToString()
                        && !n.TryGetProperty("readAt", out _))
            .Select(n => n.GetProperty("id").GetGuid())
            .ToArray();
    }
}
