using System.Diagnostics;
using System.Text;

namespace Compendio.Infrastructure.GitMirror;

/// <param name="ExitCode">Zero is success. Any other value is reported, never guessed at.</param>
public sealed record GitResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Ok => ExitCode == 0;

    /// <summary>What <c>doctor</c> and the admin screen show. Trimmed, and never the remote URL.</summary>
    public string Message =>
        (StandardError.Length > 0 ? StandardError : StandardOutput).Trim() is { Length: > 0 } text
            ? text
            : $"git exited with code {ExitCode}";
}

/// <summary>
/// Runs the <c>git</c> binary as a subprocess.
/// </summary>
/// <remarks>
/// <para>
/// Shelling out rather than linking libgit2, which v0 rejected for the content folder because a
/// native dependency fights all three deployment modes. That reasoning does not apply here: the
/// mirror is optional, and a team that wants it already has git installed.
/// </para>
/// <para>
/// Arguments are passed as a list and never as a shell string. A remote URL, a branch name or a
/// commit message assembled into a command line is a command-injection bug waiting for a repository
/// named <c>; rm -rf</c>, and <see cref="ProcessStartInfo.ArgumentList"/> removes the possibility
/// rather than escaping it.
/// </para>
/// <para>
/// Every invocation runs with prompting disabled. A push that stops on a credential prompt would
/// hang a background service forever with nothing in the log to explain it.
/// </para>
/// </remarks>
public sealed class GitCli(ILogger<GitCli> logger)
{
    /// <summary>Whether <c>git</c> is on <c>PATH</c> at all.</summary>
    /// <remarks>
    /// Not cached across the process lifetime by accident: an admin installing git should not have
    /// to restart the service, and the check is one process spawn on a schedule measured in hours.
    /// </remarks>
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await RunAsync(Environment.CurrentDirectory, TimeSpan.FromSeconds(10), cancellationToken, "--version");
            return result.Ok;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogDebug(e, "git is not available on PATH.");
            return false;
        }
    }

    public async Task<GitResult> RunAsync(
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
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
            startInfo.ArgumentList.Add(argument);
        }

        // Non-interactive, in the three places git can decide to ask a human something.
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_ASKPASS"] = "echo";
        startInfo.Environment["SSH_ASKPASS"] = "echo";
        startInfo.Environment["GCM_INTERACTIVE"] = "never";

        using var process = new Process { StartInfo = startInfo };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { stdout.AppendLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stderr.AppendLine(e.Data); } };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return new GitResult(-1, stdout.ToString(), $"git timed out after {timeout.TotalSeconds:0} s");
        }

        return new GitResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e)
        {
            // Already gone, or not ours to kill. Either way there is nothing further to do and
            // throwing here would replace a timeout with a less informative failure.
            logger.LogDebug(e, "Could not kill the timed-out git process.");
        }
    }
}
