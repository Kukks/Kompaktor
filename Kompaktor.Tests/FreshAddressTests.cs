using System.Collections.Concurrent;
using Kompaktor.Contracts;
using NBitcoin;
using Xunit;

namespace Kompaktor.Tests;

public class FreshAddressTests
{
    [Fact]
    public void ExposedScripts_CollectedFromAllocatedAndRegistered()
    {
        var key1 = new Key();
        var key2 = new Key();
        var key3 = new Key();
        var script1 = key1.PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);
        var script2 = key2.PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);
        var script3 = key3.PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);

        var allocated = new[] { new TxOut(Money.Satoshis(50_000), script1) };
        var registered = new[] { new TxOut(Money.Satoshis(50_000), script2), new TxOut(Money.Satoshis(30_000), script3) };

        var exposedScripts = allocated.Select(o => o.ScriptPubKey)
            .Concat(registered.Select(o => o.ScriptPubKey))
            .Distinct()
            .ToList();

        Assert.Equal(3, exposedScripts.Count);
        Assert.Contains(script1, exposedScripts);
        Assert.Contains(script2, exposedScripts);
        Assert.Contains(script3, exposedScripts);
    }

    [Fact]
    public void ExposedScripts_DuplicatesDeduped()
    {
        var key = new Key();
        var script = key.PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);

        var allocated = new[] { new TxOut(Money.Satoshis(50_000), script) };
        var registered = new[] { new TxOut(Money.Satoshis(50_000), script) };

        var exposedScripts = allocated.Select(o => o.ScriptPubKey)
            .Concat(registered.Select(o => o.ScriptPubKey))
            .Distinct()
            .ToList();

        Assert.Single(exposedScripts);
    }

    [Fact]
    public void ExposedScripts_Empty_WhenNoOutputs()
    {
        var allocated = Array.Empty<TxOut>();
        var registered = Array.Empty<TxOut>();

        var exposedScripts = allocated.Select(o => o.ScriptPubKey)
            .Concat(registered.Select(o => o.ScriptPubKey))
            .Distinct()
            .ToList();

        Assert.Empty(exposedScripts);
    }

    [Fact]
    public void ExposedScripts_OnlyAllocated_WhenNoRegistered()
    {
        var key = new Key();
        var script = key.PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);

        var allocated = new[] { new TxOut(Money.Satoshis(50_000), script) };
        var registered = Array.Empty<TxOut>();

        var exposedScripts = allocated.Select(o => o.ScriptPubKey)
            .Concat(registered.Select(o => o.ScriptPubKey))
            .Distinct()
            .ToList();

        Assert.Single(exposedScripts);
        Assert.Equal(script, exposedScripts[0]);
    }

    [Fact]
    public void ExposedScripts_OnlyRegistered_WhenNoAllocated()
    {
        var key = new Key();
        var script = key.PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);

        var allocated = Array.Empty<TxOut>();
        var registered = new[] { new TxOut(Money.Satoshis(50_000), script) };

        var exposedScripts = allocated.Select(o => o.ScriptPubKey)
            .Concat(registered.Select(o => o.ScriptPubKey))
            .Distinct()
            .ToList();

        Assert.Single(exposedScripts);
    }

    [Fact]
    public async Task MarkScriptsExposed_DefaultImplementation_DoesNotThrow()
    {
        IKompaktorWalletInterface wallet = new NoopWallet();
        var key = new Key();
        var scripts = new[] { key.PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86) };
        await wallet.MarkScriptsExposed(scripts);
    }

    [Fact]
    public async Task MarkScriptsExposed_TrackingImplementation_RecordsScripts()
    {
        var wallet = new TrackingWallet();
        var key1 = new Key();
        var key2 = new Key();
        var scripts = new[]
        {
            key1.PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86),
            key2.PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86)
        };

        await wallet.MarkScriptsExposed(scripts);

        Assert.Equal(2, wallet.ExposedScripts.Count);
    }

    [Fact]
    public async Task MarkScriptsExposed_MultipleRoundFailures_AccumulatesScripts()
    {
        var wallet = new TrackingWallet();
        var key1 = new Key();
        var key2 = new Key();
        var key3 = new Key();

        await wallet.MarkScriptsExposed(new[] { key1.PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86) });
        await wallet.MarkScriptsExposed(new[]
        {
            key2.PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86),
            key3.PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86)
        });

        Assert.Equal(3, wallet.ExposedScripts.Count);
    }

    [Fact]
    public void ConcurrentDictionary_SimulatesAllocatedOutputs()
    {
        var allocated = new ConcurrentDictionary<TxOut, string?>();
        var key1 = new Key();
        var key2 = new Key();
        var txOut1 = new TxOut(Money.Satoshis(50_000), key1.PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86));
        var txOut2 = new TxOut(Money.Satoshis(30_000), key2.PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86));

        allocated.TryAdd(txOut1, null);
        allocated.TryAdd(txOut2, null);

        var scripts = allocated.Keys.Select(o => o.ScriptPubKey).Distinct().ToList();
        Assert.Equal(2, scripts.Count);
    }

    private class NoopWallet : IKompaktorWalletInterface
    {
        public Task<Coin[]> GetCoins(CancellationToken ct = default) => Task.FromResult(Array.Empty<Coin>());
        public Task<NBitcoin.BIP322.BIP322Signature.Full> GenerateOwnershipProof(string message, Coin[] coins, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<WitScript> GenerateWitness(Coin coin, Transaction tx, IEnumerable<Coin> txCoins, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private class TrackingWallet : IKompaktorWalletInterface
    {
        public List<Script> ExposedScripts { get; } = new();

        public Task<Coin[]> GetCoins(CancellationToken ct = default) => Task.FromResult(Array.Empty<Coin>());
        public Task<NBitcoin.BIP322.BIP322Signature.Full> GenerateOwnershipProof(string message, Coin[] coins, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<WitScript> GenerateWitness(Coin coin, Transaction tx, IEnumerable<Coin> txCoins, CancellationToken ct = default) => throw new NotImplementedException();
        public Task MarkScriptsExposed(IEnumerable<Script> scripts, CancellationToken ct = default)
        {
            ExposedScripts.AddRange(scripts);
            return Task.CompletedTask;
        }
    }
}
