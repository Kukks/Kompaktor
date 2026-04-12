using System.Text.Json.Serialization;
using Kompaktor.JsonConverters;
using NBitcoin.Secp256k1;

namespace Kompaktor.Models;

/// <summary>
/// Emitted by the coordinator before the signing phase begins.
/// Contains a Schnorr signature (BIP 340) over the transcript hash,
/// which is the SHA256 of all event IDs up to this point.
/// Clients can verify this signature against the coordinator's public key
/// to detect equivocation (different transcripts shown to different clients).
/// Two clients with different signed transcripts for the same round have
/// non-repudiable proof that the coordinator lied.
/// </summary>
public record KompaktorRoundEventTranscriptSigned : KompaktorRoundEvent
{
    public KompaktorRoundEventTranscriptSigned(byte[] transcriptHash, byte[] coordinatorSignature, byte[] coordinatorPubKey)
    {
        TranscriptHash = transcriptHash;
        CoordinatorSignature = coordinatorSignature;
        CoordinatorPubKey = coordinatorPubKey;
    }

    /// <summary>SHA256 hash of the concatenated event IDs forming the round transcript.</summary>
    [JsonPropertyName("transcriptHash")]
    public byte[] TranscriptHash { get; init; }

    /// <summary>BIP 340 Schnorr signature over the transcript hash by the coordinator's key.</summary>
    [JsonPropertyName("coordinatorSignature")]
    public byte[] CoordinatorSignature { get; init; }

    /// <summary>The coordinator's x-only public key used for signing.</summary>
    [JsonPropertyName("coordinatorPubKey")]
    public byte[] CoordinatorPubKey { get; init; }

    public override string ToString() => "Transcript Signed";
}
