using Compendio.Hosting.Configuration;

namespace Compendio.Hosting;

/// <summary>
/// The one place that knows where things live on disk.
/// </summary>
/// <remarks>
/// <c>DataDir</c> is resolved against the binary, not the working directory: a Windows Service and
/// a systemd unit both start with a working directory nobody chose, and "copy one file and run it"
/// has to keep meaning what it says.
/// </remarks>
public sealed class DataDirectory
{
    private DataDirectory(string root, string content, string database, string logs, string keys)
    {
        Root = root;
        Content = content;
        Database = database;
        Logs = logs;
        Keys = keys;
    }

    public string Root { get; }

    public string Content { get; }

    public string Database { get; }

    public string Logs { get; }

    /// <summary>
    /// Holds the master encryption key <em>and</em> the Data Protection key ring. Losing it loses
    /// the secure pages, which is why the container docs tell operators to mount it separately.
    /// </summary>
    public string Keys { get; }

    public string TlsKeys => Path.Combine(Keys, "tls");

    public string MasterKeyFile => Path.Combine(Keys, "master.key");

    public string DataProtectionKeys => Path.Combine(Keys, "dataprotection");

    public string DatabaseFile => Path.Combine(Database, "compendio.db");

    /// <summary>
    /// Where server-triggered backups are written. The CLI still writes wherever <c>--out</c> says;
    /// this is the one location the API can write to without letting a caller choose a path.
    /// </summary>
    public string Backups => Path.Combine(Root, "backups");

    public string LockFile => Path.Combine(Root, "compendio.lock");

    public static DataDirectory Resolve(CompendioOptions options)
    {
        var root = Path.GetFullPath(
            Path.IsPathRooted(options.DataDir)
                ? options.DataDir
                : Path.Combine(AppContext.BaseDirectory, options.DataDir));

        var content = string.IsNullOrWhiteSpace(options.Content.Root)
            ? Path.Combine(root, "content")
            : Path.GetFullPath(Path.IsPathRooted(options.Content.Root)
                ? options.Content.Root
                : Path.Combine(root, options.Content.Root));

        return new DataDirectory(
            root,
            content,
            Path.Combine(root, "db"),
            Path.Combine(root, "logs"),
            Path.Combine(root, "keys"));
    }

    /// <summary>Creates every directory. Idempotent and safe to repeat on every start.</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Content);
        Directory.CreateDirectory(Database);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Backups);
        CreateProtectedDirectory(Keys);
        CreateProtectedDirectory(DataProtectionKeys);
        CreateProtectedDirectory(TlsKeys);
    }

    /// <summary>
    /// Creates a directory that only the service account should read. On Linux that is mode 0700;
    /// on Windows the installer sets the ACL, because inherited ACLs are the norm there and
    /// stripping them from a running service is more likely to lock the product out of its own key.
    /// </summary>
    /// <remarks>
    /// The mode change is best-effort. When <c>keys</c> is a mounted volume the operator owns — the
    /// default for the container, where the host bind-mount is created root-owned — a non-root
    /// process can write inside it but cannot <c>chmod</c> it, and crashing there would take the
    /// whole instance down over a hardening step the operator is already responsible for. The
    /// tightening still applies on the common path where the process owns the directory.
    /// </remarks>
    private static void CreateProtectedDirectory(string path)
    {
        Directory.CreateDirectory(path);

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Console.Error.WriteLine(
                $"warning: could not restrict permissions on '{path}' ({ex.Message}). " +
                "Ensure the directory is not readable by other users.");
        }
    }

    /// <summary>
    /// The SQLite connection string.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately <em>not</em> <c>Cache=Shared</c>. Shared cache moves locking from the file to
    /// the table and returns <c>SQLITE_LOCKED</c>, which the driver's busy-retry does not handle
    /// the way it handles <c>SQLITE_BUSY</c> — so a writer colliding with the file watcher's writer
    /// fails immediately instead of waiting. With WAL and a private cache, a reader never blocks a
    /// writer and two writers queue. This surfaced as an intermittent 500 in the test suite, which
    /// is the only reason it was found before somebody hit it on a busy instance.
    /// </para>
    /// <para>
    /// <c>Foreign Keys=True</c> belongs in the connection string rather than in a startup
    /// <c>PRAGMA</c>: foreign-key enforcement is per connection, and a pool hands out many.
    /// </para>
    /// </remarks>
    public string ConnectionString(DatabaseOptions options) =>
        string.IsNullOrWhiteSpace(options.ConnectionString)
            ? $"Data Source={DatabaseFile};Pooling=True;Foreign Keys=True;Default Timeout=30"
            : options.ConnectionString;
}
