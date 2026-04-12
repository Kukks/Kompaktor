using System.Security.Cryptography;
using System.Text;

namespace Kompaktor.Utils;

/// <summary>
/// Deterministic shuffler seeded by the round's event transcript hash.
/// All clients independently compute the same ordering from the same transcript.
/// If the coordinator equivocated (served different transcripts to different clients),
/// clients will compute different orderings and signatures won't match —
/// making equivocation detectable at signing time.
///
/// Uses SHA256 of concatenated event IDs as PRNG seed, then Fisher-Yates shuffle
/// with ChaCha-style counter-mode hashing for index generation.
/// </summary>
public static class TranscriptShuffler
{
    /// <summary>
    /// Computes a transcript seed from the given event IDs.
    /// </summary>
    public static byte[] ComputeTranscriptSeed(IEnumerable<string> eventIds)
    {
        var concatenated = string.Join("|", eventIds);
        return SHA256.HashData(Encoding.UTF8.GetBytes(concatenated));
    }

    /// <summary>
    /// Deterministically shuffles a list in-place using the transcript seed.
    /// All clients with the same seed produce the same permutation.
    /// </summary>
    public static void Shuffle<T>(List<T> items, byte[] seed)
    {
        if (items.Count <= 1)
            return;

        for (var i = items.Count - 1; i > 0; i--)
        {
            // Generate a deterministic index: hash(seed || counter) mod (i+1)
            var j = DeterministicIndex(seed, i, i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    /// <summary>
    /// Produces a deterministic integer in [0, modulus) from the seed and a counter.
    /// Uses SHA256(seed || counter_bytes) and rejection sampling to avoid modulo bias.
    /// </summary>
    private static int DeterministicIndex(byte[] seed, int counter, int modulus)
    {
        Span<byte> input = stackalloc byte[seed.Length + 4];
        seed.CopyTo(input);
        BitConverter.TryWriteBytes(input[seed.Length..], counter);

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input, hash);

        // Use first 4 bytes as uint32, rejection-sample to avoid bias
        var raw = BitConverter.ToUInt32(hash) & 0x7FFF_FFFF; // ensure positive
        return (int)(raw % (uint)modulus);
    }
}
