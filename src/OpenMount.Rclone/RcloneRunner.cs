using System.Diagnostics;
using System.Text;

namespace OpenMount.Rclone;

public sealed record RcloneRunResult(int ExitCode, string Stdout, IReadOnlyList<string> StderrLines, bool TimedOut);

/// <summary>Runs a short-lived rclone command with a controlled environment and captures its output.</summary>
public static class RcloneRunner
{
    public static async Task<RcloneRunResult> RunAsync(
        string exe,
        IReadOnlyList<string> args,
        IDictionary<string, string> environment,
        string workingDirectory,
        TimeSpan timeout,
        Func<string, string> redact,
        CancellationToken ct)
    {
        Directory.CreateDirectory(workingDirectory);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = workingDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment.Clear();
        foreach (var kv in environment) psi.Environment[kv.Key] = kv.Value;

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("cannot start rclone");
        try { process.StandardInput.Close(); } catch { }
        var stderr = new List<string>();
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.Add(redact(e.Data)); };
        process.BeginErrorReadLine();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try { process.Kill(entireProcessTree: true); } catch { }
            if (ct.IsCancellationRequested) throw;
        }
        string stdout;
        try { stdout = await stdoutTask; } catch { stdout = string.Empty; }
        lock (stderr) return new RcloneRunResult(process.HasExited ? process.ExitCode : -1, stdout, stderr.ToArray(), timedOut);
    }
}
