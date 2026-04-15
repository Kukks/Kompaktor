using System.Security.Cryptography;
using System.Text;
using Kompaktor.Models;
using NBitcoin.Secp256k1;
using Xunit;
using SHA256 = System.Security.Cryptography.SHA256;

namespace Kompaktor.Tests;

public class TranscriptSignatureTests
{
    private static ECPrivKey GenerateKey()
    {
        Span<byte> keyBytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(keyBytes);
        return ECPrivKey.Create(keyBytes);
    }

    private static (byte[] transcriptHash, byte[] signature, byte[] pubKey) SignTranscript(
        ECPrivKey key, string[] eventIds)
    {
        var concatenated = string.Join("", eventIds);
        var transcriptHash = SHA256.HashData(Encoding.UTF8.GetBytes(concatenated));

        var sig = key.SignBIP340(transcriptHash);
        var xPub = key.CreateXOnlyPubKey();

        var pubKeyBytes = new byte[32];
        xPub.WriteToSpan(pubKeyBytes);
        var sigBytes = new byte[64];
        sig.WriteToSpan(sigBytes);

        return (transcriptHash, sigBytes, pubKeyBytes);
    }

    [Fact]
    public void ValidSignature_VerifiesSuccessfully()
    {
        var key = GenerateKey();
        var eventIds = new[] { "evt-1", "evt-2", "evt-3" };
        var (hash, sig, pubKey) = SignTranscript(key, eventIds);

        Assert.True(VerifyBip340(pubKey, hash, sig));
    }

    [Fact]
    public void DifferentTranscript_FailsVerification()
    {
        var key = GenerateKey();
        var (_, sig, pubKey) = SignTranscript(key, ["evt-1", "evt-2"]);

        var tampered = SHA256.HashData(Encoding.UTF8.GetBytes("evt-1evt-2evt-3"));
        Assert.False(VerifyBip340(pubKey, tampered, sig));
    }

    [Fact]
    public void DifferentKey_FailsVerification()
    {
        var key1 = GenerateKey();
        var key2 = GenerateKey();
        var eventIds = new[] { "evt-1", "evt-2" };
        var (hash, sig, _) = SignTranscript(key1, eventIds);

        var pub2 = new byte[32];
        key2.CreateXOnlyPubKey().WriteToSpan(pub2);

        Assert.False(VerifyBip340(pub2, hash, sig));
    }

    [Fact]
    public void TamperedSignature_FailsVerification()
    {
        var key = GenerateKey();
        var (hash, sig, pubKey) = SignTranscript(key, ["evt-1"]);

        sig[0] ^= 0xFF;
        Assert.False(VerifyBip340(pubKey, hash, sig));
    }

    [Fact]
    public void TranscriptHash_IsDeterministic()
    {
        var events = new[] { "evt-a", "evt-b", "evt-c" };
        var hash1 = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("", events)));
        var hash2 = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("", events)));
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void TranscriptHash_OrderMatters()
    {
        var hash1 = SHA256.HashData(Encoding.UTF8.GetBytes("evt-1evt-2"));
        var hash2 = SHA256.HashData(Encoding.UTF8.GetBytes("evt-2evt-1"));
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void TranscriptHash_AdditionalEvent_ChangesHash()
    {
        var hash1 = SHA256.HashData(Encoding.UTF8.GetBytes("evt-1evt-2"));
        var hash2 = SHA256.HashData(Encoding.UTF8.GetBytes("evt-1evt-2evt-3"));
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void TranscriptHash_RemovedEvent_ChangesHash()
    {
        var hash1 = SHA256.HashData(Encoding.UTF8.GetBytes("evt-1evt-2evt-3"));
        var hash2 = SHA256.HashData(Encoding.UTF8.GetBytes("evt-1evt-3"));
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void EquivocationDetection_TwoClientsCanProveInconsistency()
    {
        var key = GenerateKey();
        var transcriptA = new[] { "evt-1", "evt-2", "input-alice" };
        var transcriptB = new[] { "evt-1", "evt-2", "input-bob-different" };

        var (hashA, sigA, pubKeyA) = SignTranscript(key, transcriptA);
        var (hashB, sigB, pubKeyB) = SignTranscript(key, transcriptB);

        Assert.True(VerifyBip340(pubKeyA, hashA, sigA));
        Assert.True(VerifyBip340(pubKeyB, hashB, sigB));
        Assert.NotEqual(hashA, hashB);
        Assert.Equal(pubKeyA, pubKeyB);
    }

    [Fact]
    public void TranscriptSignedEvent_StoresCorrectData()
    {
        var key = GenerateKey();
        var (hash, sig, pubKey) = SignTranscript(key, ["evt-1", "evt-2"]);

        var evt = new KompaktorRoundEventTranscriptSigned(hash, sig, pubKey);

        Assert.Equal(hash, evt.TranscriptHash);
        Assert.Equal(sig, evt.CoordinatorSignature);
        Assert.Equal(pubKey, evt.CoordinatorPubKey);
        Assert.Equal(32, evt.TranscriptHash.Length);
        Assert.Equal(64, evt.CoordinatorSignature.Length);
        Assert.Equal(32, evt.CoordinatorPubKey.Length);
    }

    [Fact]
    public void TranscriptSignedEvent_CanVerifyAfterDeserialization()
    {
        var key = GenerateKey();
        var (hash, sig, pubKey) = SignTranscript(key, ["round-create", "input-1", "input-2"]);

        var evt = new KompaktorRoundEventTranscriptSigned(hash, sig, pubKey);

        Assert.True(VerifyBip340(evt.CoordinatorPubKey, evt.TranscriptHash, evt.CoordinatorSignature));
    }

    [Fact]
    public void EmptyTranscript_CanBeSignedAndVerified()
    {
        var key = GenerateKey();
        var (hash, sig, pubKey) = SignTranscript(key, Array.Empty<string>());

        Assert.True(VerifyBip340(pubKey, hash, sig));
    }

    private static bool VerifyBip340(byte[] xOnlyPubKey, byte[] msgHash, byte[] signature)
    {
        if (!ECXOnlyPubKey.TryCreate(xOnlyPubKey, out var pubKey))
            return false;
        if (!SecpSchnorrSignature.TryCreate(signature, out var sig))
            return false;
        return pubKey.SigVerifyBIP340(sig, msgHash);
    }
}
