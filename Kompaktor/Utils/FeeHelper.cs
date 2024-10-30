using System.Collections.Concurrent;
using NBitcoin;
using WabiSabi.Crypto.ZeroKnowledge;

namespace Kompaktor.Utils;

public static class CredentialHelper
{


    public static byte[] ToBytes(this long val)
    {
        var bytes = BitConverter.GetBytes(val);
        return BitConverter.IsLittleEndian ? bytes : bytes.Reverse().ToArray();
    }
    public static byte[] ToBytes(this Credential credential)
    {
        return credential.Mac.ToBytes().Concat(credential.Randomness.ToBytes()).Concat(credential.Value.ToBytes()).ToArray();
    }
}
public static class FeeHelper
{
    public static ScriptType GetScriptType(this Script script)
    {
        return TryGetScriptType(script) ?? throw new NotImplementedException($"Unsupported script type.");
    }

    public static ScriptType? TryGetScriptType(this Script script)
    {
        foreach (ScriptType scriptType in new ScriptType[]
                     {ScriptType.P2WPKH, ScriptType.P2PKH, ScriptType.P2PK, ScriptType.Taproot})
        {
            if (script.IsScriptType(scriptType))
            {
                return scriptType;
            }
        }

        return null;
    }

    public static Money EstimateFee(this Script scriptPubKey, FeeRate feeRate)
    {
        return feeRate.GetFee(scriptPubKey.EstimateInputVsize());
    }
    
    public static Money EstimateEffectiveValue(this Coin coin, FeeRate feeRate)
    {
        var fee = coin.ScriptPubKey.EstimateFee(feeRate);
        return coin.Amount - fee;
    }
    
    public static Money EffectiveValue(this Coin coin,  TxIn signedTxIn, FeeRate feeRate)
    {
        return coin.Amount - signedTxIn.GetFee(feeRate);
    }
    
    public static int EstimateInputVsize(this Script scriptPubKey) =>
        scriptPubKey.GetScriptType().EstimateInputVsize();

    public static int EstimateInputVsize(this ScriptType scriptType) =>
        scriptType switch
        {
            ScriptType.P2WPKH => Constants.P2wpkhInputVirtualSize,
            ScriptType.Taproot => Constants.P2trInputVirtualSize,
            ScriptType.P2PKH => Constants.P2pkhInputVirtualSize,
            ScriptType.P2SH => Constants.P2shInputVirtualSize,
            ScriptType.P2WSH => Constants.P2wshInputVirtualSize,
            _ => throw new NotImplementedException($"Size estimation isn't implemented for provided script type.")
        };
    
    public static int GetSize(this TxIn signedTxIn)
    {
        // Calculate the non-witness size of the input (scriptSig included)
        var nonWitnessSize = signedTxIn.GetSerializedSize();

        // Check if the input is SegWit by examining the witness data
        var isSegWit = signedTxIn.WitScript != null && signedTxIn.WitScript.PushCount > 0;

        // If it's a SegWit input, include the witness size in the calculation
        var witnessSize = isSegWit ? signedTxIn.WitScript.GetSerializedSize() : 0;

        // Calculate the total weight of the input
        var inputWeight = isSegWit ? nonWitnessSize * 3 + witnessSize : nonWitnessSize * 4;

        // Convert weight to virtual size (vBytes)
        var virtualSize = (inputWeight + 3) / 4; // rounding up to the nearest byte
        return virtualSize;
    }

    public static Money GetFee(this TxIn signedTxIn, FeeRate feeRate)
    {
        

        // Calculate the fee based on the virtual size and FeeRate
        var feePaidForInput = feeRate.GetFee(signedTxIn.GetSize());

        return feePaidForInput;
    }

    public static Money EffectiveValue(this TxOut txOut, FeeRate feeRate)
    {
        return txOut.Value - feeRate.GetFee(txOut.GetSerializedSize());
    }

    public static Money EffectiveCost(this TxOut txOut, FeeRate feeRate)
    {
        return txOut.Value + feeRate.GetFee(txOut.GetSerializedSize());
    }
}