using Kompaktor.Models;
using WabiSabi.Crypto;
using WabiSabi.Crypto.Randomness;

namespace Kompaktor.Credentials;

public static class CredentialTypeConstants
{
	/// <summary>
	/// Default number of credentials per issuance (k in the WabiSabi paper).
	/// Higher values reduce reissuance tree depth at the cost of larger proofs per round.
	/// </summary>
	public const int DefaultCredentialNumber = 2;

	public static CredentialIssuer CredentialIssuer(this CredentialType credentialType, CredentialIssuerSecretKey secretKey, WasabiRandom random, int numberOfCredentials = DefaultCredentialNumber)
	{
		return new CredentialIssuer(secretKey, random, credentialType.MaxAmountValue(), numberOfCredentials);
	}
	public static CredentialIssuer CredentialIssuer(this CredentialType credentialType, WasabiRandom random, int numberOfCredentials = DefaultCredentialNumber)
	{
		return credentialType.CredentialIssuer(new CredentialIssuerSecretKey(random), random, numberOfCredentials);
	}

	public static CredentialConfiguration CredentialConfiguration(
		this CredentialType credentialType,
		CredentialIssuer credentialIssuer)
	{
		var k = credentialIssuer.NumberOfCredentials;
		return new CredentialConfiguration(
			credentialIssuer.MaxAmount,
			credentialType.IssuanceIn(k),
			credentialType.IssuanceOut(k),
			credentialIssuer.CredentialIssuerSecretKey.ComputeCredentialIssuerParameters());
	}

    public static long MaxAmountValue(this CredentialType credentialType) => credentialType switch
    {
        CredentialType.Amount => 4_300_000_000_000L,
        _ => throw new NotSupportedException()
    };

    public static IntRange IssuanceIn(this CredentialType credentialType, int k = DefaultCredentialNumber) => credentialType switch
    {
        CredentialType.Amount => new IntRange(k, k),
        _ => throw new NotSupportedException()
    };

    public static IntRange IssuanceOut(this CredentialType credentialType, int k = DefaultCredentialNumber) => credentialType switch
    {
        CredentialType.Amount => new IntRange(k, k),
        _ => throw new NotSupportedException()
    };
}