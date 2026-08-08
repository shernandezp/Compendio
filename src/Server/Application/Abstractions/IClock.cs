namespace Compendio.Application.Abstractions;

/// <summary>
/// Time, as a dependency.
/// </summary>
/// <remarks>
/// Retention thinning, tombstone expiry, the watcher's rename-correlation window and the loop
/// -prevention window are all time-dependent, and testing them against the wall clock means either
/// sleeping or not testing them.
/// </remarks>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
