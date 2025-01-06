using Kompaktor.Models;
using Kompaktor.Utils;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace Kompaktor.Behaviors;

public class SelfSendChangeBehaviorTrait : KompaktorClientBaseBehaviorTrait
{
    private ILogger Logger => Client.Logger;
    private readonly Func<Script> _changeAddressFetcher;
    private readonly TimeSpan? _timeBeforeOutputTimeout;

    public SelfSendChangeBehaviorTrait(Func<Script> changeAddressFetcher, TimeSpan timeBeforeOutputTimeout)
    {
        _changeAddressFetcher = changeAddressFetcher;
        //  _timeBeforeOutputTimeout = timeBeforeOutputTimeout;
    }

    public override void Start(KompaktorRoundClient client)
    {
        base.Start(client);
        Client.FinishedOutputRegistration += sender =>
        {
            _ = TaskUtils.Loop(OnOutputTimeout, () => Client.Round.Status != KompaktorStatus.OutputRegistration, Logger,
                "SelfSendChangeBehaviorTrait", _cts.Token);
            return Task.CompletedTask;
        };
        _cts = new CancellationTokenSource();
        Client.DoNotSignKillSwitches.AddOrReplace(this, true);
    }

    private CancellationTokenSource _cts;

    public override void Dispose()
    {
        _cts?.Cancel();
        base.Dispose();
    }

    private async Task OnOutputTimeout()
    {
        if (_timeBeforeOutputTimeout is not null && !(Client.Round.OutputPhaseEnd - DateTimeOffset.UtcNow >
                                                      _timeBeforeOutputTimeout))
        {
            return;
        }

        try
        {
            await Client.OutputRegistrationLock.WaitAsync(_cts.Token);


            var creds = Client.AvailableCredentials.ExceptBy(reserved, credential => credential.Mac.Serial()).ToArray();
            var credAmount = creds.Sum(credential => credential.Value);
            var fee = Client.Round.RoundEventCreated.FeeRate;
            var outAmount = Client.RemainingPlannedOutputs().Sum(txout => txout.EffectiveCost(fee));
            var outAmount2 = credAmount - outAmount;

            if (!Client.Round.RoundEventCreated.OutputAmount.Contains(outAmount2))
            {
                Client.DoNotSignKillSwitches.AddOrReplace(this, false);
                return;
            }

            var txout = new TxOut(outAmount2, _changeAddressFetcher());
            txout.Value = txout.EffectiveValue(Client.Round.RoundEventCreated.FeeRate);
            //adjust the amount
            if (
                Client.Round.RoundEventCreated.OutputAmount.Contains(txout.Value))
            {
                Logger.LogInformation($"Adding change output {txout.Value} to the transaction");
                Client.AllocatedPlannedOutputs.TryAdd(txout, this);
                foreach (var credential in creds)
                {
                    reserved.Add(credential.Mac.Serial());
                }
            }

            Client.DoNotSignKillSwitches.AddOrReplace(this, false);
        }
        finally
        {
            Client.OutputRegistrationLock.Release();
        }
    }

    private ConcurrentHashSet<string> reserved = new();
}