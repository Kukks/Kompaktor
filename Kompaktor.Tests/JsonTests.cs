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
    public void RegisterInputQuoteRequestTests()
    {

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