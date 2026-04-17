using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Kompaktor.Blockchain;

/// <summary>
/// Low-level Electrum Stratum JSON-RPC client over TCP/SSL.
/// Handles request/response correlation via message IDs and subscription notification dispatch.
/// </summary>
public class ElectrumClient : IDisposable
{
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _nextId;
    private CancellationTokenSource? _readCts;

    public event Action<string, JsonElement>? OnNotification;

    public ElectrumClient(Stream stream)
    {
        _reader = new StreamReader(stream, Encoding.UTF8);
        _writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
        StartReadLoop();
    }

    public async Task<JsonElement> RequestAsync(string method, object[] parameters, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var request = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = parameters
        });

        await _writeLock.WaitAsync(ct);
        try
        {
            await _writer.WriteLineAsync(request);
        }
        finally
        {
            _writeLock.Release();
        }

        using var reg = ct.Register(() => tcs.TrySetCanceled());
        return await tcs.Task;
    }

    private void StartReadLoop()
    {
        _readCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_readCts.Token.IsCancellationRequested)
                {
                    var line = await _reader.ReadLineAsync(_readCts.Token);
                    if (line is null) break;

                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;

                        if (root.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number)
                        {
                            var id = idProp.GetInt32();
                            if (_pending.TryRemove(id, out var tcs))
                            {
                                if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                                    tcs.SetException(new ElectrumException(error.ToString()));
                                else if (root.TryGetProperty("result", out var result))
                                    tcs.SetResult(result.Clone());
                                else
                                    tcs.SetResult(default);
                            }
                        }
                        else if (root.TryGetProperty("method", out var methodProp))
                        {
                            var method = methodProp.GetString()!;
                            var parameters = root.TryGetProperty("params", out var p)
                                ? p.Clone()
                                : default;
                            OnNotification?.Invoke(method, parameters);
                        }
                    }
                    catch (JsonException)
                    {
                        // Skip malformed messages
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        });
    }

    public void Dispose()
    {
        _readCts?.Cancel();
        _readCts?.Dispose();
        _writeLock.Dispose();

        foreach (var kvp in _pending)
            kvp.Value.TrySetCanceled();
        _pending.Clear();
    }
}

public class ElectrumException : Exception
{
    public ElectrumException(string message) : base(message) { }
}
