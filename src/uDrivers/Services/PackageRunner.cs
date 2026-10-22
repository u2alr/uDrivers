using System.Diagnostics;

namespace uDrivers.Services;

/// <summary>Runs installers and Windows tools hidden, with a timeout.</summary>
public static class PackageRunner
{
    public static async Task<int> RunAsync(
        string fileName, string arguments, string? workingDirectory, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(fileName) ?? Environment.CurrentDirectory,
        };

        Log.Info($"Run: \"{fileName}\" {arguments} (in {psi.WorkingDirectory})");
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {fileName}.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            if (!ct.IsCancellationRequested)
                throw new TimeoutException($"The installer didn't finish within {timeout.TotalMinutes:0} minutes.");
            throw;
        }

        Log.Info($"Exit code {process.ExitCode}: {Path.GetFileName(fileName)}");
        return process.ExitCode;
    }

    /// <summary>Runs a vendor-supplied command line (which may contain quotes, cmd builtins or .cmd files) via cmd.exe.</summary>
    public static Task<int> RunCommandLineAsync(string commandLine, string workingDirectory, TimeSpan timeout, CancellationToken ct)
    {
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        return RunAsync(cmd, $"/d /c \"{commandLine}\"", workingDirectory, timeout, ct);
    }

    /// <summary>Extracts every file from a .cab with the built-in expand.exe and returns the output folder.</summary>
    public static async Task<string> ExpandCabAsync(string cabPath, CancellationToken ct)
    {
        var outDir = Path.Combine(Path.GetDirectoryName(cabPath)!, Path.GetFileNameWithoutExtension(cabPath) + "_x");
        if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        Directory.CreateDirectory(outDir);

        var expand = Path.Combine(Environment.SystemDirectory, "expand.exe");
        var code = await RunAsync(expand, $"\"{cabPath}\" -F:* \"{outDir}\"", outDir, TimeSpan.FromMinutes(5), ct);
        if (code != 0) throw new InvalidOperationException($"Couldn't unpack {Path.GetFileName(cabPath)} (expand exit {code}).");
        return outDir;
    }

    public static string FreshFolder(params string[] parts)
    {
        var path = Path.Combine([Web.DownloadsFolder, .. parts]);
        if (Directory.Exists(path))
        {
            try { Directory.Delete(path, recursive: true); } catch { /* in use; reuse it */ }
        }
        Directory.CreateDirectory(path);
        return path;
    }
}
