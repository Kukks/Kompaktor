namespace Kompaktor.Behaviors;

public class ConsolidationBehaviorTrait : KompaktorClientBaseBehaviorTrait
{
    private readonly int _consolidationThreshold;

    public ConsolidationBehaviorTrait(int consolidationThreshold = 10)
    {
        _consolidationThreshold = consolidationThreshold;
    }
    
    public override void Start(KompaktorRoundClient client)
    {
        base.Start(client);
        Client.StartCoinSelection += ClientOnStartCoinSelection;
    }

    private Task ClientOnStartCoinSelection(object sender)
    {
        if(Client.AllocatedSelectedCoins.Count > _consolidationThreshold || Client.RemainingCoinCandidates!.Value.Length  == 0)
            return Task.CompletedTask;

        while (Client.AllocatedSelectedCoins.Count < _consolidationThreshold &&
               Client.RemainingCoinCandidates!.Value.Length > 0)
        {
            Client.AllocatedSelectedCoins.TryAdd(Client.RemainingCoinCandidates.Value.First(),
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