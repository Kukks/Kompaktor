using NBitcoin;

namespace Kompaktor.Models;

public record SignRequest
{
    public OutPoint OutPoint { get; set; }
    public WitScript Witness { get; set; }
}