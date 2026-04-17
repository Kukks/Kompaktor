using System.Text;
using System.Text.Json;
using Kompaktor.JsonConverters;
using Kompaktor.Models;
using Kompaktor.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using NBitcoin.Secp256k1;
using WabiSabi;
using WabiSabi.CredentialRequesting;
using WabiSabi.Crypto;
using WabiSabi.Crypto.Groups;
using WabiSabi.Crypto.ZeroKnowledge;
using Xunit;
using Xunit.Abstractions;

namespace Kompaktor.Tests;

public class JsonTests
{
    private readonly LoggerFactory _loggerFactory;

    public JsonTests(ITestOutputHelper outputHelper)
    {
        _loggerFactory = new LoggerFactory();
        _loggerFactory.AddXUnit(outputHelper);
        _loggerFactory.AddProvider(new ConsoleLoggerProvider(new OptionsMonitor<ConsoleLoggerOptions>(
            new OptionsFactory<ConsoleLoggerOptions>(Array.Empty<IConfigureOptions<ConsoleLoggerOptions>>(),
                Array.Empty<IPostConfigureOptions<ConsoleLoggerOptions>>()),
            new List<IOptionsChangeTokenSource<ConsoleLoggerOptions>>(), new OptionsCache<ConsoleLoggerOptions>())));
    }

    [Fact]
    public void MessageTests()
    {
        var helloWorld = "Hello World"u8.ToArray();
        var msgRequest = new MessageRequest(helloWorld);
        var msgEvent = new KompaktorRoundEventMessage(msgRequest);

        var reqJson = JsonSerializer.Serialize(msgRequest);
        Assert.Equal("{\"message\":\"SGVsbG8gV29ybGQ=\"}", reqJson);
        Assert.True(JsonSerializer.Deserialize<MessageRequest>(reqJson).Message.SequenceEqual(helloWorld));

        var eventJson = JsonSerializer.Serialize(msgEvent);
        var msgEventDeserialized = JsonSerializer.Deserialize<KompaktorRoundEventMessage>(eventJson);
        Assert.True(msgEvent.Request.Message.SequenceEqual(msgEventDeserialized.Request.Message));
        Assert.Equal(msgEvent.Timestamp.ToUnixTimeSeconds(), msgEventDeserialized.Timestamp.ToUnixTimeSeconds());
    }


    [Fact]
    public void ZeroCredentialsRequest_RoundTrip()
    {
        var options = KompaktorJsonHelper.CreateSerializerOptions();

        var issuanceRequest = IssuanceRequest.FromComponents(Generators.Gg, new[] { Generators.Ga, Generators.Gh });
        var proof = Proof.FromComponents(
            GroupElementVector.FromElements(new[] { Generators.G, Generators.Gg }),
            ScalarVector.FromScalars(new[] { new Scalar(5), new Scalar(10) }));
        var request = new ZeroCredentialsRequest(new[] { issuanceRequest }, new[] { proof });

        var json = JsonSerializer.Serialize<ICredentialsRequest>(request, options);
        var deserialized = JsonSerializer.Deserialize<ICredentialsRequest>(json, options)!;

        Assert.Equal(0, deserialized.Delta);
        Assert.Empty(deserialized.Presented);
        Assert.Single(deserialized.Requested);
        Assert.Single(deserialized.Proofs);

        // Verify nested IssuanceRequest
        var rtIr = deserialized.Requested.First();
        Assert.Equal(issuanceRequest.Ma, rtIr.Ma);
        Assert.Equal(issuanceRequest.BitCommitments.Count(), rtIr.BitCommitments.Count());
        foreach (var (orig, rt) in issuanceRequest.BitCommitments.Zip(rtIr.BitCommitments))
            Assert.Equal(orig, rt);

        // Verify nested Proof
        var rtProof = deserialized.Proofs.First();
        foreach (var (orig, rt) in proof.PublicNonces.Zip(rtProof.PublicNonces))
            Assert.Equal(orig, rt);
        foreach (var (orig, rt) in proof.Responses.Zip(rtProof.Responses))
            Assert.Equal(orig, rt);
    }

    [Fact]
    public void RealCredentialsRequest_RoundTrip()
    {
        var options = KompaktorJsonHelper.CreateSerializerOptions();

        var presentation = CredentialPresentation.FromComponents(
            Generators.Ga, Generators.Gx0, Generators.Gx1, Generators.GV, Generators.Gs);
        var issuanceRequest = IssuanceRequest.FromComponents(Generators.Gg, new[] { Generators.Ga });
        var proof = Proof.FromComponents(
            GroupElementVector.FromElements(new[] { Generators.G }),
            ScalarVector.FromScalars(new[] { new Scalar(7) }));
        var request = new RealCredentialsRequest(
            delta: 100_000,
            presented: new[] { presentation },
            requested: new[] { issuanceRequest },
            proofs: new[] { proof });

        var json = JsonSerializer.Serialize<ICredentialsRequest>(request, options);
        var deserialized = JsonSerializer.Deserialize<ICredentialsRequest>(json, options)!;

        Assert.Equal(100_000, deserialized.Delta);
        Assert.Single(deserialized.Presented);
        Assert.Single(deserialized.Requested);
        Assert.Single(deserialized.Proofs);

        // Verify CredentialPresentation survives round-trip
        var rtCp = deserialized.Presented.First();
        Assert.Equal(presentation.Ca, rtCp.Ca);
        Assert.Equal(presentation.Cx0, rtCp.Cx0);
        Assert.Equal(presentation.Cx1, rtCp.Cx1);
        Assert.Equal(presentation.CV, rtCp.CV);
        Assert.Equal(presentation.S, rtCp.S);
    }
}

public class NativeAotJsonTests
{
    private readonly JsonSerializerOptions _options = KompaktorJsonHelper.CreateSerializerOptions();

    [Fact]
    public void ScalarVector_RoundTrip()
    {
        var scalars = new[] { new Scalar(1), new Scalar(42), new Scalar(1000) };
        var vector = ScalarVector.FromScalars(scalars);

        var json = JsonSerializer.Serialize(vector, _options);
        var deserialized = JsonSerializer.Deserialize<ScalarVector>(json, _options)!;

        Assert.Equal(scalars.Length, deserialized.Count());
        foreach (var (original, roundTripped) in vector.Zip(deserialized))
            Assert.Equal(original, roundTripped);
    }

    [Fact]
    public void GroupElementVector_RoundTrip()
    {
        var elements = new[] { Generators.G, Generators.Gg, Generators.Gh };
        var vector = GroupElementVector.FromElements(elements);

        var json = JsonSerializer.Serialize(vector, _options);
        var deserialized = JsonSerializer.Deserialize<GroupElementVector>(json, _options)!;

        Assert.Equal(elements.Length, deserialized.Count());
        foreach (var (original, roundTripped) in vector.Zip(deserialized))
            Assert.Equal(original, roundTripped);
    }

    [Fact]
    public void MAC_RoundTrip()
    {
        var mac = MAC.FromComponents(new Scalar(7), Generators.Gw);

        var json = JsonSerializer.Serialize(mac, _options);
        var deserialized = JsonSerializer.Deserialize<MAC>(json, _options)!;

        Assert.Equal(mac.T, deserialized.T);
        Assert.Equal(mac.V, deserialized.V);
    }

    [Fact]
    public void CredentialPresentation_RoundTrip()
    {
        var cp = CredentialPresentation.FromComponents(
            Generators.Ga, Generators.Gx0, Generators.Gx1, Generators.GV, Generators.Gs);

        var json = JsonSerializer.Serialize(cp, _options);
        var deserialized = JsonSerializer.Deserialize<CredentialPresentation>(json, _options)!;

        Assert.Equal(cp.Ca, deserialized.Ca);
        Assert.Equal(cp.Cx0, deserialized.Cx0);
        Assert.Equal(cp.Cx1, deserialized.Cx1);
        Assert.Equal(cp.CV, deserialized.CV);
        Assert.Equal(cp.S, deserialized.S);
    }

    [Fact]
    public void IssuanceRequest_RoundTrip()
    {
        var ir = IssuanceRequest.FromComponents(Generators.Gg, new[] { Generators.Ga, Generators.Gh });

        var json = JsonSerializer.Serialize(ir, _options);
        var deserialized = JsonSerializer.Deserialize<IssuanceRequest>(json, _options)!;

        Assert.Equal(ir.Ma, deserialized.Ma);
        Assert.Equal(ir.BitCommitments.Count(), deserialized.BitCommitments.Count());
        foreach (var (original, roundTripped) in ir.BitCommitments.Zip(deserialized.BitCommitments))
            Assert.Equal(original, roundTripped);
    }

    [Fact]
    public void KompaktorJsonHelper_ByteSerialization_RoundTrip()
    {
        var mac = MAC.FromComponents(new Scalar(99), Generators.G);

        var bytes = KompaktorJsonHelper.SerializeToUtf8Bytes(mac);
        var deserialized = KompaktorJsonHelper.DeserializeFromBytes<MAC>(bytes)!;

        Assert.Equal(mac.T, deserialized.T);
        Assert.Equal(mac.V, deserialized.V);
    }

    [Fact]
    public void CredentialHelper_MacFromBytes_RoundTrip()
    {
        var mac = MAC.FromComponents(new Scalar(42), Generators.G);

        var macBytes = mac.ToBytes();
        Assert.Equal(65, macBytes.Length);

        var reconstructed = CredentialHelper.MacFromBytes(macBytes);
        Assert.Equal(mac.T, reconstructed.T);
        Assert.Equal(mac.V, reconstructed.V);
    }
}