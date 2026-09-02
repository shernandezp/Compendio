using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Compendio.Application.Abstractions;
using Compendio.Hosting;
using Compendio.Hosting.Configuration;
using Compendio.Infrastructure.GitMirror;
using Compendio.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace Compendio.Tests.Integration;

/// <summary>
/// Criterion 11: the mirror pushes secure pages as ciphertext, and degrades when git is absent.
/// </summary>
/// <remarks>
/// <para>
/// Against a real bare repository and the real <c>git</c> binary, because the thing being asserted
/// is what ends up on somebody else's disk. A faked git would assert that the code calls the
/// commands we thought it should, which is a different and much weaker claim.
/// </para>
/// <para>
/// The runner is built by hand rather than resolved, so its options can point at a throwaway remote
/// without turning the mirror on for the rest of the suite.
/// </para>
/// </remarks>
[Collection(nameof(CompendioCollection))]
public sealed class GitMirrorTests(CompendioApplication app) : IAsyncLifetime
{
    private const string SecureFolder = "GitVault";
    private const string SecurePage = "GitVault/Wifi.md";
    private const string Plaintext = "correct-horse-battery-staple";

    private static JsonSerializerOptions Json => CompendioApplication.Json;

    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient _admin = null!;

    public async ValueTask InitializeAsync() => _admin = await app.SignInAsAdminAsync();

    public ValueTask DisposeAsync()
    {
        _admin.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ACloneOfTheRemoteHoldsCiphertextAndNoneOfThePlaintext()
    {
        await SeedSecurePageAsync();

        var remote = CreateBareRepository();
        var runner = BuildRunner(remote, enabled: true);

        var result = await runner.RunAsync(Ct);
        result.Ok.ShouldBeTrue($"the push should succeed: {result.Message}");

        var clone = Clone(remote);

        // The envelope is there under its own name — files-first is suspended inside a secure scope,
        // and `wifi.md.enc` is exactly what a person cloning this repository should find.
        var files = Directory.GetFiles(clone, "*", SearchOption.AllDirectories)
            .Where(f => !f.Replace('\\', '/').Contains("/.git/", StringComparison.Ordinal))
            .ToArray();

        files.ShouldContain(f => f.EndsWith("Wifi.md.enc", StringComparison.Ordinal));
        files.ShouldNotContain(f => f.EndsWith("Wifi.md", StringComparison.Ordinal));

        // And the secret is nowhere in the bytes of anything that was pushed. A name check alone
        // would pass if the plaintext had also been committed somewhere else in the tree.
        var needle = Encoding.UTF8.GetBytes(Plaintext);

        foreach (var file in files)
        {
            Contains(File.ReadAllBytes(file), needle)
                .ShouldBeFalse($"'{file}' carries the plaintext this scope exists to protect");
        }
    }

    /// <summary>A second run with nothing changed is a skip, not a commit and not a failure.</summary>
    [Fact]
    public async Task AnUnchangedContentFolderIsSkippedRatherThanCommitted()
    {
        await SeedSecurePageAsync();

        var remote = CreateBareRepository();
        var runner = BuildRunner(remote, enabled: true);

        (await runner.RunAsync(Ct)).Ok.ShouldBeTrue();

        var second = await runner.RunAsync(Ct);

        second.Ok.ShouldBeTrue();
        second.Skipped.ShouldBeTrue("committing an empty tree every hour would make the history useless");
    }

    /// <summary>Disabled is the default, and it touches nothing at all.</summary>
    [Fact]
    public async Task ADisabledMirrorDoesNothing()
    {
        var runner = BuildRunner(remote: "http://example.invalid/repo.git", enabled: false);

        var result = await runner.RunAsync(Ct);

        result.Skipped.ShouldBeTrue();
        result.Message.ShouldBe("disabled");
    }

    /// <summary>
    /// An unreachable remote is reported, never thrown, and never takes anything else down.
    /// </summary>
    /// <remarks>
    /// The same shape as git being missing from <c>PATH</c>, which is the case the spec names: the
    /// feature says it cannot run and the rest of the instance is unaffected.
    /// </remarks>
    [Fact]
    public async Task AnUnreachableRemoteIsReportedAndTheInstanceKeepsWorking()
    {
        await SeedSecurePageAsync();

        var runner = BuildRunner(
            remote: Path.Combine(Path.GetTempPath(), $"compendio-nowhere-{Guid.CreateVersion7():N}"),
            enabled: true);

        var result = await runner.RunAsync(Ct);
        result.Ok.ShouldBeFalse();

        // The wiki is still a wiki.
        var page = await _admin.GetAsync($"/api/v1/pages/{SecurePage}", Ct);
        page.StatusCode.ShouldBe(HttpStatusCode.OK);

        var status = await _admin.GetAsync("/api/v1/admin/git-mirror", Ct);
        status.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // ---- fixture ---------------------------------------------------------------------------------

    /// <summary>
    /// A page whose folder is an encrypted scope, so the file on disk is already an envelope.
    /// </summary>
    /// <remarks>
    /// Nothing in the mirror knows about encryption, and that is the design: secure scopes are
    /// <c>.enc</c> on disk, so pushing the folder pushes ciphertext with nothing to remember.
    /// </remarks>
    private async Task SeedSecurePageAsync()
    {
        if (app.FileExists(SecurePage) || app.FileExists(SecurePage + ".enc"))
        {
            return;
        }

        var create = await _admin.PostAsJsonAsync("/api/v1/pages", new
        {
            folderPath = SecureFolder,
            title = "Wifi",
            content = $"The guest wifi password is {Plaintext}.\n",
        }, Json, Ct);

        create.EnsureSuccessStatusCode();

        var scope = await _admin.PostAsJsonAsync("/api/v1/admin/secure-scopes", new
        {
            path = SecureFolder,
            indexContent = false,
            allowAi = false,
        }, Json, Ct);

        // Strict on purpose. Tolerating a failure here would leave the page in plaintext and turn
        // the ciphertext assertions below into assertions about a folder that was never encrypted.
        if (!scope.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Could not make '{SecureFolder}' a secure scope: {(int)scope.StatusCode} " +
                await scope.Content.ReadAsStringAsync(Ct));
        }

        app.FileExists(SecurePage + ".enc")
            .ShouldBeTrue($"'{SecurePage}.enc' should exist on disk after the scope was created. " +
                          $"Content folder holds: {string.Join(", ", ListContent())}");
    }

    private IEnumerable<string> ListContent() =>
        Directory.Exists(Path.Combine(app.ContentRoot, SecureFolder))
            ? Directory.GetFiles(Path.Combine(app.ContentRoot, SecureFolder), "*", SearchOption.AllDirectories)
                .Select(path => Path.GetFileName(path) ?? path)
            : ["<no GitVault folder>"];

    private GitMirrorRunner BuildRunner(string remote, bool enabled)
    {
        var scope = app.Services.CreateScope();

        var options = Options.Create(new CompendioOptions
        {
            DataDir = app.DataDirectory,
            GitMirror = new GitMirrorOptions
            {
                Enabled = enabled,
                RemoteUrl = remote,
                Branch = "main",
                TimeoutSeconds = 60,
            },
        });

        return new GitMirrorRunner(
            new GitCli(NullLogger<GitCli>.Instance),
            scope.ServiceProvider.GetRequiredService<DataDirectory>(),
            scope.ServiceProvider.GetRequiredService<IDbContextFactory<CompendioDbContext>>(),
            scope.ServiceProvider.GetRequiredService<IUserDirectory>(),
            scope.ServiceProvider.GetRequiredService<INotificationWriter>(),
            options,
            scope.ServiceProvider.GetRequiredService<IClock>(),
            NullLogger<GitMirrorRunner>.Instance);
    }

    private static string CreateBareRepository()
    {
        var path = Path.Combine(Path.GetTempPath(), $"compendio-remote-{Guid.CreateVersion7():N}.git");
        Directory.CreateDirectory(path);

        Run(path, "init", "--bare", "--initial-branch", "main");
        return path;
    }

    private static string Clone(string remote)
    {
        var into = Path.Combine(Path.GetTempPath(), $"compendio-clone-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(into);

        Run(into, "clone", remote, ".");
        return into;
    }

    private static void Run(string workingDirectory, params string[] arguments)
    {
        var info = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(info)!;
        process.WaitForExit(60_000);

        process.ExitCode.ShouldBe(0, $"git {string.Join(' ', arguments)} failed: {process.StandardError.ReadToEnd()}");
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return false;
        }

        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;

            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }
}
