using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Compendio.Application.Abstractions;
using Compendio.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Compendio.Tests.Integration;

/// <summary>
/// Two snapshots of the same page at the same moment do not collide.
/// </summary>
/// <remarks>
/// <para>
/// A version's <c>Sequence</c> is allocated by reading the highest one and adding one, and
/// <c>PageVersions(PageId, Sequence)</c> is unique. Read-then-write against a unique index is a race
/// by construction: two writers read the same maximum and the loser's insert violates the
/// constraint, which surfaces as an unhandled <c>DbUpdateException</c> — a 500 on somebody's save.
/// </para>
/// <para>
/// It needs two writers on one page at one moment, which in production is an ordinary Tuesday: a
/// save, the watcher ingesting the file it just wrote, and a reconciliation pass all snapshot the
/// same page. It showed up here as an intermittent 500 in the lifecycle tests, roughly one run in
/// four, which is exactly the shape of a race nobody can reproduce on demand.
/// </para>
/// </remarks>
[Collection(nameof(CompendioCollection))]
public sealed class HistoryConcurrencyTests(CompendioApplication app) : IAsyncLifetime
{
    private const string Path = "Concurrency/racing.md";

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
    public async Task ConcurrentSnapshotsOfOnePageAllSucceedAndGetDistinctSequences()
    {
        var page = await EnsurePageAsync();

        const int writers = 8;

        // Distinct content per writer, so the identical-hash short circuit cannot hide the race by
        // turning most of these into no-ops.
        var attempts = Enumerable.Range(0, writers).Select(i => Task.Run(async () =>
        {
            using var scope = app.Services.CreateScope();
            var history = scope.ServiceProvider.GetRequiredService<IPageHistory>();

            await history.SnapshotAsync(
                page,
                Encoding.UTF8.GetBytes($"---\ntitle: Racing\n---\n\nWriter {i}.\n"),
                VersionSource.Editor,
                authorUserId: null,
                note: $"writer-{i}",
                at: DateTimeOffset.UtcNow,
                Ct);
        }, Ct));

        // The assertion is simply that none of them threw. A unique-constraint violation here is the
        // 500 a user would have seen.
        await Should.NotThrowAsync(() => Task.WhenAll(attempts));

        using var check = app.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<ICompendioDbContext>();

        var sequences = await db.PageVersions
            .AsNoTracking()
            .Where(v => v.PageId == page.Id)
            .Select(v => v.Sequence)
            .ToListAsync(Ct);

        sequences.Distinct().Count().ShouldBe(sequences.Count, "every version needs its own sequence");
        sequences.Count.ShouldBeGreaterThanOrEqualTo(writers, "no writer's history should have been dropped");
    }

    private async Task<Page> EnsurePageAsync()
    {
        if (!app.FileExists(Path))
        {
            var create = await _admin.PostAsJsonAsync("/api/v1/pages", new
            {
                folderPath = "Concurrency",
                title = "Racing",
                content = "Start.\n",
            }, Json, Ct);

            create.EnsureSuccessStatusCode();
        }

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ICompendioDbContext>();

        return await db.Pages.AsNoTracking().FirstAsync(p => p.Path == Path, Ct);
    }
}
