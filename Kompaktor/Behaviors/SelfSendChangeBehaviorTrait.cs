using Kompaktor.Models;
using Kompaktor.Utils;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace Kompaktor.Behaviors;

public class SelfSendChangeBehaviorTrait : KompaktorClientBaseBehaviorTrait
{
    private readonly ILogger _logger;
    private readonly Func<Script> _changeAddressFetcher;
    private readonly TimeSpan _timeBeforeOutputTimeout;

    public SelfSendChangeBehaviorTrait(ILogger logger,Func<Script> changeAddressFetcher, TimeSpan timeBeforeOutputTimeout)
    {
        _logger = logger;
        _changeAddressFetcher = changeAddressFetcher;
        _timeBeforeOutputTimeout = timeBeforeOutputTimeout;
    }

    public override void Start(KompaktorRoundClient client)
    {
        base.Start(client);
        Client.FinishedOutputRegistration += OnOutputTimeout;
        _cts = new CancellationTokenSource();
        Client.DoNotSignKillSwitches.Add(this, true);
    }

    private CancellationTokenSource _cts;

    public override void Dispose()
    {
        _cts?.Cancel();
        base.Dispose();
    }

    private async Task OnOutputTimeout(object sender)
    {
        while (Client.Round.Status == KompaktorStatus.OutputRegistration &&
               (Client.Round.OutputPhaseEnd - DateTimeOffset.UtcNow) >
               _timeBeforeOutputTimeout)
        {
            await Task.Delay(100, _cts.Token);
        }

        var credAmount = Client.AvailableCredentials.Sum(credential => credential.Value);
        if (!Client.Round.RoundEventCreated.OutputAmount.Contains(credAmount))
        {
            Client.DoNotSignKillSwitches.AddOrReplace(this, false);
            return;
        }

        var txout = new TxOut(credAmount, _changeAddressFetcher());
        txout.Value =  txout.EffectiveValue(Client.Round.RoundEventCreated.FeeRate);
        //adjust the amount
        if (
            Client.Round.RoundEventCreated.OutputAmount.Contains(txout.Value))
        {
            _logger.LogInformation("Adding change output {txout} to the transaction", txout.Value);
            Client.AllocatedPlannedOutputs.TryAdd(txout, this);
            
        }
        
        Client.DoNotSignKillSwitches.AddOrReplace(this, false);
    }
}