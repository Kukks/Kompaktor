using Kompaktor.Wallet.Data;

namespace Kompaktor.Scoring;

public record ScoredUtxo(UtxoEntity Utxo, AnonymityScore Score, string[] Labels)
{
    public ScoredUtxo(UtxoEntity utxo, AnonymityScore score) : this(utxo, score, []) { }
}
