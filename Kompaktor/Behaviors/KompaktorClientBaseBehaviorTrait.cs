using Kompaktor.Models;

namespace Kompaktor.Behaviors;

public abstract class KompaktorClientBaseBehaviorTrait : IDisposable
{
    protected KompaktorRoundClient Client { get; set; }

    public virtual void Dispose()
    {
        Client.StatusChanged -= OnStatusChanged;
    }

    public virtual void Start(KompaktorRoundClient client)
    {
        Client = client;
        Client.StatusChanged += OnStatusChanged;
    }

    protected virtual Task OnStatusChanged(object sender, KompaktorStatus phase)
    {
        // Handle status change logic here
        return Task.CompletedTask;
    }
}