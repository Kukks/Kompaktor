using Kompaktor.Wallet.Data;
using Microsoft.EntityFrameworkCore;
using NBitcoin;

namespace Kompaktor.Wallet;

/// <summary>
/// Records completed coinjoin transactions in the wallet database.
/// This data feeds the anonymity scorer for privacy-aware coin selection.
///
/// Call <see cref="RecordRoundAsync"/> after each successful coinjoin round
/// to persist the transaction details and participation records.
/// </summary>
public class CoinJoinRecorder
{
    private readonly WalletDbContext _db;
    private readonly string _walletId;

    public CoinJoinRecorder(WalletDbContext db, string walletId)
    {
        _db = db;
        _walletId = walletId;
    }

    /// <summary>
    /// Records a completed coinjoin round in the wallet database.
    /// Creates the transaction record, coinjoin record, and links our inputs/outputs.
    /// </summary>
    /// <param name="roundId">The round identifier.</param>
    /// <param name="tx">The signed coinjoin transaction.</param>
    /// <param name="ourInputOutpoints">Outpoints of coins we registered as inputs.</param>
    /// <param name="ourOutputScripts">Scripts of outputs we registered.</param>
    /// <param name="totalParticipants">Total number of participants in the round.</param>
    public async Task RecordRoundAsync(
        string roundId,
        Transaction tx,
        OutPoint[] ourInputOutpoints,
        Script[] ourOutputScripts,
        int totalParticipants)
    {
        var txId = tx.GetHash().ToString();

        // Store the transaction if not already present
        if (!await _db.Transactions.AnyAsync(t => t.Id == txId))
        {
            _db.Transactions.Add(new TransactionEntity
            {
                Id = txId,
                RawHex = tx.ToHex()
            });
        }

        // Create the coinjoin record
        var record = new CoinJoinRecordEntity
        {
            TransactionId = txId,
            RoundId = roundId,
            Status = "Completed",
            OurInputCount = ourInputOutpoints.Length,
            TotalInputCount = tx.Inputs.Count,
            OurOutputCount = ourOutputScripts.Length,
            TotalOutputCount = tx.Outputs.Count,
            ParticipantCount = totalParticipants,
            OutputValuesSat = tx.Outputs.Select(o => o.Value.Satoshi).ToArray()
        };

        // Link our input UTXOs
        foreach (var outpoint in ourInputOutpoints)
        {
            var utxo = await _db.Utxos
                .FirstOrDefaultAsync(u =>
                    u.TxId == outpoint.Hash.ToString() &&
                    u.OutputIndex == (int)outpoint.N);

            if (utxo is not null)
            {
                record.Participations.Add(new CoinJoinParticipationEntity
                {
                    UtxoId = utxo.Id,
                    Role = "Input"
                });

                // Mark the input as spent by this coinjoin
                utxo.SpentByTxId = txId;
            }
        }

        // Create output UTXOs and link them
        var walletAddresses = await _db.Addresses
            .Include(a => a.Account)
            .Where(a => a.Account.WalletId == _walletId)
            .ToListAsync();

        for (var i = 0; i < tx.Outputs.Count; i++)
        {
            var output = tx.Outputs[i];
            var scriptBytes = output.ScriptPubKey.ToBytes();

            // Check if this output belongs to our wallet
            if (!ourOutputScripts.Any(s => s == output.ScriptPubKey))
                continue;

            var matchingAddr = walletAddresses.FirstOrDefault(a =>
                a.ScriptPubKey.SequenceEqual(scriptBytes));

            if (matchingAddr is null) continue;

            // Create the new UTXO from this coinjoin output
            var newUtxo = new UtxoEntity
            {
                TxId = txId,
                OutputIndex = i,
                AddressId = matchingAddr.Id,
                AmountSat = output.Value.Satoshi,
                ScriptPubKey = scriptBytes,
                IsCoinJoinOutput = true
                // ConfirmedHeight will be set when the tx confirms
            };
            _db.Utxos.Add(newUtxo);

            record.Participations.Add(new CoinJoinParticipationEntity
            {
                Utxo = newUtxo,
                Role = "Output"
            });
        }

        _db.CoinJoinRecords.Add(record);
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Records a failed round (no transaction was broadcast).
    /// Useful for tracking intersection attack attempts.
    /// </summary>
    public async Task RecordFailedRoundAsync(
        string roundId,
        OutPoint[] ourInputOutpoints)
    {
        var record = new CoinJoinRecordEntity
        {
            RoundId = roundId,
            TransactionId = null, // No tx for failed rounds
            Status = "Failed",
            OurInputCount = ourInputOutpoints.Length,
        };

        _db.CoinJoinRecords.Add(record);
        await _db.SaveChangesAsync();
    }
}
