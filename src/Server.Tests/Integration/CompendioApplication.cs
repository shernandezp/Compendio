using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Compendio.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Compendio.Tests.Integration;

/// <summary>
/// A real instance: real SQLite, a real temp data directory, real files on disk.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not an in-memory database and not a mocked file system. The whole product is a
/// claim about what happens on disk — atomic writes, the watcher, the content hash, encryption —
/// and a fixture that abstracts the disk away tests something else.
/// </para>
/// <para>
/// Configuration arrives as environment variables because the host resolves its data directory
/// during <c>CreateBuilder</c>, before a test could inject anything later in the pipeline.
/// </para>
/// </remarks>
public sealed class CompendioApplication : WebApplicationFactory<Program>, IAsyncLifetime
{
    private bool _ownsDirectory = true;

    /// <remarks>
    /// One public constructor, and it takes nothing: xUnit activates a collection fixture through
    /// this and rejects a type that offers a choice. Reuse of an existing directory goes through
    /// <see cref="StartingFrom"/> instead.
    /// </remarks>
    public CompendioApplication() =>
        DataDirectory = Path.Combine(Path.GetTempPath(), $"compendio-test-{Guid.CreateVersion7():N}");

    /// <summary>
    /// An instance that starts against an <em>existing</em> data directory.
    /// </summary>
    /// <remarks>
    /// For the behaviours that only exist across a restart: deleting the keys and coming back up,
    /// or restoring a backup and booting on it. It does not delete the directory on dispose, since
    /// it did not create it.
    /// </remarks>
    public static CompendioApplication StartingFrom(string dataDirectory) =>
        new() { DataDirectory = dataDirectory, _ownsDirectory = false };

    /// <summary>
    /// Stops this instance without deleting its data directory, so another one can start on it.
    /// </summary>
    /// <remarks>
    /// A restart test has to shut the host down first — the key files and the database are held
    /// open — but shutting it down normally also cleans the directory up, which would take the very
    /// state the test is about to assert on.
    /// </remarks>
    public async Task ShutDownKeepingDataAsync()
    {
        _ownsDirectory = false;
        await DisposeAsync();
    }

    public string DataDirectory { get; private init; }

    public string ContentRoot => Path.Combine(DataDirectory, "content");

    public string KeysRoot => Path.Combine(DataDirectory, "keys");

    public string DatabaseFile => Path.Combine(DataDirectory, "db", "compendio.db");

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Gives the host a web root holding a stand-in SPA shell.
    /// </summary>
    /// <remarks>
    /// The real <c>wwwroot</c> is produced by the client build, which the server test job skips on
    /// purpose — npm in a server CI job is a tax. Without a shell there is nothing for the fallback
    /// to serve, and the nonce substitution that the whole CSP depends on would go untested here
    /// and be caught only by the container job. This stands in for it: the same placeholder, in the
    /// same two positions, so the substitution is exercised for real.
    /// </remarks>
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        var webRoot = Path.Combine(DataDirectory, "wwwroot");
        Directory.CreateDirectory(webRoot);

        var shell = Path.Combine(webRoot, "index.html");
        if (!File.Exists(shell))
        {
            File.WriteAllText(shell,
                """
                <!doctype html>
                <html lang="es">
                  <head>
                    <meta name="csp-nonce" content="__CSP_NONCE__" />
                    <style nonce="__CSP_NONCE__">body { margin: 0; }</style>
                  </head>
                  <body><div id="root"></div></body>
                </html>
                """);
        }

        builder.UseSetting("webroot", webRoot);

        // A scripted AI provider, so the AI tests never depend on a model being installed and can
        // assert on the exact prompt the provider was handed. Registering it unconditionally is
        // safe: nothing resolves IAiProvider until an admin has configured a base URL and a model,
        // and every test that does not do so never reaches it.
        builder.ConfigureTestServices(services =>
            services.AddScoped<Application.Abstractions.IAiProvider>(_ => Ai));

        // Errors the host logs are otherwise invisible to a test: a 500 arrives as a ProblemDetails
        // that deliberately describes nothing, and the exception behind it goes to a log nobody is
        // reading. This keeps them, so an intermittent failure can be diagnosed from the run that
        // produced it rather than reproduced by hand.
        builder.ConfigureServices(services =>
            services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(Errors)));
    }

    /// <summary>Everything the host logged at <c>Error</c>, newest last.</summary>
    public List<string> Errors { get; } = [];

    /// <summary>
    /// The stand-in model. Records what it was sent, which is the whole point.
    /// </summary>
    /// <remarks>
    /// The retrieval-leak criterion is not "the answer did not mention the secret" — a model can be
    /// asked not to and comply by luck. It is "the secret was never in the prompt", and only a fake
    /// that keeps the prompt can assert that.
    /// </remarks>
    public StubAiProvider Ai { get; } = new();

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(DataDirectory);

        Environment.SetEnvironmentVariable("DataDir", DataDirectory);
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");

        // The watcher and the poll loop make timing assertions flaky in a test that is not about
        // them; the watcher tests turn it back on explicitly.
        Environment.SetEnvironmentVariable("Content__WatcherMode", "Native");
        Environment.SetEnvironmentVariable("Bootstrap__AdminUser", string.Empty);

        // Every test signs in, and in-process requests all share one client address, so the login
        // limiter partitions them together and trips. Raising the limit keeps the limiter itself in
        // the pipeline — it is still exercised by the test that asserts it works.
        Environment.SetEnvironmentVariable("Security__LoginAttemptsPerMinute", "100000");
        Environment.SetEnvironmentVariable("Security__WritesPerMinute", "100000");
        Environment.SetEnvironmentVariable("Security__SearchesPerMinute", "100000");

        // Warm the host so migrations and the FTS schema are in place before the first request.
        using var probe = CreateClient();
        _ = await probe.GetAsync("/health", TestContext.Current.CancellationToken);
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        // Pooled SQLite handles outlive the host, and on Windows they keep the file locked.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            if (_ownsDirectory && Directory.Exists(DataDirectory))
            {
                ClearReadOnly(DataDirectory);
                Directory.Delete(DataDirectory, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A SQLite handle can outlive the host by a moment on Windows, and the git-mirror test
            // leaves a .git directory whose object files git marks read-only. Leaving a temp folder
            // behind is not worth failing a test run over.
        }
    }

    /// <summary>
    /// Clears the read-only bit that git sets on the objects it writes.
    /// </summary>
    /// <remarks>
    /// <c>Directory.Delete(recursive: true)</c> refuses read-only files on Windows, and the git
    /// mirror leaves a <c>.git</c> directory full of them inside the content folder — exactly as it
    /// does in production.
    /// </remarks>
    private static void ClearReadOnly(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(file);

            if (attributes.HasFlag(FileAttributes.ReadOnly))
            {
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
        }
    }

    /// <summary>A client signed in as the administrator created by the setup wizard.</summary>
    public async Task<HttpClient> SignInAsAdminAsync(string userName = "admin", string password = "Compendio!Test1")
    {
        var client = CreateAuthenticatedClientHandle();

        var state = await client.GetFromJsonAsync<SetupState>("/api/v1/setup/state", Json, TestContext.Current.CancellationToken);

        if (state!.NeedsSetup)
        {
            var setup = await client.PostAsJsonAsync("/api/v1/setup", new
            {
                language = "es",
                adminUserName = userName,
                adminPassword = password,
                adminDisplayName = "Ana Rodríguez",
                instanceName = "Compendio Test",
                defaultAccess = "Read",
            }, Json, TestContext.Current.CancellationToken);

            setup.EnsureSuccessStatusCode();
        }

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { userName, password },
            Json, TestContext.Current.CancellationToken);

        login.EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>A client signed in as a user the administrator created.</summary>
    public async Task<HttpClient> SignInAsync(string userName, string password)
    {
        var client = CreateAuthenticatedClientHandle();

        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { userName, password },
            Json, TestContext.Current.CancellationToken);

        login.EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>Cookies are the auth mechanism, so the handler has to keep them.</summary>
    private HttpClient CreateAuthenticatedClientHandle() =>
        CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });

    /// <summary>Writes a file directly, as an external editor would.</summary>
    public async Task WriteFileAsync(string relativePath, string content)
    {
        var full = Path.Combine(ContentRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content.ReplaceLineEndings("\n"), TestContext.Current.CancellationToken);
    }

    public string ReadFile(string relativePath) =>
        File.ReadAllText(Path.Combine(ContentRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    public bool FileExists(string relativePath) =>
        File.Exists(Path.Combine(ContentRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>
    /// Empties the AI usage table.
    /// </summary>
    /// <remarks>
    /// The daily budget is counted over a rolling window in the database, and the whole class shares
    /// one instance — so without this, a test asserting on a cap of two would be counting whatever
    /// the tests before it happened to spend. Reset rather than isolate, because a per-test database
    /// would cost far more than one delete.
    /// </remarks>
    public async Task ResetAiUsageAsync()
    {
        using var scope = Services.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.CompendioDbContext>();
        await db.AiUsage.ExecuteDeleteAsync(TestContext.Current.CancellationToken);
    }

    public sealed record SetupState(bool NeedsSetup, string DefaultLanguage, object[] Languages, string ContentRoot);
}

/// <summary>Keeps every <c>Error</c> the host logs, so a 500 in a test is diagnosable.</summary>
internal sealed class CapturingLoggerProvider(List<string> sink) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, sink);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, List<string> sink) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            lock (sink)
            {
                sink.Add($"[{category}] {formatter(state, exception)}{(exception is null ? string.Empty : "\n" + exception)}");
            }
        }
    }
}

/// <summary>A model that answers from a script and remembers every prompt it was given.</summary>
public sealed class StubAiProvider : IAiProvider
{
    private readonly List<AiPrompt> _prompts = [];
    private readonly Lock _gate = new();

    /// <summary>What the next completion returns. Set per test.</summary>
    public string Reply { get; set; } = "OK";

    /// <summary>
    /// What the query-expansion call returns, one query per line.
    /// </summary>
    /// <remarks>
    /// Answered separately from <see cref="Reply"/> because "Ask the wiki" makes two calls with very
    /// different jobs. A stub that returned the scripted answer to the expansion call would search
    /// for the answer's own prose, retrieve nothing, and make every retrieval assertion pass
    /// vacuously — including the one that is supposed to prove a leak cannot happen.
    /// </remarks>
    public string ExpansionReply { get; set; } = string.Empty;

    /// <summary>Set to make the provider fail, for the error-path tests.</summary>
    public Exception? Fault { get; set; }

    public IReadOnlyList<AiPrompt> Prompts
    {
        get
        {
            lock (_gate)
            {
                return _prompts.ToArray();
            }
        }
    }

    /// <summary>Everything the model was shown, concatenated. What a leak assertion greps.</summary>
    public string AllPromptText => string.Concat(Prompts.Select(p => p.System + "\n" + p.User));

    public void Reset()
    {
        lock (_gate)
        {
            _prompts.Clear();
        }

        Fault = null;
        Reply = "OK";
        ExpansionReply = string.Empty;
    }

    public Task<AiCompletion> CompleteAsync(AiPrompt prompt, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _prompts.Add(prompt);
        }

        if (Fault is { } fault)
        {
            throw fault;
        }

        var isExpansion = prompt.System.Contains("keyword search queries", StringComparison.OrdinalIgnoreCase);
        var text = isExpansion && ExpansionReply.Length > 0 ? ExpansionReply : Reply;

        return Task.FromResult(new AiCompletion(text, "stub-model", 0, 0));
    }

    public Task<AiProbeResult> ProbeAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Fault is null
            ? new AiProbeResult(true, "OK", "stub-model")
            : new AiProbeResult(false, Fault.Message, null));
}

[CollectionDefinition(nameof(CompendioCollection))]
public sealed class CompendioCollection : ICollectionFixture<CompendioApplication>;
