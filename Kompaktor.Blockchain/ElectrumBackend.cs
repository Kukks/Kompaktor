using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using NBitcoin;

namespace Kompaktor.Blockchain;

/// <summary>
/// IBlockchainBackend implementation using the Electrum Stratum protocol.
/// Wraps ElectrumClient and translates IBlockchainBackend calls to Stratum JSON-RPC methods.
/// </summary>
public class ElectrumBackend : IBlockchainBackend
{
    private readonly ElectrumOptions _options;
    private ElectrumClient? _client;
    private TcpClient? _tcp;
    private int _latestHeight;
    private readonly ConcurrentDictionary<string, Script> _scriptHashToScript = new();

    public ElectrumBackend(ElectrumOptions options)
    {
        _options = options;
    }

    public bool IsConnected => _client is not null;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(_options.Host, _options.Port, ct);

        Stream stream = _tcp.GetStream();
        if (_options.UseSsl)
        {
            var sslStream = new SslStream(stream, false);
            await sslStream.AuthenticateAsClientAsync(_options.Host);
            stream = sslStream;
        }

        _client = new ElectrumClient(stream);

        // Handshake
        await _client.RequestAsync("server.version", ["Kompaktor", "1.4"], ct);

        // Subscribe to headers for block height tracking
        _client.OnNotification += HandleNotification;
        var headerResult = await _client.RequestAsync("blockchain.headers.subscribe", [], ct);
        if (headerResult.TryGetProperty("height", out var h))
            _latestHeight = h.GetInt32();
    }

    public async Task<UtxoInfo?> GetUtxoAsync(OutPoint outpoint, CancellationToken ct = default)
    {
        var tx = await GetTransactionAsync(outpoint.Hash, ct);
        if (tx is null || outpoint.N >= (uint)tx.Outputs.Count) return null;

        var txOut = tx.Outputs[(int)outpoint.N];
        var scriptHash = ToElectrumScriptHash(txOut.ScriptPubKey);
        var unspent = await _client!.RequestAsync("blockchain.scripthash.listunspent", [scriptHash], ct);

        foreach (var item in unspent.EnumerateArray())
        {
            var txHash = item.GetProperty("tx_hash").GetString();
            var txPos = item.GetProperty("tx_pos").GetInt32();
            if (txHash == outpoint.Hash.ToString() && txPos == (int)outpoint.N)
            {
                var height = item.GetProperty("height").GetInt32();
                var confirmations = height > 0 ? _latestHeight - height + 1 : 0;
                return new UtxoInfo(outpoint, txOut, confirmations, false);
            }
        }

        return null; // Spent or not found
    }

    public async Task<IList<UtxoInfo>> GetUtxosForScriptAsync(Script scriptPubKey, CancellationToken ct = default)
    {
        var scriptHash = ToElectrumScriptHash(scriptPubKey);
        var result = await _client!.RequestAsync("blockchain.scripthash.listunspent", [scriptHash], ct);
        var utxos = new List<UtxoInfo>();

        foreach (var item in result.EnumerateArray())
        {
            var txHash = uint256.Parse(item.GetProperty("tx_hash").GetString()!);
            var txPos = item.GetProperty("tx_pos").GetInt32();
            var value = item.GetProperty("value").GetInt64();
            var height = item.GetProperty("height").GetInt32();
            var confirmations = height > 0 ? _latestHeight - height + 1 : 0;

            utxos.Add(new UtxoInfo(
                new OutPoint(txHash, txPos),
                new TxOut(Money.Satoshis(value), scriptPubKey),
                confirmations,
                false));
        }

        return utxos;
    }

    public async Task<Transaction?> GetTransactionAsync(uint256 txId, CancellationToken ct = default)
    {
        try
        {
            var hex = await _client!.RequestAsync("blockchain.transaction.get", [txId.ToString()], ct);
            return Transaction.Parse(hex.GetString()!, Network.Main); // Raw hex is network-agnostic
        }
        catch (ElectrumException)
        {
            return null;
        }
    }

    public async Task<uint256> BroadcastAsync(Transaction tx, CancellationToken ct = default)
    {
        var result = await _client!.RequestAsync("blockchain.transaction.broadcast", [tx.ToHex()], ct);
        return uint256.Parse(result.GetString()!);
    }

    public async Task SubscribeAddressAsync(Script scriptPubKey, CancellationToken ct = default)
    {
        var scriptHash = ToElectrumScriptHash(scriptPubKey);
        _scriptHashToScript[scriptHash] = scriptPubKey;
        await _client!.RequestAsync("blockchain.scripthash.subscribe", [scriptHash], ct);
    }

    public Task UnsubscribeAddressAsync(Script scriptPubKey, CancellationToken ct = default)
    {
        var scriptHash = ToElectrumScriptHash(scriptPubKey);
        _scriptHashToScript.TryRemove(scriptHash, out _);
        return Task.CompletedTask;
    }

    public event EventHandler<AddressNotification>? AddressNotified;

    public async Task<FeeRate> EstimateFeeAsync(int confirmationTarget = 6, CancellationToken ct = default)
    {
        var result = await _client!.RequestAsync("blockchain.estimatefee", [confirmationTarget], ct);
        var btcPerKb = result.GetDouble();
        if (btcPerKb < 0) btcPerKb = 0.00001; // Fallback minimum
        return new FeeRate(Money.Coins((decimal)btcPerKb));
    }

    public Task<int> GetBlockHeightAsync(CancellationToken ct = default) =>
        Task.FromResult(_latestHeight);

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        _tcp?.Dispose();
    }

    private void HandleNotification(string method, JsonElement parameters)
    {
        if (method == "blockchain.headers.subscribe" && parameters.ValueKind == JsonValueKind.Array)
        {
            var header = parameters[0];
            if (header.TryGetProperty("height", out var h))
                _latestHeight = h.GetInt32();
        }
        else if (method == "blockchain.scripthash.subscribe" && parameters.ValueKind == JsonValueKind.Array)
        {
            var scriptHash = parameters[0].GetString();
            if (scriptHash is not null && _scriptHashToScript.TryGetValue(scriptHash, out var script))
            {
                AddressNotified?.Invoke(this, new AddressNotification(script, uint256.Zero, null));
            }
        }
    }

    public static string ToElectrumScriptHash(Script script)
    {
        var hash = SHA256.HashData(script.ToBytes());
        Array.Reverse(hash);
        return Convert.ToHexString(hash).ToLower();
    }
}
