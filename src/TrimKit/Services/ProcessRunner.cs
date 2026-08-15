using System.Diagnostics;

namespace TrimKit.Services;

/// <summary>
/// Shared process runner utility that handles stdout/stderr concurrently
/// to prevent deadlocks, and supports cancellation via CancellationToken.
/// </summary>
public static class ProcessRunner
{
    /// <summary>
    /// Runs dism.exe with the given arguments, reading stdout/stderr concurrently.
    /// Throws InvalidOperationException if the process exits with non-zero code.
    /// </summary>
    public static async Task<string> RunDismAsync(string arguments, CancellationToken cancellationToken = default)
    {
        return await RunProcessAsync("dism.exe", "/English " + arguments, cancellationToken);
    }

    /// <summary>
    /// Runs a PowerShell script non-interactively, reading stdout/stderr concurrently.
    /// Throws InvalidOperationException if the process exits with non-zero code and stderr is non-empty.
    /// </summary>
    public static async Task<string> RunPowerShellAsync(string script, CancellationToken cancellationToken = default)
    {
        var arguments = $"-NoProfile -NonInteractive -Command \"{script.Replace("\"", "\\\"")}\"";
        return await RunProcessAsync("powershell.exe", arguments, cancellationToken, throwOnlyIfStderr: true);
    }

    /// <summary>
    /// Runs an arbitrary process, reading stdout and stderr concurrently to avoid pipe buffer deadlocks.
    /// </summary>
    public static async Task<string> RunProcessAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken = default,
        bool throwOnlyIfStderr = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        // Read stdout and stderr CONCURRENTLY to prevent pipe buffer deadlock
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            if (throwOnlyIfStderr && string.IsNullOrWhiteSpace(error))
                return output;

            var errorMsg = !string.IsNullOrWhiteSpace(error) ? error.Trim() : output.Trim();
            throw new InvalidOperationException(
                $"{fileName} failed (exit code {process.ExitCode}): {errorMsg}");
        }

        return output;
    }
}
