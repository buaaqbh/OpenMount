using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using OpenMount.Core.Engine;

namespace OpenMount.Windows;

/// <summary>Per-drive lock held while a session has the drive connected. Other sessions see "connected elsewhere".</summary>
public sealed class FileSessionLock : ISessionLock
{
    private FileStream? _stream;

    private FileSessionLock(FileStream stream) => _stream = stream;

    public static FileSessionLock? TryAcquire(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
            var payload = JsonSerializer.Serialize(new { sessionId = Process.GetCurrentProcess().SessionId, pid = Environment.ProcessId, since = DateTimeOffset.UtcNow });
            fs.SetLength(0);
            fs.Write(Encoding.UTF8.GetBytes(payload));
            fs.Flush();
            return new FileSessionLock(fs);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
    }
}

public sealed class FileSessionLockFactory : ISessionLockFactory
{
    public ISessionLock? TryAcquire(string lockFilePath) => FileSessionLock.TryAcquire(lockFilePath);
}

/// <summary>One instance per Windows logon session: Local\ mutex + a named pipe that forwards command lines.</summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly bool _owned;
    private CancellationTokenSource? _serverCts;

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, @"Local\OpenMount.Instance", out _owned);
    }

    public bool IsFirstInstance => _owned;

    public static string PipeName
    {
        get
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value ?? "nosid";
            return $"OpenMount.{sid}.{Process.GetCurrentProcess().SessionId}";
        }
    }

    /// <summary>Forward arguments to the running instance. Returns false when nobody answered.</summary>
    public static bool SendToExisting(IEnumerable<string> args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            using var w = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            w.WriteLine(JsonSerializer.Serialize(args.ToArray()));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void StartServer(Action<string[]> onArgs)
    {
        _serverCts = new CancellationTokenSource();
        var ct = _serverCts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(ct);
                    using var r = new StreamReader(server, Encoding.UTF8);
                    var line = await r.ReadLineAsync(ct);
                    if (line is null) continue;
                    var args = JsonSerializer.Deserialize<string[]>(line) ?? Array.Empty<string>();
                    onArgs(args);
                }
                catch (OperationCanceledException) { }
                catch { await Task.Delay(500, ct).ContinueWith(_ => { }); }
            }
        }, ct);
    }

    public void Dispose()
    {
        _serverCts?.Cancel();
        if (_owned) { try { _mutex.ReleaseMutex(); } catch { } }
        _mutex.Dispose();
    }
}
