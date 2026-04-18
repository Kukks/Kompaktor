using Kompaktor.Contracts;
using NBitcoin;
using NBitcoin.BIP322;
using Xunit;

namespace Kompaktor.Tests;

public class OwnershipProofVerificationTests
{
    private static OutPoint Op(int id) =>
        new(uint256.Parse($"{id:D64}"), 0);

    private static Coin MakeCoin(int id, long satoshis = 100_000) =>
        new(Op(id), new TxOut(Money.Satoshis(satoshis),
            new Key().PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86)));

    [Fact]
    public async Task VerifyUtxo_DefaultImplementation_ReturnsNull()
    {
        IKompaktorWalletInterface wallet = new LightWallet();
        var coin = MakeCoin(1);
        var result = await wallet.VerifyUtxo(coin.Outpoint, coin.TxOut);
        Assert.Null(result);
    }

    [Fact]
    public async Task VerifyUtxo_FullNode_ReturnsTrue_WhenValid()
    {
        var coin = MakeCoin(1, 50_000);
        var wallet = new FullNodeWallet(new Dictionary<OutPoint, TxOut>
        {
            [coin.Outpoint] = coin.TxOut
        });

        var result = await wallet.VerifyUtxo(coin.Outpoint, coin.TxOut);
        Assert.True(result);
    }

    [Fact]
    public async Task VerifyUtxo_FullNode_ReturnsFalse_WhenMissing()
    {
        var wallet = new FullNodeWallet(new Dictionary<OutPoint, TxOut>());
        var coin = MakeCoin(1);
        var result = await wallet.VerifyUtxo(coin.Outpoint, coin.TxOut);
        Assert.False(result);
    }

    [Fact]
    public async Task VerifyUtxo_FullNode_ReturnsFalse_WhenAmountMismatch()
    {
        var coin = MakeCoin(1, 50_000);
        var wallet = new FullNodeWallet(new Dictionary<OutPoint, TxOut>
        {
            [coin.Outpoint] = new TxOut(Money.Satoshis(99_999), coin.ScriptPubKey)
        });

        var result = await wallet.VerifyUtxo(coin.Outpoint, coin.TxOut);
        Assert.False(result);
    }

    [Fact]
    public async Task VerifyUtxo_FullNode_ReturnsFalse_WhenScriptMismatch()
    {
        var coin = MakeCoin(1, 50_000);
        var differentScript = new Key().PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);
        var wallet = new FullNodeWallet(new Dictionary<OutPoint, TxOut>
        {
            [coin.Outpoint] = new TxOut(coin.Amount, differentScript)
        });

        var result = await wallet.VerifyUtxo(coin.Outpoint, coin.TxOut);
        Assert.False(result);
    }

    [Fact]
    public void VerifyInputUtxos_ExcludesOwnInputs()
    {
        var myInputs = new HashSet<OutPoint> { Op(1), Op(2) };
        var allInputs = new[] { MakeCoin(1), MakeCoin(2), MakeCoin(3), MakeCoin(4) };

        var otherInputs = allInputs.Where(c => !myInputs.Contains(c.Outpoint)).ToList();

        Assert.Equal(2, otherInputs.Count);
        Assert.DoesNotContain(otherInputs, c => c.Outpoint == Op(1));
        Assert.DoesNotContain(otherInputs, c => c.Outpoint == Op(2));
        Assert.Contains(otherInputs, c => c.Outpoint == Op(3));
        Assert.Contains(otherInputs, c => c.Outpoint == Op(4));
    }

    [Fact]
    public void VerifyInputUtxos_NoOtherInputs_Skips()
    {
        var myInputs = new HashSet<OutPoint> { Op(1) };
        var allInputs = new[] { MakeCoin(1) };

        var otherInputs = allInputs.Where(c => !myInputs.Contains(c.Outpoint)).ToList();
        Assert.Empty(otherInputs);
    }

    [Fact]
    public async Task VerifyInputUtxos_AllValid_NoException()
    {
        var coins = new[] { MakeCoin(3), MakeCoin(4) };
        var utxoSet = coins.ToDictionary(c => c.Outpoint, c => c.TxOut);
        var wallet = new FullNodeWallet(utxoSet);

        var invalidInputs = new List<OutPoint>();
        foreach (var coin in coins)
        {
            var result = await wallet.VerifyUtxo(coin.Outpoint, coin.TxOut);
            if (result == false)
                invalidInputs.Add(coin.Outpoint);
        }

        Assert.Empty(invalidInputs);
    }

    [Fact]
    public async Task VerifyInputUtxos_FabricatedInput_Detected()
    {
        var realCoin = MakeCoin(3);
        var fabricated = MakeCoin(4, 999_999);
        var utxoSet = new Dictionary<OutPoint, TxOut>
        {
            [realCoin.Outpoint] = realCoin.TxOut
        };
        var wallet = new FullNodeWallet(utxoSet);

        var invalidInputs = new List<OutPoint>();
        foreach (var coin in new[] { realCoin, fabricated })
        {
            var result = await wallet.VerifyUtxo(coin.Outpoint, coin.TxOut);
            if (result == false)
                invalidInputs.Add(coin.Outpoint);
        }

        Assert.Single(invalidInputs);
        Assert.Equal(fabricated.Outpoint, invalidInputs[0]);
    }

    [Fact]
    public async Task VerifyInputUtxos_NullResult_NotFlaggedAsInvalid()
    {
        IKompaktorWalletInterface wallet = new LightWallet();
        var coin = MakeCoin(1);
        var result = await wallet.VerifyUtxo(coin.Outpoint, coin.TxOut);

        var invalidInputs = new List<OutPoint>();
        if (result == false)
            invalidInputs.Add(coin.Outpoint);

        Assert.Empty(invalidInputs);
    }

    [Fact]
    public async Task VerifyInputUtxos_MixedResults_OnlyFalseCountsAsInvalid()
    {
        var validCoin = MakeCoin(1);
        var invalidCoin = MakeCoin(2);
        var unknownCoin = MakeCoin(3);

        var wallet = new MixedWallet(new Dictionary<OutPoint, bool?>
        {
            [validCoin.Outpoint] = true,
            [invalidCoin.Outpoint] = false,
            [unknownCoin.Outpoint] = null
        });

        var invalidInputs = new List<OutPoint>();
        foreach (var coin in new[] { validCoin, invalidCoin, unknownCoin })
        {
            var result = await wallet.VerifyUtxo(coin.Outpoint, coin.TxOut);
            if (result == false)
                invalidInputs.Add(coin.Outpoint);
        }

        Assert.Single(invalidInputs);
        Assert.Equal(invalidCoin.Outpoint, invalidInputs[0]);
    }

    [Fact]
    public void InputNotValid_ErrorCode_Exists()
    {
        var code = Kompaktor.Errors.KompaktorProtocolErrorCode.InputNotValid;
        Assert.Equal("InputNotValid", code.ToString());
    }

    private class LightWallet : IKompaktorWalletInterface
    {
        public Task<Coin[]> GetCoins(CancellationToken ct = default) => Task.FromResult(Array.Empty<Coin>());
        public Task<BIP322Signature.Full> GenerateOwnershipProof(string message, Coin[] coins, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<WitScript> GenerateWitness(Coin coin, Transaction tx, IEnumerable<Coin> txCoins, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private class FullNodeWallet : IKompaktorWalletInterface
    {
        private readonly Dictionary<OutPoint, TxOut> _utxoSet;
        public FullNodeWallet(Dictionary<OutPoint, TxOut> utxoSet) => _utxoSet = utxoSet;

        public Task<Coin[]> GetCoins(CancellationToken ct = default) => Task.FromResult(Array.Empty<Coin>());
        public Task<BIP322Signature.Full> GenerateOwnershipProof(string message, Coin[] coins, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<WitScript> GenerateWitness(Coin coin, Transaction tx, IEnumerable<Coin> txCoins, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool?> VerifyUtxo(OutPoint outpoint, TxOut expectedTxOut, CancellationToken ct = default)
        {
            if (!_utxoSet.TryGetValue(outpoint, out var actual))
                return Task.FromResult<bool?>(false);
            return Task.FromResult<bool?>(
                actual.Value == expectedTxOut.Value && actual.ScriptPubKey == expectedTxOut.ScriptPubKey);
        }
    }

    private class MixedWallet : IKompaktorWalletInterface
    {
        private readonly Dictionary<OutPoint, bool?> _results;
        public MixedWallet(Dictionary<OutPoint, bool?> results) => _results = results;

        public Task<Coin[]> GetCoins(CancellationToken ct = default) => Task.FromResult(Array.Empty<Coin>());
        public Task<BIP322Signature.Full> GenerateOwnershipProof(string message, Coin[] coins, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<WitScript> GenerateWitness(Coin coin, Transaction tx, IEnumerable<Coin> txCoins, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool?> VerifyUtxo(OutPoint outpoint, TxOut expectedTxOut, CancellationToken ct = default) =>
            Task.FromResult(_results.GetValueOrDefault(outpoint));
    }
}
