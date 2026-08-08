using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using Compendio.Hosting.Configuration;

namespace Compendio.Hosting;

public enum GuardSeverity
{
    Warning,
    Fatal,
}

/// <param name="Message">
/// Written for a non-developer who has to act on it. Names the account, the port, the path — never
/// just "access denied".
/// </param>
public sealed record GuardFinding(GuardSeverity Severity, string Code, string Message);

/// <summary>
/// The checks that run before anything else, each failing with a message someone can act on.
/// </summary>
/// <remarks>
/// These exist because their failure modes are all silent and all late: SQLite on a network share
/// corrupts weeks later, an unwritable data directory looks like a permissions bug in the UI, and a
/// second instance on the same data directory produces two watchers fighting each other.
/// </remarks>
public sealed class StartupGuards(DataDirectory dataDirectory, CompendioOptions options)
{
    private FileStream? _lock;

    public IReadOnlyList<GuardFinding> Run(int? httpPort)
    {
        var findings = new List<GuardFinding>();

        CheckDatabaseLocation(findings);
        CheckWritable(findings);
        CheckContentLocation(findings);
        CheckSingleInstance(findings);

        if (httpPort is { } port)
        {
            CheckPort(findings, port);
        }

        return findings;
    }

    public bool ShouldUsePolling() =>
        options.Content.WatcherMode switch
        {
            WatcherMode.Poll => true,
            WatcherMode.Native => false,
            _ => NetworkPath.IsNetwork(dataDirectory.Content),
        };

    /// <summary>Releases the single-instance lock. Called on shutdown.</summary>
    public void Release()
    {
        _lock?.Dispose();
        _lock = null;
    }

    /// <summary>
    /// SQLite on a network file system corrupts, and it corrupts long after the decision was made.
    /// Refusing to start is the only honest response.
    /// </summary>
    private void CheckDatabaseLocation(List<GuardFinding> findings)
    {
        var databaseDirectory = dataDirectory.Database;

        if (NetworkPath.IsNetwork(databaseDirectory))
        {
            findings.Add(new GuardFinding(
                GuardSeverity.Fatal,
                "guard.database_on_network",
                $"The database directory '{databaseDirectory}' is on a network file system. " +
                "SQLite's locking does not work reliably there and the database will eventually corrupt. " +
                "Point DataDir at local storage, or set Database:ConnectionString to a local file."));
        }
    }

    private void CheckWritable(List<GuardFinding> findings)
    {
        foreach (var directory in new[] { dataDirectory.Root, dataDirectory.Content, dataDirectory.Database, dataDirectory.Keys })
        {
            var probe = Path.Combine(directory, $".write-probe-{Environment.ProcessId}");
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(probe, [0]);
                File.Delete(probe);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                findings.Add(new GuardFinding(
                    GuardSeverity.Fatal,
                    "guard.not_writable",
                    $"'{directory}' is not writable by {CurrentAccount()}. " +
                    "Grant that account write access to the data directory, or run Compendio as an account that has it."));
            }
        }
    }

    private void CheckContentLocation(List<GuardFinding> findings)
    {
        if (!NetworkPath.IsNetwork(dataDirectory.Content) || options.Content.WatcherMode == WatcherMode.Native)
        {
            return;
        }

        // A warning, not a refusal: keeping content on a share is a legitimate thing an SMB does,
        // and the cost is only that file-change detection becomes a poll.
        findings.Add(new GuardFinding(
            GuardSeverity.Warning,
            "guard.content_on_network",
            $"The content folder '{dataDirectory.Content}' is on a network file system. " +
            $"File-change notifications are unreliable over SMB, so Compendio will poll every {options.Content.PollSeconds} s instead. " +
            "Set Content:WatcherMode=Native to override."));
    }

    /// <summary>
    /// Two instances over one data directory means two watchers, two indexers and two writers.
    /// The lock is an OS file lock, so a hard kill releases it and there is no manual cleanup.
    /// </summary>
    private void CheckSingleInstance(List<GuardFinding> findings)
    {
        try
        {
            _lock = new FileStream(
                dataDirectory.LockFile,
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.Read,
                bufferSize: 1,
                FileOptions.DeleteOnClose);

            var stamp = Encoding.UTF8.GetBytes(string.Create(
                CultureInfo.InvariantCulture,
                $"pid={Environment.ProcessId} machine={Environment.MachineName} started={DateTimeOffset.UtcNow:O}"));

            _lock.Write(stamp);
            _lock.Flush();
        }
        catch (IOException)
        {
            findings.Add(new GuardFinding(
                GuardSeverity.Fatal,
                "guard.already_running",
                $"Another Compendio instance is already using '{dataDirectory.Root}' ({DescribeLockHolder()}). " +
                "Stop it before starting this one, or point this instance at a different DataDir."));
        }
    }

    private string DescribeLockHolder()
    {
        try
        {
            using var stream = new FileStream(dataDirectory.LockFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd().Trim();
            if (text.Length == 0)
            {
                return "holder unknown";
            }

            var pidToken = text.Split(' ').FirstOrDefault(t => t.StartsWith("pid=", StringComparison.Ordinal));
            if (pidToken is not null && int.TryParse(pidToken[4..], out var pid))
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    return $"{text}, process name '{process.ProcessName}'";
                }
                catch (ArgumentException)
                {
                    return $"{text} — that process is gone; the lock will clear on its own";
                }
            }

            return text;
        }
        catch (IOException)
        {
            return "holder unknown";
        }
    }

    private static void CheckPort(List<GuardFinding> findings, int port)
    {
        try
        {
            using var listener = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            listener.DualMode = true;
            listener.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
        }
        catch (SocketException)
        {
            findings.Add(new GuardFinding(
                GuardSeverity.Fatal,
                "guard.port_in_use",
                $"Port {port} is already in use. Stop whatever is listening on it, or set Urls " +
                $"(for example Urls=http://0.0.0.0:8081) to a free port."));
        }
    }

    private static string CurrentAccount() =>
        OperatingSystem.IsWindows()
            ? $"'{Environment.UserDomainName}\\{Environment.UserName}'"
            : $"'{Environment.UserName}'";
}

/// <summary>
/// Whether a path lives on a network file system.
/// </summary>
/// <remarks>
/// Two very different questions on the two platforms: on Windows, a UNC path or a mapped drive; on
/// Linux, an NFS/CIFS/SMB/sshfs mount, which means reading the mount table.
/// </remarks>
public static class NetworkPath
{
    private static readonly string[] NetworkFileSystems =
        ["nfs", "nfs4", "cifs", "smb", "smb2", "smb3", "smbfs", "afs", "fuse.sshfs", "fuse.davfs", "9p", "glusterfs", "ceph"];

    public static bool IsNetwork(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var full = Path.GetFullPath(path);

            if (OperatingSystem.IsWindows())
            {
                return IsWindowsNetwork(full);
            }

            return IsUnixNetwork(full);
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsWindowsNetwork(string full)
    {
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return true;
        }

        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
        {
            return false;
        }

        try
        {
            return new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsUnixNetwork(string full)
    {
        const string mounts = "/proc/self/mounts";
        if (!File.Exists(mounts))
        {
            return false;
        }

        var best = string.Empty;
        var bestType = string.Empty;

        foreach (var line in File.ReadLines(mounts))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3)
            {
                continue;
            }

            var mountPoint = parts[1];

            // Segment-aware: "/data" is not the mount point of "/database", and treating it as one
            // would report a local path as being on a network file system and refuse to start.
            if (full != mountPoint &&
                !full.StartsWith(mountPoint.TrimEnd('/') + "/", StringComparison.Ordinal))
            {
                continue;
            }

            // Longest matching mount point wins; "/" matches everything.
            if (mountPoint.Length >= best.Length)
            {
                best = mountPoint;
                bestType = parts[2];
            }
        }

        return NetworkFileSystems.Contains(bestType, StringComparer.OrdinalIgnoreCase);
    }
}
