using System.Collections.Concurrent;
using Compendio.Application.Abstractions;
using Compendio.Domain.Content;
using Compendio.Hosting;
using Compendio.Hosting.Configuration;
using Microsoft.Extensions.Options;

namespace Compendio.Engine;

/// <summary>
/// Watches the content folder — as an optimization, never as the source of truth.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FileSystemWatcher"/> drops events under load, fires several per save, and behaves
/// differently on shares; antivirus makes all three worse. Everything here is therefore best-effort
/// and a full reconciliation pass must always be able to fix any drift — which is why
/// <see cref="Reconciler"/> runs on startup as well as on demand.
/// </para>
/// <para>
/// Polling is not a degraded mode, it is the correct mode on a network path, and it is selected
/// automatically. The cost is latency; the alternative is silently missing changes.
/// </para>
/// </remarks>
public sealed class ContentWatcher(
    IServiceScopeFactory scopeFactory,
    DataDirectory dataDirectory,
    StartupGuards guards,
    IPathPolicy paths,
    IClock clock,
    IOptions<CompendioOptions> options,
    ILogger<ContentWatcher> logger) : BackgroundService
{
    private readonly ContentOptions _content = options.Value.Content;

    /// <summary>Pending paths and when they were last touched. One save fires several events.</summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _pending = new(StringComparer.Ordinal);

    /// <summary>
    /// Deletes waiting to be correlated with a create, by content hash. A move seen as
    /// delete-then-create silently drops the page's identity, history, ACL and search rows.
    /// </summary>
    private readonly ConcurrentDictionary<string, (ContentPath Path, DateTimeOffset At)> _recentDeletes = new(StringComparer.Ordinal);

    /// <summary>Directory renames, which carry their whole subtree — pages, history, ACL and keys.</summary>
    private readonly ConcurrentQueue<(string From, string To)> _renamedFolders = new();

    private FileSystemWatcher? _watcher;
    private bool _polling;
    private bool _pollPrimed;
    private DateTimeOffset _lastPoll = DateTimeOffset.MinValue;
    private Dictionary<string, (long Size, DateTimeOffset Modified)> _pollState = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _polling = guards.ShouldUsePolling();

        if (_polling)
        {
            logger.LogInformation(
                "Watching '{Path}' by polling every {Seconds} s (network path or Content:WatcherMode=Poll).",
                dataDirectory.Content, _content.PollSeconds);
        }
        else
        {
            StartNativeWatcher();
        }

        var debounce = TimeSpan.FromMilliseconds(_content.DebounceMilliseconds);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(debounce, stoppingToken);

                // Before the file drain: the pages inside a renamed folder are at their new paths
                // already, and handling the folder first means the drain finds them there.
                await DrainRenamedFoldersAsync(stoppingToken);

                await DrainAsync(debounce, stoppingToken);

                // Outside DrainAsync on purpose. A delete is held back waiting for the create half
                // of a move, and the only thing that can turn it into a real delete is this sweep —
                // so it has to run on every tick, not only on ticks that had other work. Inside the
                // drain it was unreachable the moment the queue emptied, which is exactly what
                // happens after a single file is deleted and nothing else changes.
                await ExpireCorrelationWindowAsync(stoppingToken);

                if (_polling)
                {
                    await PollAsync(stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        finally
        {
            _watcher?.Dispose();
        }
    }

    private void StartNativeWatcher()
    {
        try
        {
            _watcher = new FileSystemWatcher(dataDirectory.Content)
            {
                IncludeSubdirectories = true,
                // Size is deliberately absent: it fires mid-write, before the file is complete.
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                InternalBufferSize = 64 * 1024,
            };

            _watcher.Created += (_, e) => Enqueue(e.FullPath);
            _watcher.Changed += (_, e) => Enqueue(e.FullPath);
            _watcher.Deleted += (_, e) => Enqueue(e.FullPath);
            _watcher.Renamed += (_, e) =>
            {
                // A renamed directory is the one case the file-level correlation cannot see: the
                // children usually fire no events of their own, so without this the folder's access
                // rules are tombstoned at the old path and the new path inherits — a restricted
                // folder becoming readable by renaming it in Explorer.
                if (Directory.Exists(e.FullPath))
                {
                    _renamedFolders.Enqueue((e.OldFullPath, e.FullPath));
                    return;
                }

                Enqueue(e.OldFullPath);
                Enqueue(e.FullPath);
            };

            _watcher.Error += (_, e) =>
            {
                // Buffer overflow means events were dropped. Polling from here on is the only
                // honest response; a reconciliation pass repairs whatever was missed.
                logger.LogWarning(e.GetException(),
                    "The file watcher reported an error. Switching to polling every {Seconds} s.", _content.PollSeconds);
                _polling = true;
            };

            _watcher.EnableRaisingEvents = true;
            logger.LogInformation("Watching '{Path}' natively.", dataDirectory.Content);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.LogWarning(e, "Could not start the native file watcher. Falling back to polling.");
            _polling = true;
        }
    }

    private void Enqueue(string fullPath)
    {
        if (paths.TryMap(fullPath, PathKind.Any, out var path) && !PathPolicy.IsIgnored(path.Value))
        {
            _pending[path.Value.Value] = clock.UtcNow;
        }
    }

    /// <summary>
    /// Applies directory renames, moving each folder's rows rather than rebuilding them.
    /// </summary>
    /// <remarks>
    /// A rename that is not recognized as one costs the folder its access rules and every page in it
    /// its history. Both halves have to be inside the content root and the old one has to be a
    /// folder we know about; anything else is left to the reconciliation pass, which correlates by
    /// content and is the slower, surer version of the same thing.
    /// </remarks>
    private async Task DrainRenamedFoldersAsync(CancellationToken cancellationToken)
    {
        if (_renamedFolders.IsEmpty)
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var pipeline = scope.ServiceProvider.GetRequiredService<IContentPipeline>();

        while (_renamedFolders.TryDequeue(out var rename))
        {
            if (!paths.TryMap(rename.From, PathKind.Folder, out var from) ||
                !paths.TryMap(rename.To, PathKind.Folder, out var to) ||
                PathPolicy.IsIgnored(from.Value) || PathPolicy.IsIgnored(to.Value) ||
                PathPolicy.IsAssets(from.Value) || PathPolicy.IsAssets(to.Value))
            {
                continue;
            }

            try
            {
                await pipeline.IngestFolderMoveAsync(from.Value, to.Value, cancellationToken);
                logger.LogInformation("Applied an external folder rename: {From} → {To}.", from.Value.Value, to.Value.Value);
            }
            catch (Exception e)
            {
                // Reconciliation correlates the same rename by content, so this is recoverable.
                logger.LogWarning(e, "Could not apply the folder rename '{From}' → '{To}'.", rename.From, rename.To);
            }
        }
    }

    /// <summary>Processes paths that have been quiet for one debounce interval.</summary>
    private async Task DrainAsync(TimeSpan debounce, CancellationToken cancellationToken)
    {
        if (_pending.IsEmpty)
        {
            return;
        }

        var now = clock.UtcNow;
        var ready = _pending
            .Where(kv => now - kv.Value >= debounce)
            .Select(kv => kv.Key)
            .Order(StringComparer.Ordinal)
            .ToList();

        if (ready.Count == 0)
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var pipeline = scope.ServiceProvider.GetRequiredService<IContentPipeline>();
        var store = scope.ServiceProvider.GetRequiredService<IContentStore>();

        foreach (var key in ready)
        {
            _pending.TryRemove(key, out _);
            var path = ContentPath.FromTrusted(key);

            try
            {
                if (store.Exists(path))
                {
                    await HandleAppearanceAsync(pipeline, store, path, cancellationToken);
                }
                else if (!store.FolderExists(path))
                {
                    await HandleDisappearanceAsync(pipeline, store, path, cancellationToken);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Almost always a file still being written, or antivirus holding a handle.
                // Re-queue once; reconciliation catches anything that keeps failing.
                logger.LogDebug("Deferring '{Path}': {Message}", key, e.Message);
                _pending.TryAdd(key, clock.UtcNow);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to process a file-system change for '{Path}'.", key);
            }
        }
    }

    /// <summary>
    /// A file appeared. If a delete of identical content happened moments ago, this is a move.
    /// </summary>
    private async Task HandleAppearanceAsync(
        IContentPipeline pipeline,
        IContentStore store,
        ContentPath path,
        CancellationToken cancellationToken)
    {
        var hash = await store.HashAsync(path, cancellationToken);

        if (hash is not null && _recentDeletes.TryRemove(hash, out var deleted))
        {
            await pipeline.IngestMoveAsync(deleted.Path, path, cancellationToken);
            return;
        }

        await pipeline.IngestChangeAsync(path, cancellationToken);
    }

    /// <summary>
    /// A file disappeared. Held briefly against a matching create before being treated as a delete.
    /// </summary>
    private async Task HandleDisappearanceAsync(
        IContentPipeline pipeline,
        IContentStore store,
        ContentPath path,
        CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var known = await KnownHashAsync(scope.ServiceProvider, path, cancellationToken);

        if (known is not null)
        {
            _recentDeletes[known] = (path, clock.UtcNow);

            // Give the create side of a move a chance to arrive. If none does, the sweep below
            // turns it into a real delete.
            return;
        }

        await pipeline.IngestDeleteAsync(path, cancellationToken);
    }

    private async Task ExpireCorrelationWindowAsync(CancellationToken cancellationToken)
    {
        if (_recentDeletes.IsEmpty)
        {
            return;
        }

        var window = TimeSpan.FromSeconds(_content.RenameCorrelationSeconds);
        var now = clock.UtcNow;

        var expired = _recentDeletes
            .Where(entry => now - entry.Value.At > window)
            .ToList();

        if (expired.Count == 0)
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var pipeline = scope.ServiceProvider.GetRequiredService<IContentPipeline>();

        foreach (var (hash, entry) in expired)
        {
            if (!_recentDeletes.TryRemove(hash, out _))
            {
                // A create correlated it into a move between the snapshot and here.
                continue;
            }

            try
            {
                // No create correlated: this really was a delete.
                await pipeline.IngestDeleteAsync(entry.Path, cancellationToken);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to apply a delayed delete for '{Path}'.", entry.Path.Value);
            }
        }
    }

    private static async Task<string?> KnownHashAsync(IServiceProvider services, ContentPath path, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<ICompendioDbContext>();
        var page = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstOrDefaultAsync(db.Pages.Where(p => p.Path == path.Value), cancellationToken);

        return page?.ContentHash;
    }

    /// <summary>
    /// Compares <c>(path, size, mtime)</c> and hashes only the candidates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hashing every file every interval would make a large corpus unusable on a share, which is
    /// exactly where polling is needed.
    /// </para>
    /// <para>
    /// The interval is enforced by a timestamp rather than by sleeping. Sleeping here would block
    /// the debounce loop this method is called from, so a change the poll had already spotted would
    /// wait a whole extra interval before anything looked at it.
    /// </para>
    /// </remarks>
    private async Task PollAsync(CancellationToken cancellationToken)
    {
        if (clock.UtcNow - _lastPoll < TimeSpan.FromSeconds(_content.PollSeconds))
        {
            return;
        }

        _lastPoll = clock.UtcNow;

        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IContentStore>();

        var current = new Dictionary<string, (long Size, DateTimeOffset Modified)>(StringComparer.Ordinal);

        await foreach (var entry in store.EnumerateAsync(ContentPath.Root, cancellationToken))
        {
            if (entry.IsFolder)
            {
                continue;
            }

            current[entry.Path.Value] = (entry.ByteSize, entry.ModifiedAt);
        }

        if (!_pollPrimed)
        {
            // The first pass only learns the current state. Treating everything as changed would
            // re-ingest the whole corpus on every start, immediately after the reconciliation pass
            // that already did exactly that work.
            _pollState = current;
            _pollPrimed = true;
            return;
        }

        foreach (var (path, state) in current)
        {
            if (!_pollState.TryGetValue(path, out var previous) || previous != state)
            {
                _pending[path] = clock.UtcNow.AddMilliseconds(-_content.DebounceMilliseconds);
            }
        }

        foreach (var path in _pollState.Keys.Where(p => !current.ContainsKey(p)))
        {
            _pending[path] = clock.UtcNow.AddMilliseconds(-_content.DebounceMilliseconds);
        }

        _pollState = current;
    }
}
