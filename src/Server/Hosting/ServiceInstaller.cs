using System.Diagnostics;
using System.Runtime.Versioning;
using Compendio.Domain;

namespace Compendio.Hosting;

/// <summary>
/// <c>compendio install</c> / <c>uninstall</c>.
/// </summary>
/// <remarks>
/// <c>uninstall</c> leaves the data untouched and says so out loud. An uninstaller that deletes
/// somebody's wiki because they wanted to move it to another machine is not a supported outcome.
/// </remarks>
public static class ServiceInstaller
{
    public static int Install()
    {
        if (OperatingSystem.IsWindows())
        {
            return InstallWindows();
        }

        if (OperatingSystem.IsLinux())
        {
            return InstallSystemd();
        }

        Console.Error.WriteLine("Service installation is supported on Windows and Linux. Run Compendio directly, or use the container.");
        return 1;
    }

    public static int Uninstall()
    {
        if (OperatingSystem.IsWindows())
        {
            var result = Run("sc.exe", $"delete {CompendioConstants.ServiceName}");
            Console.WriteLine(result == 0
                ? $"Removed the '{CompendioConstants.ServiceName}' service. Your data directory has not been touched."
                : "Could not remove the service. Run this from an elevated prompt.");
            return result;
        }

        if (OperatingSystem.IsLinux())
        {
            Run("systemctl", $"stop {CompendioConstants.CommandName}");
            Run("systemctl", $"disable {CompendioConstants.CommandName}");

            var unit = $"/etc/systemd/system/{CompendioConstants.CommandName}.service";
            if (File.Exists(unit))
            {
                File.Delete(unit);
            }

            Run("systemctl", "daemon-reload");
            Console.WriteLine("Removed the systemd unit. Your data directory has not been touched.");
            return 0;
        }

        return 1;
    }

    [SupportedOSPlatform("windows")]
    private static int InstallWindows()
    {
        var executable = Environment.ProcessPath;
        if (executable is null)
        {
            Console.Error.WriteLine("Could not determine the path of this executable.");
            return 1;
        }

        // A virtual service account, never LocalSystem: the service needs its own data directory
        // and nothing else on the machine.
        var arguments =
            $"create {CompendioConstants.ServiceName} binPath= \"{executable}\" start= auto " +
            $"obj= \"{CompendioConstants.WindowsServiceAccount}\" DisplayName= \"{CompendioConstants.ProductName}\"";

        if (Run("sc.exe", arguments) != 0)
        {
            Console.Error.WriteLine("Could not create the service. Run this from an elevated prompt.");
            return 1;
        }

        Run("sc.exe", $"description {CompendioConstants.ServiceName} \"A Markdown folder that is the database of record.\"");
        Run("sc.exe", $"failure {CompendioConstants.ServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/30000");
        Run("sc.exe", $"start {CompendioConstants.ServiceName}");

        Console.WriteLine($"""
            Installed '{CompendioConstants.ServiceName}' and started it.

            It runs as {CompendioConstants.WindowsServiceAccount}. Grant that account write access to the
            data directory if it is somewhere other than beside the executable:

              icacls "<data directory>" /grant "{CompendioConstants.WindowsServiceAccount}":(OI)(CI)M
            """);

        return 0;
    }

    [SupportedOSPlatform("linux")]
    private static int InstallSystemd()
    {
        var executable = Environment.ProcessPath;
        if (executable is null)
        {
            Console.Error.WriteLine("Could not determine the path of this executable.");
            return 1;
        }

        var dataDirectory = Path.Combine(Path.GetDirectoryName(executable)!, "data");
        var unitPath = $"/etc/systemd/system/{CompendioConstants.CommandName}.service";

        // Type=notify because AddSystemd() reports readiness, so systemd knows the difference
        // between "the process started" and "it is serving".
        var unit = $"""
            [Unit]
            Description={CompendioConstants.ProductName}
            After=network-online.target
            Wants=network-online.target

            [Service]
            Type=notify
            ExecStart={executable}
            WorkingDirectory={Path.GetDirectoryName(executable)}
            User={CompendioConstants.CommandName}
            Group={CompendioConstants.CommandName}
            Restart=on-failure
            RestartSec=5

            NoNewPrivileges=true
            ProtectSystem=strict
            ProtectHome=true
            PrivateTmp=true
            PrivateDevices=true
            ReadWritePaths={dataDirectory}

            [Install]
            WantedBy=multi-user.target
            """;

        try
        {
            File.WriteAllText(unitPath, unit);
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not write '{unitPath}'. Run this with sudo.");
            return 1;
        }

        Run("useradd", $"--system --no-create-home --shell /usr/sbin/nologin {CompendioConstants.CommandName}");
        Run("chown", $"-R {CompendioConstants.CommandName}:{CompendioConstants.CommandName} {dataDirectory}");
        Run("systemctl", "daemon-reload");
        Run("systemctl", $"enable --now {CompendioConstants.CommandName}");

        Console.WriteLine($"""
            Installed '{unitPath}' and started it.

              systemctl status {CompendioConstants.CommandName}
              journalctl -u {CompendioConstants.CommandName} -f
            """);

        return 0;
    }

    private static int Run(string fileName, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });

            if (process is null)
            {
                return 1;
            }

            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return 1;
        }
    }
}
