namespace Kompaktor.Behaviors;

public class ConsolidationBehaviorTrait : KompaktorClientBaseBehaviorTrait
{
    public override void Start(KompaktorRoundClient client)
    {
        base.Start(client);
        Client.StartCoinSelection += ClientOnStartCoinSelection;
    }

    private Task ClientOnStartCoinSelection(object sender)
    {
        if(Client.RemainingCoinCandidates!.Value.Length + Client.AllocatedSelectedCoins.Count< 2)
            return Task.CompletedTask;
        foreach (var coinCandidate in Client.RemainingCoinCandidates)
        {
            Client.AllocatedSelectedCoins.TryAdd(coinCandidate,
                null); //dont subscribe as this is only meant to consolidate
        }

        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        Client.StartCoinSelection -= ClientOnStartCoinSelection;
        base.Dispose();
    }
}