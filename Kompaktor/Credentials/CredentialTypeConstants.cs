using Kompaktor.Models;
using WabiSabi.Crypto;
using WabiSabi.Crypto.Randomness;

namespace Kompaktor.Credentials;

public static class CredentialTypeConstants
{

	public static CredentialIssuer CredentialIssuer(this CredentialType credentialType, CredentialIssuerSecretKey secretKey, WasabiRandom random)
	{
		return new CredentialIssuer(secretKey, random, credentialType.MaxAmountValue());
	}
	public static CredentialIssuer CredentialIssuer(this CredentialType credentialType,  WasabiRandom random)
	{
		return credentialType.CredentialIssuer(new CredentialIssuerSecretKey(random), random);
	}
	
	public static CredentialConfiguation CredentialConfiguration(
		this CredentialType credentialType,
		CredentialIssuer credentialIssuer)
	{
		return new CredentialConfiguation(
			credentialIssuer.MaxAmount,
			credentialType.IssuanceIn(),
			credentialType.IssuanceOut(),
			credentialIssuer.CredentialIssuerSecretKey.ComputeCredentialIssuerParameters());
	}
	
	
	
    public static long MaxAmountValue(this CredentialType credentialType) => credentialType switch
    {
        CredentialType.Amount => 4_300_000_000_000L,
        _ => throw new NotSupportedException()
    };

    public static Models.IntRange IssuanceIn(this CredentialType credentialType) => credentialType switch
    {
        CredentialType.Amount => new IntRange(2, 2),
        _ => throw new NotSupportedException()
    };
     public static IntRange IssuanceOut(this CredentialType credentialType) => credentialType switch
    {
        CredentialType.Amount => new IntRange(2, 2),
        _ => throw new NotSupportedException()
    };
    

	
}