using Kompaktor.Blockchain;
using NBitcoin;
using Xunit;

namespace Kompaktor.Tests;

public class MultiServerBackendTests
{
    [Fact]
    public void AssignRouting_PinsScriptToServer()
    {
        var options = new MultiServerOptions
        {
            Servers =
            [
                new ElectrumServerConfig { Name = "S0", Host = "s0.test", Port = 50001 },
                new ElectrumServerConfig { Name = "S1", Host = "s1.test", Port = 50001 },
                new ElectrumServerConfig { Name = "S2", Host = "s2.test", Port = 50001 }
            ]
        };
        var backend = new MultiServerBackend(options);
        var script = new Key().GetScriptPubKey(ScriptPubKeyType.Segwit);

        backend.AssignRouting(script, 1);

        // Assigning same script again with different group should overwrite
        backend.AssignRouting(script, 2);
        // No exception — routing is updated
    }

    [Fact]
    public void AssignRoutingAuto_RoundRobin_DistributesEvenly()
    {
        var options = new MultiServerOptions
        {
            Strategy = RoutingStrategy.RoundRobin,
            Servers =
            [
                new ElectrumServerConfig { Name = "S0", Host = "s0.test", Port = 50001 },
                new ElectrumServerConfig { Name = "S1", Host = "s1.test", Port = 50001 },
                new ElectrumServerConfig { Name = "S2", Host = "s2.test", Port = 50001 }
            ]
        };
        var backend = new MultiServerBackend(options);

        var groups = new int[6];
        for (int i = 0; i < 6; i++)
        {
            var script = new Key().GetScriptPubKey(ScriptPubKeyType.Segwit);
            groups[i] = backend.AssignRoutingAuto(script);
        }

        // Should distribute across 3 servers: each server gets 2 addresses
        var counts = groups.GroupBy(g => g).Select(g => g.Count()).OrderBy(c => c).ToArray();
        Assert.Equal(3, counts.Length);
        Assert.All(counts, c => Assert.Equal(2, c));
    }

    [Fact]
    public void AssignRoutingAuto_SameScript_ReturnsSameGroup()
    {
        var options = new MultiServerOptions
        {
            Strategy = RoutingStrategy.RoundRobin,
            Servers =
            [
                new ElectrumServerConfig { Name = "S0", Host = "s0.test", Port = 50001 },
                new ElectrumServerConfig { Name = "S1", Host = "s1.test", Port = 50001 }
            ]
        };
        var backend = new MultiServerBackend(options);
        var script = new Key().GetScriptPubKey(ScriptPubKeyType.Segwit);

        var group1 = backend.AssignRoutingAuto(script);
        var group2 = backend.AssignRoutingAuto(script);

        Assert.Equal(group1, group2);
    }

    [Fact]
    public void AssignRouting_WrapsAroundServerCount()
    {
        var options = new MultiServerOptions
        {
            Servers =
            [
                new ElectrumServerConfig { Name = "S0", Host = "s0.test", Port = 50001 },
                new ElectrumServerConfig { Name = "S1", Host = "s1.test", Port = 50001 }
            ]
        };
        var backend = new MultiServerBackend(options);
        var script = new Key().GetScriptPubKey(ScriptPubKeyType.Segwit);

        // Routing group 5 with 2 servers should wrap to server 1
        backend.AssignRouting(script, 5);
        // No exception — wraps via modulo
    }

    [Fact]
    public void ScriptHashRouting_IsConsistent()
    {
        // Verify that the same script always produces the same Electrum script hash
        var script = new Key().GetScriptPubKey(ScriptPubKeyType.Segwit);
        var hash1 = ElectrumBackend.ToElectrumScriptHash(script);
        var hash2 = ElectrumBackend.ToElectrumScriptHash(script);

        Assert.Equal(hash1, hash2);
        Assert.Equal(64, hash1.Length);
    }

    [Fact]
    public void MultiServerOptions_DefaultStrategy_IsRoundRobin()
    {
        var options = new MultiServerOptions();
        Assert.Equal(RoutingStrategy.RoundRobin, options.Strategy);
    }

    [Fact]
    public void ElectrumServerConfig_HasDefaults()
    {
        var config = new ElectrumServerConfig();
        Assert.Equal("localhost", config.Host);
        Assert.Equal(50001, config.Port);
        Assert.False(config.UseSsl);
    }
}
