using System.Text;
using System.Text.Json;
using Kompaktor.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using WabiSabi.Crypto.Groups;
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
    
    public void GroupElementVectorTests()
    {
    }
    
}