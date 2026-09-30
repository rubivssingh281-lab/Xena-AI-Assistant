using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Xena.Desktop;

/// <summary>
/// Connection to Xena's Python backend (xena_bridge.py) over a local socket, one
/// JSON object per line. Several requests can be in flight at once: replies are
/// matched to requests by id, so a quick action is never stuck behind a long reply.
/// </summary>
internal sealed class PythonBridge : IDisposable
{
    private sealed class Pending
    {
        public readonly TaskCompletionSource<string> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action<JsonElement>? OnPartial;
        public SynchronizationContext? Context;
    }

    private readonly Process _process;
    private readonly System.Net.Sockets.TcpClient _client;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, Pending> _pending = new();
    private readonly ConcurrentQueue<string> _stderrTail = new();
    private int _requestId;
    private volatile bool _disposed;

    /// <summary>Raised (on a background thread) when the backend stops unexpectedly.</summary>
    public event Action? Disconnected;
    public bool IsAlive { get; private set; } = true;
    public int ProcessId => _process.Id;

    /// <summary>The backend's last few error-output lines (to explain a crash).</summary>
    public string LastErrors => string.Join("\n", _stderrTail);

    public PythonBridge()
    {
        var workspace = FindWorkspaceRoot();
        var venvPython = Path.Combine(workspace, ".venv", "Scripts", "python.exe");
        var python = File.Exists(venvPython) ? venvPython : "python";
        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = python,
                Arguments = $"-u \"{Path.Combine(workspace, "xena_bridge.py")}\"",
                WorkingDirectory = workspace,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        _process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            _stderrTail.Enqueue(e.Data);
            while (_stderrTail.Count > 12) _stderrTail.TryDequeue(out string? _);
        };
        _process.Start();
        _process.BeginErrorReadLine();

        string? portLine;
        while (true)
        {
            portLine = _process.StandardOutput.ReadLine();
            if (portLine == null) throw new InvalidOperationException("Python bridge exited before printing port. " + LastErrors);
            if (portLine.StartsWith("PORT:")) break;
        }
        var port = int.Parse(portLine.Substring(5));

        Task.Run(() =>
        {
            while (_process.StandardOutput.ReadLine() != null) { }
        });

        _client = new System.Net.Sockets.TcpClient();
        _client.Connect("127.0.0.1", port);
        var stream = _client.GetStream();
        // UTF-8 WITHOUT a BOM: a leading BOM byte would corrupt the first JSON
        // line the Python bridge parses.
        var utf8NoBom = new UTF8Encoding(false);
        _reader = new StreamReader(stream, utf8NoBom);
        _writer = new StreamWriter(stream, utf8NoBom) { AutoFlush = true };
        _ = Task.Run(ReadLoopAsync);
    }

    /// <summary>
    /// Send one action and await its final result. While the backend is still
    /// working it may stream "partial" updates (text chunks or snapshots); each is
    /// passed to <paramref name="onPartial"/> on the caller's thread, in order.
    /// Cancelling <paramref name="cancel"/> asks the backend to stop: a request that
    /// hadn't started throws <see cref="OperationCanceledException"/>; one that had
    /// returns whatever it produced so far.
    /// </summary>
    public async Task<string> SendAsync(string action, Dictionary<string, string>? args = null,
                                        Action<JsonElement>? onPartial = null,
                                        CancellationToken cancel = default)
    {
        if (!IsAlive) throw new InvalidOperationException("Xena's backend isn't running.");
        var id = Interlocked.Increment(ref _requestId);
        var pending = new Pending { OnPartial = onPartial, Context = SynchronizationContext.Current };
        _pending[id] = pending;
        try
        {
            await WriteAsync(new { id, action, args = args ?? new() });
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }
        using var registration = cancel.Register(() => _ = CancelAsync(id));
        return await pending.Done.Task;
    }

    /// <summary>Round-trip time to the backend, in milliseconds.</summary>
    public async Task<double> PingAsync()
    {
        var clock = Stopwatch.StartNew();
        await SendAsync("ping");
        return clock.Elapsed.TotalMilliseconds;
    }

    private async Task CancelAsync(int id)
    {
        try
        {
            await WriteAsync(new
            {
                id = Interlocked.Increment(ref _requestId),
                action = "cancel",
                args = new Dictionary<string, string> { ["id"] = id.ToString() },
            });
        }
        catch { /* the backend is gone: nothing left to stop */ }
    }

    private async Task WriteAsync(object message)
    {
        var line = JsonSerializer.Serialize(message);
        await _writeLock.WaitAsync();
        try { await _writer.WriteLineAsync(line); }
        finally { _writeLock.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (true)
            {
                var line = await _reader.ReadLineAsync();
                if (line is null) break;
                JsonElement root;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    root = document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    continue;
                }
                if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.Number) continue;
                var id = idElement.GetInt32();
                if (!_pending.TryGetValue(id, out var pending)) continue;

                if (root.TryGetProperty("partial", out var partial))
                {
                    var handler = pending.OnPartial;
                    if (handler == null) continue;
                    if (pending.Context != null) pending.Context.Post(_ => handler(partial), null);
                    else handler(partial);
                    continue;
                }
                _pending.TryRemove(id, out _);
                if (root.TryGetProperty("error", out var error))
                    pending.Done.TrySetException(new InvalidOperationException(error.GetString()));
                else if (root.TryGetProperty("cancelled", out _))
                    pending.Done.TrySetCanceled();
                else
                    pending.Done.TrySetResult(root.TryGetProperty("result", out var result)
                        ? result.GetString() ?? "Action completed."
                        : "Action completed.");
            }
        }
        catch { /* socket closed */ }

        IsAlive = false;
        foreach (var pending in _pending.Values)
            pending.Done.TrySetException(new InvalidOperationException("Xena's backend stopped."));
        _pending.Clear();
        if (!_disposed) Disconnected?.Invoke();
    }

    public void Dispose()
    {
        _disposed = true;
        try
        {
            _writer?.Dispose();
            _reader?.Dispose();
            _client?.Dispose();
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (Exception) { }
        _process.Dispose();
    }

    private static string FindWorkspaceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Xena.py"))) return directory.FullName;
            directory = directory.Parent;
        }
        return Directory.GetCurrentDirectory();
    }
}
