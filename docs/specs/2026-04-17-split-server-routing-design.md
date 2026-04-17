# Split-Server Routing — Sub-Project D Design Spec

**Date:** 2026-04-17
**Scope:** Multi-server Electrum routing for wallet address privacy
**Out of scope:** Credential tracking (E), Web UI (C)

## Goal

Prevent a single Electrum server from seeing all wallet addresses by distributing address subscriptions across multiple independent Electrum servers. Each "routing group" of addresses is assigned to a specific server, so no single server can correlate the full wallet. This is a fundamental privacy requirement — without it, the Electrum server operator gains the same wallet visibility as a full-node wallet, undermining the privacy gains from coinjoin.

## Problem

When a wallet queries a single Electrum server for UTXO information, the server learns:
1. Every address the wallet owns (via `blockchain.scripthash.subscribe` calls)
2. The total balance across all addresses
3. The transaction history for every address
4. Which addresses belong to the same wallet (they all connect from the same IP/circuit)

Even with Tor, if all queries go to one server, the server correlates by session.

## Solution: MultiServerBackend

A new `IBlockchainBackend` implementation that wraps multiple `ElectrumBackend` instances and routes operations based on script assignment. The wallet pre-assigns each address to a routing group at address generation time, and the routing backend ensures each group's queries only hit its designated server.

## Architecture

### New Types

| Type | Location | Purpose |
|------|----------|---------|
| `MultiServerBackend` | `Kompaktor.Blockchain` | `IBlockchainBackend` that routes to multiple `ElectrumBackend`s |
| `MultiServerOptions` | `Kompaktor.Blockchain` | Configuration: server list, routing strategy |
| `RoutingGroup` | `Kompaktor.Blockchain` | Identifies which server handles which addresses |

### Modified Types

| Type | Change |
|------|--------|
| `AddressEntity` | Add `RoutingGroup` field (nullable int) |

## 1. MultiServerOptions

```csharp
public class MultiServerOptions
{
    public List<ElectrumServerConfig> Servers { get; set; } = [];
    public RoutingStrategy Strategy { get; set; } = RoutingStrategy.RoundRobin;
}

public class ElectrumServerConfig
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 50001;
    public bool UseSsl { get; set; }
}

public enum RoutingStrategy
{
    RoundRobin,     // Assign addresses to servers in round-robin order
    Random,         // Random assignment
    Manual          // Caller specifies routing group per address
}
```

## 2. MultiServerBackend

Implements `IBlockchainBackend` by delegating to child `ElectrumBackend` instances.

### Routing Rules

| Operation | Routing |
|-----------|---------|
| `SubscribeAddressAsync(script)` | Route to the server assigned to this script's routing group |
| `GetUtxosForScriptAsync(script)` | Route to the server assigned to this script |
| `GetUtxoAsync(outpoint)` | Route to any server (outpoint isn't address-linked) |
| `GetTransactionAsync(txId)` | Route to any server (tx lookup isn't address-linked) |
| `BroadcastAsync(tx)` | Broadcast to ALL servers (redundancy + prevents broadcast timing analysis) |
| `EstimateFeeAsync` | Route to any server |
| `GetBlockHeightAsync` | Return max across all servers |

### Key Design Decisions

1. **Broadcast to all**: When broadcasting a transaction, send it to every connected server. This prevents a single server from correlating "this server was the only one that received this tx" with timing.

2. **Outpoint/tx lookups go to random server**: UTXO and transaction lookups by ID don't reveal address ownership, so they can go to any server. We round-robin for load balancing.

3. **Script-based ops are pinned**: Any operation involving a scriptPubKey is routed to the server assigned to that script's routing group. This is the core privacy property.

4. **Server failure fallback**: If a server is down, operations for its routing group fail rather than falling back to another server (which would leak the script to a different server). The wallet should retry or alert the user.

### Routing Group Assignment

When the wallet generates addresses, each address is assigned a routing group (integer). The routing group maps to a server index. Assignment strategies:

- **RoundRobin**: Address 0 → server 0, address 1 → server 1, ..., address N → server N % serverCount
- **Random**: Each address randomly assigned to a server
- **Manual**: Caller provides the routing group (for advanced users who want specific addresses on specific servers)

### Interface

```csharp
public class MultiServerBackend : IBlockchainBackend
{
    private readonly ElectrumBackend[] _backends;
    private readonly MultiServerOptions _options;
    private readonly Dictionary<string, int> _scriptToServer; // scripthash → server index
    private int _roundRobinIndex;

    public MultiServerBackend(MultiServerOptions options);

    // Register a script with a specific routing group
    public void AssignRouting(Script script, int routingGroup);

    // IBlockchainBackend implementation routes based on script assignment
}
```

## 3. AddressEntity Routing Group

Add to `AddressEntity`:
```csharp
public int? RoutingGroup { get; set; }
```

When `MultiServerBackend` is active, address generation assigns a routing group. When the wallet subscribes addresses to the backend, it calls `AssignRouting` for each address based on the persisted routing group.

## 4. Integration with KompaktorHdWallet

The wallet's `SetBlockchainBackend` method accepts any `IBlockchainBackend`. When passed a `MultiServerBackend`, it additionally assigns routing groups for all addresses during setup:

```csharp
public async Task InitializeRouting()
{
    if (_blockchain is not MultiServerBackend multi) return;

    var addresses = await _db.Addresses
        .Where(a => a.Account.Wallet.Id == WalletId)
        .ToListAsync();

    foreach (var addr in addresses)
    {
        if (addr.RoutingGroup is not null)
        {
            var script = new Script(addr.ScriptPubKey);
            multi.AssignRouting(script, addr.RoutingGroup.Value);
        }
    }
}
```

## 5. Testing Strategy

| Test Category | What to Test |
|--------------|-------------|
| `MultiServerBackendTests` | Script routing, broadcast to all, outpoint routing, server failure behavior |
| `RoutingAssignmentTests` | RoundRobin assignment, random assignment, persistence |

Tests use mock/stub backends (not real Electrum connections). The `ElectrumBackend` is already tested separately.

## 6. Configuration Example

```json
{
  "MultiServer": {
    "Strategy": "RoundRobin",
    "Servers": [
      { "Name": "Server1", "Host": "electrum1.example.com", "Port": 50002, "UseSsl": true },
      { "Name": "Server2", "Host": "electrum2.example.com", "Port": 50002, "UseSsl": true },
      { "Name": "Server3", "Host": "electrum3.example.com", "Port": 50002, "UseSsl": true }
    ]
  }
}
```
