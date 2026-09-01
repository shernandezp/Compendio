using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Compendio.Tests.Integration;

/// <summary>
/// That AI is genuinely optional, and that retrieval is a permissions boundary rather than a prompt.
/// </summary>
/// <remarks>
/// <para>
/// Two criteria live here and they pull in opposite directions. One says the product must be
/// complete with no AI configured — no endpoint that answers, no affordance to render. The other
/// says that when AI <em>is</em> configured, content the asking user cannot read must never reach
/// the model.
/// </para>
/// <para>
/// The second is asserted against the prompt the provider was handed, not against the answer it
/// gave. A model can be told not to mention something and comply by luck; the only assertion worth
/// making is that the secret was never sent.
/// </para>
/// </remarks>
[Collection(nameof(CompendioCollection))]
public sealed class AiTests(CompendioApplication app) : IAsyncLifetime
{
    /// <summary>
    /// A word that appears only in the restricted page's body — never in a question.
    /// </summary>
    /// <remarks>
    /// The distinction is the whole test. Asserting that the prompt does not contain a word the user
    /// typed proves nothing, because the question is sent to the model by design. Only a string that
    /// exists solely inside the page can show whether the page reached the model.
    /// </remarks>
    private const string Secret = "Bluebird";
    private const string RestrictedFolder = "AiSecrets";
    private const string RestrictedPage = "AiSecrets/Merger.md";
    private const string OpenPage = "AiOpen/Handbook.md";

    private static JsonSerializerOptions Json => CompendioApplication.Json;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync()
    {
        _admin = await app.SignInAsAdminAsync();
        app.Ai.Reset();

        // Every test in this class decides for itself whether AI is on, and starts from an unspent
        // budget — the counter lives in the database and the instance is shared.
        await _admin.DeleteAsync("/api/v1/admin/ai", Ct);
        await app.ResetAiUsageAsync();
    }

    public ValueTask DisposeAsync()
    {
        app.Ai.Reset();
        _admin.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Criterion 6: with no provider configured, every AI action returns 404 and status says so.
    /// </summary>
    /// <remarks>
    /// Asserted per action rather than once, because the failure this guards against is a seventh
    /// action being added later without the guard call — which a single spot-check would miss.
    /// </remarks>
    [Fact]
    public async Task WithNoProviderConfiguredEveryActionRefusesAndStatusReportsDisabled()
    {
        var status = await _admin.GetFromJsonAsync<JsonElement>("/api/v1/ai/status", Json, Ct);
        status.GetProperty("enabled").GetBoolean().ShouldBeFalse();
        status.GetProperty("features").GetArrayLength().ShouldBe(0);

        var path = await EnsurePageAsync("AiOpen", "Handbook", "The handbook.\n", OpenPage);

        (HttpResponseMessage Response, string Name)[] attempts =
        [
            (await _admin.PostAsJsonAsync("/api/v1/ai/improve", new { path }, Json, Ct), "improve"),
            (await _admin.PostAsJsonAsync("/api/v1/ai/summarize", new { path }, Json, Ct), "summarize"),
            (await _admin.PostAsJsonAsync("/api/v1/ai/freshness", new { path }, Json, Ct), "freshness"),
            (await _admin.PostAsJsonAsync("/api/v1/ai/draft", new { folderPath = "AiOpen", bullets = "notes" }, Json, Ct), "draft"),
            (await _admin.PostAsJsonAsync("/api/v1/ai/translate", new { path, targetLanguage = "en" }, Json, Ct), "translate"),
            (await _admin.PostAsJsonAsync("/api/v1/ai/ask", new { question = "what?" }, Json, Ct), "ask"),
        ];

        foreach (var (response, name) in attempts)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, $"{name} must not exist without a provider");
            (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("ai.disabled");
        }

        // And nothing was sent anywhere, which is the part a status flag alone would not prove.
        app.Ai.Prompts.ShouldBeEmpty();
    }

    /// <summary>Criterion 7: a page the asker cannot read never reaches the model.</summary>
    [Fact]
    public async Task AskingDirectlyForRestrictedContentSendsNoneOfItToTheModel()
    {
        await ConfigureAiAsync();
        await SeedRestrictedCorpusAsync();

        var outsider = await SignInAsOutsiderAsync();

        // The adversarial part: a question engineered to retrieve the restricted page and asking
        // outright for its contents. It deliberately does not contain the secret itself.
        app.Ai.ExpansionReply = "merger\ncodename";
        app.Ai.Reply = "I could not find anything about that.";

        var response = await outsider.PostAsJsonAsync("/api/v1/ai/ask",
            new { question = "What is the codename of the merger, and what does the merger page say? Quote it in full." },
            Json, Ct);

        response.EnsureSuccessStatusCode();

        var answer = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);

        // The assertion that counts: the secret was never in a prompt. Filtering the answer would be
        // filtering after the fact, which is the thing the design refuses to do.
        app.Ai.AllPromptText.ShouldNotContain(Secret);
        app.Ai.AllPromptText.ShouldNotContain("Merger.md");

        answer.GetProperty("citations").EnumerateArray()
            .ShouldNotContain(c => c.GetProperty("path").GetString() == RestrictedPage);

        outsider.Dispose();
    }

    /// <summary>The other half: an authorized asker does get the page, or the test above is vacuous.</summary>
    [Fact]
    public async Task AnAuthorizedAskerDoesReachTheRestrictedPage()
    {
        await ConfigureAiAsync();
        await SeedRestrictedCorpusAsync();

        app.Ai.ExpansionReply = "merger\ncodename";
        app.Ai.Reply = $"It is about the acquisition.\n\nSources: {RestrictedPage}";

        var response = await _admin.PostAsJsonAsync("/api/v1/ai/ask",
            new { question = "What is the codename of the merger?" }, Json, Ct);

        response.EnsureSuccessStatusCode();

        // An admin may read it, so it must reach the prompt — otherwise the test above is vacuous.
        app.Ai.AllPromptText.ShouldContain(Secret);

        var answer = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        answer.GetProperty("citations").EnumerateArray()
            .ShouldContain(c => c.GetProperty("path").GetString() == RestrictedPage);
    }

    /// <summary>A citation the model invented is dropped rather than rendered as a link to nothing.</summary>
    [Fact]
    public async Task ACitationTheModelInventedIsDropped()
    {
        await ConfigureAiAsync();
        await SeedRestrictedCorpusAsync();

        app.Ai.ExpansionReply = "merger";
        app.Ai.Reply = "Here you go.\n\nSources: Totally/Made-Up.md, " + RestrictedPage;

        var response = await _admin.PostAsJsonAsync("/api/v1/ai/ask", new { question = "merger" }, Json, Ct);
        response.EnsureSuccessStatusCode();

        var answer = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        var citations = answer.GetProperty("citations").EnumerateArray()
            .Select(c => c.GetProperty("path").GetString()).ToArray();

        citations.ShouldNotContain("Totally/Made-Up.md");
    }

    /// <summary>Criterion 8: the sibling is written, badged, and the badge clears on a human save.</summary>
    [Fact]
    public async Task TranslationIsBadgedUntilAHumanSavesIt()
    {
        await ConfigureAiAsync();

        var source = await EnsurePageAsync("AiOpen", "Holiday policy",
            "You get twenty-three days.\n", "AiOpen/Holiday-policy.md");

        app.Ai.Reply = "Tienes veintitrés días.\n";

        var response = await _admin.PostAsJsonAsync("/api/v1/ai/translate",
            new { path = source, targetLanguage = "es" }, Json, Ct);

        response.EnsureSuccessStatusCode();

        var translated = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        var translatedPath = translated.GetProperty("path").GetString()!;

        translatedPath.ShouldEndWith(".es.md");

        var onDisk = app.ReadFile(translatedPath);
        onDisk.ShouldContain("machineTranslated: true");
        onDisk.ShouldContain("lang: es");
        onDisk.ShouldContain("translationKey:");

        // A human saves it. The badge is cleared by the server, not by trusting the client to have
        // dropped the key.
        var current = await _admin.GetFromJsonAsync<JsonElement>($"/api/v1/pages/{translatedPath}", Json, Ct);

        var save = await _admin.PutAsJsonAsync($"/api/v1/pages/{translatedPath}", new
        {
            content = current.GetProperty("content").GetString(),
            expectedHash = current.GetProperty("contentHash").GetString(),
        }, Json, Ct);

        save.EnsureSuccessStatusCode();

        app.ReadFile(translatedPath).ShouldNotContain("machineTranslated");

        // Clearing the badge must not eat the rest of the front matter.
        app.ReadFile(translatedPath).ShouldContain("lang: es");
    }

    /// <summary>A secure scope is excluded from AI until it opts in, and says so when it does not.</summary>
    [Fact]
    public async Task ASecureScopeIsExcludedFromAiUntilItOptsIn()
    {
        await ConfigureAiAsync();

        const string folder = "AiVault";
        const string page = "AiVault/Passwords.md";

        await EnsurePageAsync(folder, "Passwords", "The wifi key is hunter2.\n", page);

        var scope = await _admin.PostAsJsonAsync("/api/v1/admin/secure-scopes",
            new { path = folder, indexContent = false, allowAi = false }, Json, Ct);

        if (scope.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.BadRequest))
        {
            scope.EnsureSuccessStatusCode();
        }

        var refused = await _admin.PostAsJsonAsync("/api/v1/ai/summarize", new { path = page }, Json, Ct);

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("ai.not_allowed_here");
        app.Ai.AllPromptText.ShouldNotContain("hunter2");
    }

    /// <summary>Criterion 10, the AI half: clearing the configuration removes every affordance again.</summary>
    [Fact]
    public async Task ClearingTheConfigurationReturnsTheInstanceToNoAiAtAll()
    {
        await ConfigureAiAsync();
        (await _admin.GetFromJsonAsync<JsonElement>("/api/v1/ai/status", Json, Ct))
            .GetProperty("enabled").GetBoolean().ShouldBeTrue();

        var cleared = await _admin.DeleteAsync("/api/v1/admin/ai", Ct);
        cleared.EnsureSuccessStatusCode();

        var status = await _admin.GetFromJsonAsync<JsonElement>("/api/v1/ai/status", Json, Ct);
        status.GetProperty("enabled").GetBoolean().ShouldBeFalse();

        var ask = await _admin.PostAsJsonAsync("/api/v1/ai/ask", new { question = "anything" }, Json, Ct);
        ask.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The daily budget stops requests reaching the provider, and says when it frees up.
    /// </summary>
    /// <remarks>
    /// The assertion that matters is the prompt count, not the status code. A limit that returns 429
    /// <em>after</em> letting the request through would look identical from the outside and would
    /// have cost exactly as much money, which is the entire thing this feature exists to prevent.
    /// </remarks>
    [Fact]
    public async Task TheDailyBudgetRefusesOnceItIsSpentAndNothingFurtherReachesTheProvider()
    {
        await ConfigureAiAsync(dailyPerUser: 2);

        var path = await EnsurePageAsync("AiOpen", "Handbook", "The handbook.\n", OpenPage);
        app.Ai.Reply = "A tidier handbook.";

        for (var i = 0; i < 2; i++)
        {
            var allowed = await _admin.PostAsJsonAsync("/api/v1/ai/summarize", new { path }, Json, Ct);
            allowed.EnsureSuccessStatusCode();
        }

        var sentSoFar = app.Ai.Prompts.Count;

        var refused = await _admin.PostAsJsonAsync("/api/v1/ai/summarize", new { path }, Json, Ct);

        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        problem.GetProperty("code").GetString().ShouldBe("ai.quota_exceeded");
        problem.GetProperty("scope").GetString().ShouldBe("user");
        problem.GetProperty("limit").GetInt32().ShouldBe(2);
        problem.TryGetProperty("resetsAt", out _).ShouldBeTrue();

        app.Ai.Prompts.Count.ShouldBe(sentSoFar, "a refused request must not have reached the provider");
    }

    /// <summary>The budget is reported before it is spent, so the UI can warn rather than surprise.</summary>
    [Fact]
    public async Task StatusReportsWhatIsLeftOfTheCallersBudget()
    {
        await ConfigureAiAsync(dailyPerUser: 3);

        var path = await EnsurePageAsync("AiOpen", "Handbook", "The handbook.\n", OpenPage);
        app.Ai.Reply = "Shorter.";

        var before = await _admin.GetFromJsonAsync<JsonElement>("/api/v1/ai/status", Json, Ct);
        var remainingBefore = before.GetProperty("budget").GetProperty("remaining").GetInt32();

        (await _admin.PostAsJsonAsync("/api/v1/ai/summarize", new { path }, Json, Ct)).EnsureSuccessStatusCode();

        var after = await _admin.GetFromJsonAsync<JsonElement>("/api/v1/ai/status", Json, Ct);

        after.GetProperty("budget").GetProperty("limit").GetInt32().ShouldBe(3);
        after.GetProperty("budget").GetProperty("remaining").GetInt32().ShouldBe(remainingBefore - 1);
    }

    /// <summary>
    /// A request refused on permissions costs nothing, because it never reached the provider.
    /// </summary>
    /// <remarks>
    /// The ordering this pins down is the one that is easy to get backwards: charging in the guard's
    /// first call would bill somebody for a page they were told they could not touch, and would let a
    /// stranger drain an editor's allowance by asking about pages they cannot read.
    /// </remarks>
    [Fact]
    public async Task ARequestRefusedOnPermissionsDoesNotSpendTheBudget()
    {
        await ConfigureAiAsync(dailyPerUser: 5);
        await SeedRestrictedCorpusAsync();

        var outsider = await SignInAsOutsiderAsync();

        var refused = await outsider.PostAsJsonAsync("/api/v1/ai/summarize", new { path = RestrictedPage }, Json, Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var status = await outsider.GetFromJsonAsync<JsonElement>("/api/v1/ai/status", Json, Ct);
        status.GetProperty("budget").GetProperty("used").GetInt32().ShouldBe(0);

        outsider.Dispose();
    }

    /// <summary>
    /// The admin screen reports what the instance has spent even when no instance cap is set.
    /// </summary>
    /// <remarks>
    /// Found by running the product rather than by reading it. The usage figure exists so an
    /// administrator can choose a cap against real numbers — and since the instance cap ships off,
    /// the person doing the choosing is always in the no-cap case. An implementation that only
    /// counted once a cap existed showed zero to exactly the person who needed the number, and the
    /// truth only to somebody who had already decided.
    /// </remarks>
    [Fact]
    public async Task InstanceUsageIsReportedEvenWithNoInstanceCapSet()
    {
        await ConfigureAiAsync(dailyPerUser: 1000, dailyPerInstance: 0);

        var path = await EnsurePageAsync("AiOpen", "Handbook", "The handbook.\n", OpenPage);
        app.Ai.Reply = "Shorter.";

        (await _admin.PostAsJsonAsync("/api/v1/ai/summarize", new { path }, Json, Ct)).EnsureSuccessStatusCode();

        var settings = await _admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/ai", Json, Ct);
        var usage = settings.GetProperty("instanceUsage");

        usage.GetProperty("limit").GetInt32().ShouldBe(0, "no cap is set");
        usage.GetProperty("used").GetInt32().ShouldBe(1, "but the spend is still real and still counted");

        // No ceiling means no countdown. A number here would be an invented limit.
        usage.TryGetProperty("remaining", out var remaining).ShouldBeFalse(
            "remaining must stay absent when there is nothing to count down from");
        remaining.ValueKind.ShouldBe(JsonValueKind.Undefined);

        settings.GetProperty("topSpenders").GetArrayLength().ShouldBe(1);
    }

    /// <summary>The instance-wide cap is a separate ceiling with its own, differently worded refusal.</summary>
    [Fact]
    public async Task TheInstanceCapRefusesEvenWhenThePersonalOneHasRoomLeft()
    {
        await ConfigureAiAsync(dailyPerUser: 1000, dailyPerInstance: 1);

        var path = await EnsurePageAsync("AiOpen", "Handbook", "The handbook.\n", OpenPage);
        app.Ai.Reply = "Shorter.";

        (await _admin.PostAsJsonAsync("/api/v1/ai/summarize", new { path }, Json, Ct)).EnsureSuccessStatusCode();

        var refused = await _admin.PostAsJsonAsync("/api/v1/ai/summarize", new { path }, Json, Ct);

        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        problem.GetProperty("code").GetString().ShouldBe("ai.quota_exceeded_instance");
        problem.GetProperty("scope").GetString().ShouldBe("instance");
    }

    /// <summary>
    /// The usage table is pruned from a scope with no request behind it, as maintenance does it.
    /// </summary>
    /// <remarks>
    /// Worth its own test because the failure is invisible for six hours and then permanent: the
    /// prune runs inside <c>MaintenanceService</c>'s background scope, where <c>ICurrentUser</c> has
    /// no <c>HttpContext</c> to read. Anything on that path that assumed a request would throw into
    /// a catch block that logs and waits another six hours, while the table grew forever.
    /// </remarks>
    [Fact]
    public async Task UsageIsPrunedFromABackgroundScopeWithNoRequestBehindIt()
    {
        await ConfigureAiAsync(dailyPerUser: 10);

        var path = await EnsurePageAsync("AiOpen", "Handbook", "The handbook.\n", OpenPage);
        app.Ai.Reply = "Shorter.";

        (await _admin.PostAsJsonAsync("/api/v1/ai/summarize", new { path }, Json, Ct)).EnsureSuccessStatusCode();

        using var scope = app.Services.CreateScope();
        var budget = scope.ServiceProvider.GetRequiredService<Compendio.Application.Ai.AiBudget>();

        // Retention is thirty days, so today's row survives — the assertion is that the pass runs at
        // all, not that it deleted something.
        await Should.NotThrowAsync(() => budget.PruneAsync(Ct));

        var status = await _admin.GetFromJsonAsync<JsonElement>("/api/v1/ai/status", Json, Ct);
        status.GetProperty("budget").GetProperty("used").GetInt32().ShouldBe(1);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    /// <param name="dailyPerUser">
    /// Explicit in every test rather than left to the shipped default, so a test that happens to make
    /// a lot of AI calls cannot start failing on a budget it never meant to exercise.
    /// </param>
    private async Task ConfigureAiAsync(int dailyPerUser = 1000, int dailyPerInstance = 0)
    {
        var response = await _admin.PutAsJsonAsync("/api/v1/admin/ai", new
        {
            baseUrl = "http://localhost:11434/v1",
            model = "stub-model",
            apiKey = "not-a-real-key",
            dailyPerUser,
            dailyPerInstance,
        }, Json, Ct);

        response.EnsureSuccessStatusCode();
    }

    private async Task SeedRestrictedCorpusAsync()
    {
        await EnsurePageAsync(RestrictedFolder, "Merger",
            $"The {Secret} merger completes in March. Due diligence is with Legal.\n", RestrictedPage);

        await EnsurePageAsync("AiOpen", "Handbook", "The company handbook. Nothing secret here.\n", OpenPage);

        var acl = await _admin.PutAsJsonAsync($"/api/v1/acl/{RestrictedFolder}",
            new { inheritParent = false, entries = Array.Empty<object>() }, Json, Ct);

        acl.EnsureSuccessStatusCode();

        await WaitForIndexAsync();
    }

    private async Task<HttpClient> SignInAsOutsiderAsync()
    {
        var create = await _admin.PostAsJsonAsync("/api/v1/admin/users", new
        {
            userName = "carla",
            password = "Compendio!Test3",
            displayName = "Carla Ruiz",
            role = "Editor",
        }, Json, Ct);

        if (create.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.BadRequest))
        {
            create.EnsureSuccessStatusCode();
        }

        return await app.SignInAsync("carla", "Compendio!Test3");
    }

    private async Task<string> EnsurePageAsync(string folder, string title, string body, string expectedPath)
    {
        if (app.FileExists(expectedPath))
        {
            return expectedPath;
        }

        var response = await _admin.PostAsJsonAsync("/api/v1/pages",
            new { folderPath = folder, title, content = body }, Json, Ct);

        response.EnsureSuccessStatusCode();

        var page = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        return page.GetProperty("path").GetString()!;
    }

    private async Task WaitForIndexAsync()
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var ready = await _admin.GetFromJsonAsync<JsonElement>("/ready", Json, Ct);

            if (ready.GetProperty("queueDepth").GetInt32() == 0 &&
                ready.GetProperty("index").GetString() == "ready")
            {
                await Task.Delay(200, Ct);
                return;
            }

            await Task.Delay(250, Ct);
        }

        throw new TimeoutException("The search index did not become ready within 15 seconds.");
    }
}
