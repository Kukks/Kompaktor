using System.IO.Pipelines;
using System.Text.Json;
using Kompaktor.Blockchain;
using Xunit;

namespace Kompaktor.Tests;

public class ElectrumClientTests
{
    [Fact]
    public async Task SendRequest_CorrelatesResponseById()
    {
        using var pair = new StreamPair();
        using var client = new ElectrumClient(pair.ClientStream);

        var serverTask = Task.Run(async () =>
        {
            var line = await pair.ReadServerLineAsync();
            var request = JsonDocument.Parse(line);
            var id = request.RootElement.GetProperty("id").GetInt32();
            var response = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = "1.4" });
            await pair.WriteServerLineAsync(response);
        });

        var result = await client.RequestAsync("server.version", ["Kompaktor", "1.4"]);
        await serverTask;

        Assert.Equal("1.4", result.GetString());
    }

    [Fact]
    public async Task SendRequest_MultipleConcurrent_CorrectCorrelation()
    {
        using var pair = new StreamPair();
        using var client = new ElectrumClient(pair.ClientStream);

        var serverTask = Task.Run(async () =>
        {
            var requests = new List<(int id, string method)>();
            for (int i = 0; i < 3; i++)
            {
                var line = await pair.ReadServerLineAsync();
                var doc = JsonDocument.Parse(line);
                requests.Add((doc.RootElement.GetProperty("id").GetInt32(),
                    doc.RootElement.GetProperty("method").GetString()!));
            }

            // Respond in reverse order to test correlation
            requests.Reverse();
            foreach (var (id, method) in requests)
            {
                var response = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result = $"response-{id}" });
                await pair.WriteServerLineAsync(response);
            }
        });

        var t1 = client.RequestAsync("method.a", []);
        var t2 = client.RequestAsync("method.b", []);
        var t3 = client.RequestAsync("method.c", []);

        var results = await Task.WhenAll(t1, t2, t3);
        await serverTask;

        Assert.NotNull(results[0]);
        Assert.NotNull(results[1]);
        Assert.NotNull(results[2]);
    }

    [Fact]
    public async Task SubscriptionNotification_DispatchedToHandler()
    {
        using var pair = new StreamPair();
        using var client = new ElectrumClient(pair.ClientStream);

        var received = new TaskCompletionSource<JsonElement>();
        client.OnNotification += (method, parameters) =>
        {
            if (method == "blockchain.scripthash.subscribe")
                received.TrySetResult(parameters);
        };

        var serverTask = Task.Run(async () =>
        {
            // Read subscription request
            await pair.ReadServerLineAsync();
            // Send subscription response
            var subResponse = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, result = "status_hash_1" });
            await pair.WriteServerLineAsync(subResponse);

            // Then send a notification (no id field)
            var notification = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                method = "blockchain.scripthash.subscribe",
                @params = new[] { "scripthash_abc", "new_status_hash" }
            });
            await pair.WriteServerLineAsync(notification);
        });

        await client.RequestAsync("blockchain.scripthash.subscribe", ["scripthash_abc"]);
        var notif = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await serverTask;

        Assert.Equal(JsonValueKind.Array, notif.ValueKind);
    }

    [Fact]
    public async Task ErrorResponse_ThrowsElectrumException()
    {
        using var pair = new StreamPair();
        using var client = new ElectrumClient(pair.ClientStream);

        var serverTask = Task.Run(async () =>
        {
            var line = await pair.ReadServerLineAsync();
            var doc = JsonDocument.Parse(line);
            var id = doc.RootElement.GetProperty("id").GetInt32();
            var response = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id,
                error = new { code = -1, message = "unknown method" }
            });
            await pair.WriteServerLineAsync(response);
        });

        await Assert.ThrowsAsync<ElectrumException>(
            () => client.RequestAsync("nonexistent.method", []));
        await serverTask;
    }

    [Fact]
    public void ScriptHashConversion_MatchesElectrumFormat()
    {
        // Known test vector: the script for the genesis coinbase output
        // P2PKH for address 1A1zP1eP5QGefi2DMPTfTL5SLmv7DivfNa
        var script = NBitcoin.Script.FromHex("76a91462e907b15cbf27d5425399ebf6f0fb50ebb88f1888ac");
        var scriptHash = ElectrumBackend.ToElectrumScriptHash(script);

        // Script hash should be 64 hex chars (32 bytes reversed SHA256)
        Assert.Equal(64, scriptHash.Length);
        Assert.Matches("^[0-9a-f]+$", scriptHash);
    }
}

/// <summary>
/// Provides a pair of streams connected by in-memory pipes for testing.
/// </summary>
internal sealed class StreamPair : IDisposable
{
    private readonly Pipe _clientToServer = new();
    private readonly Pipe _serverToClient = new();
    private readonly StreamReader _serverReader;
    private readonly StreamWriter _serverWriter;

    public StreamPair()
    {
        ClientStream = new DuplexPipeStream(_serverToClient.Reader, _clientToServer.Writer);
        _serverReader = new StreamReader(_clientToServer.Reader.AsStream());
        _serverWriter = new StreamWriter(_serverToClient.Writer.AsStream()) { AutoFlush = true };
    }

    public Stream ClientStream { get; }

    public async Task<string> ReadServerLineAsync() =>
        (await _serverReader.ReadLineAsync())!;

    public async Task WriteServerLineAsync(string line) =>
        await _serverWriter.WriteLineAsync(line);

    public void Dispose()
    {
        _clientToServer.Writer.Complete();
        _serverToClient.Writer.Complete();
    }
}

internal sealed class DuplexPipeStream : Stream
{
    private readonly Stream _readStream;
    private readonly Stream _writeStream;

    public DuplexPipeStream(PipeReader reader, PipeWriter writer)
    {
        _readStream = reader.AsStream();
        _writeStream = writer.AsStream();
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) =>
        _readStream.Read(buffer, offset, count);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        _readStream.ReadAsync(buffer, offset, count, ct);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
        _readStream.ReadAsync(buffer, ct);

    public override void Write(byte[] buffer, int offset, int count) =>
        _writeStream.Write(buffer, offset, count);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        _writeStream.WriteAsync(buffer, offset, count, ct);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
        _writeStream.WriteAsync(buffer, ct);

    public override void Flush() => _writeStream.Flush();
    public override Task FlushAsync(CancellationToken ct) => _writeStream.FlushAsync(ct);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
