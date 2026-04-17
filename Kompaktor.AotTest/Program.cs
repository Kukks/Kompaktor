using System.Text.Json;
using Kompaktor.JsonConverters;
using Kompaktor.Utils;
using NBitcoin.Secp256k1;
using WabiSabi;
using WabiSabi.Crypto;
using WabiSabi.Crypto.Groups;
using WabiSabi.Crypto.ZeroKnowledge;

var options = KompaktorJsonHelper.CreateSerializerOptions();
var failures = 0;

failures += Assert("ScalarVector", () =>
{
    var scalars = new[] { new Scalar(1), new Scalar(42), new Scalar(1000) };
    var vector = ScalarVector.FromScalars(scalars);

    var json = JsonSerializer.Serialize(vector, options);
    var rt = JsonSerializer.Deserialize<ScalarVector>(json, options)!;

    return scalars.Length == rt.Count() && vector.Zip(rt).All(p => p.First == p.Second);
});

failures += Assert("GroupElementVector", () =>
{
    var elements = new[] { Generators.G, Generators.Gg, Generators.Gh };
    var vector = GroupElementVector.FromElements(elements);

    var json = JsonSerializer.Serialize(vector, options);
    var rt = JsonSerializer.Deserialize<GroupElementVector>(json, options)!;

    return elements.Length == rt.Count() && vector.Zip(rt).All(p => p.First == p.Second);
});

failures += Assert("MAC", () =>
{
    var mac = MAC.FromComponents(new Scalar(7), Generators.Gw);

    var json = JsonSerializer.Serialize(mac, options);
    var rt = JsonSerializer.Deserialize<MAC>(json, options)!;

    return mac.T == rt.T && mac.V == rt.V;
});

failures += Assert("CredentialPresentation", () =>
{
    var cp = CredentialPresentation.FromComponents(
        Generators.Ga, Generators.Gx0, Generators.Gx1, Generators.GV, Generators.Gs);

    var json = JsonSerializer.Serialize(cp, options);
    var rt = JsonSerializer.Deserialize<CredentialPresentation>(json, options)!;

    return cp.Ca == rt.Ca && cp.Cx0 == rt.Cx0 && cp.Cx1 == rt.Cx1 && cp.CV == rt.CV && cp.S == rt.S;
});

failures += Assert("IssuanceRequest", () =>
{
    var ir = IssuanceRequest.FromComponents(Generators.Gg, new[] { Generators.Ga, Generators.Gh });

    var json = JsonSerializer.Serialize(ir, options);
    var rt = JsonSerializer.Deserialize<IssuanceRequest>(json, options)!;

    return ir.Ma == rt.Ma
           && ir.BitCommitments.Count() == rt.BitCommitments.Count()
           && ir.BitCommitments.Zip(rt.BitCommitments).All(p => p.First == p.Second);
});

failures += Assert("Proof", () =>
{
    var nonces = GroupElementVector.FromElements(new[] { Generators.G, Generators.Gg });
    var responses = ScalarVector.FromScalars(new[] { new Scalar(5), new Scalar(10) });
    var proof = Proof.FromComponents(nonces, responses);

    var json = JsonSerializer.Serialize(proof, options);
    var rt = JsonSerializer.Deserialize<Proof>(json, options)!;

    return proof.PublicNonces.Zip(rt.PublicNonces).All(p => p.First == p.Second)
           && proof.Responses.Zip(rt.Responses).All(p => p.First == p.Second);
});

failures += Assert("ByteSerialization", () =>
{
    var mac = MAC.FromComponents(new Scalar(99), Generators.G);

    var bytes = KompaktorJsonHelper.SerializeToUtf8Bytes(mac);
    var rt = KompaktorJsonHelper.DeserializeFromBytes<MAC>(bytes)!;

    return mac.T == rt.T && mac.V == rt.V;
});

failures += Assert("MacFromBytes", () =>
{
    var mac = MAC.FromComponents(new Scalar(42), Generators.G);
    var macBytes = mac.ToBytes();

    if (macBytes.Length != 65) return false;

    var rt = CredentialHelper.MacFromBytes(macBytes);
    return mac.T == rt.T && mac.V == rt.V;
});

if (failures > 0)
{
    Console.Error.WriteLine($"FAILED: {failures} test(s) failed");
    return 1;
}

Console.WriteLine("All NativeAOT serialization tests passed");
return 0;

static int Assert(string name, Func<bool> test)
{
    try
    {
        if (test())
        {
            Console.WriteLine($"  PASS: {name}");
            return 0;
        }
        Console.Error.WriteLine($"  FAIL: {name} — assertion failed");
        return 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  FAIL: {name} — {ex.GetType().Name}: {ex.Message}");
        return 1;
    }
}
