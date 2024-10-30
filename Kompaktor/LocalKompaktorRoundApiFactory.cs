using Kompaktor.Contracts;

namespace Kompaktor;

public class LocalKompaktorRoundApiFactory : IKompaktorRoundApiFactory
{
    private readonly KompaktorRoundOperator _roundOperator;

    public LocalKompaktorRoundApiFactory(KompaktorRoundOperator roundOperator)
    {
        _roundOperator = roundOperator;
    }

    public IKompaktorRoundApi Create()
    {
        return new LocalKompaktorApi(_roundOperator);
    }
}