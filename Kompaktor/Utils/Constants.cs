namespace Kompaktor.Utils;

public static class Constants
{
    public const int P2wpkhInputSizeInBytes = 41;
    public const int P2wpkhInputVirtualSize = 69;
    public const int P2pkhInputSizeInBytes = 145;
    public const int P2wpkhOutputVirtualSize = 31;

    public const int P2trInputVirtualSize = 58;
    public const int P2trOutputVirtualSize = 43;

    public const int P2pkhInputVirtualSize = 148;
    public const int P2pkhOutputVirtualSize = 34;
    public const int P2wshInputVirtualSize = 105; // we assume a 2-of-n multisig
    public const int P2wshOutputVirtualSize = 32;
    public const int P2shInputVirtualSize = 297; // we assume a 2-of-n multisig
    public const int P2shOutputVirtualSize = 32;
}

