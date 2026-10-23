using System.Diagnostics;
using OpenMount.Core.Engine;

namespace OpenMount.Rclone;

/// <summary>A running rclone child process with a controlled environment, redirected stderr and a redacted tail buffer.</summary>
public sealed class RcloneProcess : IDisposable
{
    private readonly Process _process;
    private readonly IChildProcessJob? _job;
    private readonly Queue<string> _tail = new();
    private readonly object _tailGate = new();
    private readonly Func<string, string> _redact;
    private readonly Action<string>? _onLine;

    private RcloneProcess(Process process, IChildProcessJob? job, Func<string, string> redact, Action<string>? onLine)
    {
        _process = process;
        _job = job;
        _redact = redact;
        _onLine = onLine;
    }

    public int Id => _process.Id;
    public bool HasExited { get { try { return _process.HasExited; } catch { return true; } } }
    public int ExitCode { get { try { return _process.ExitCode; } catch { return -1; } } }
    public event EventHandler? Exited;

    public static RcloneProcess Start(
        string exe,
        IReadOnlyList<string> args,
        IDictionary<string, string> environment,
        string workingDirectory,
        IChildProcessJob? job,
        Func<string, string> redact,
        Action<string>? onLine)
    {
        Directory.CreateDirectory(workingDirectory);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment.Clear();
        foreach (var kv in environment) psi.Environment[kv.Key] = kv.Value;

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var wrapper = new RcloneProcess(process, job, redact, onLine);
        process.Exited += (_, _) => wrapper.Exited?.Invoke(wrapper, EventArgs.Empty);
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) wrapper.OnStderr(e.Data); };
        process.OutputDataReceived += (_, _) => { };
        if (!process.Start()) throw new InvalidOperationException("rclone failed to start");
        try { job?.Attach(process); }
        catch (Exception ex) { wrapper.OnStderr("openmount: job object attach failed: " + ex.Message); }
        try { process.StandardInput.Close(); } catch { }
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        return wrapper;
    }

    private void OnStderr(string line)
    {
        var redacted = _redact(line);
        lock (_tailGate)
        {
            _tail.Enqueue(redacted);
            while (_tail.Count > 50) _tail.Dequeue();
        }
        try { _onLine?.Invoke(redacted); } catch { }
    }

    public IReadOnlyList<string> Tail
    {
        get { lock (_tailGate) return _tail.ToArray(); }
    }

    public async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await _process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return HasExited;
        }
    }

    public void Kill()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch { }
    }

    public void Dispose()
    {
        try { _process.Dispose(); } catch { }
        _job?.Dispose();
    }
}
