using Kompaktor.Models;
using WabiSabi.Crypto;
using WabiSabi.Crypto.Randomness;
using WabiSabi.Crypto.ZeroKnowledge;

namespace Kompaktor.Credentials;

public static class CredentialTypeConstants
{
	/// <summary>
	/// Default number of credentials per issuance (k in the WabiSabi paper).
	/// Higher values reduce reissuance tree depth at the cost of larger proofs per round.
	/// </summary>
	public const int DefaultCredentialNumber = 2;

	public static ICredentialIssuer CreateIssuer(this CredentialType credentialType, CredentialIssuerSecretKey secretKey, WasabiRandom random, int numberOfCredentials = DefaultCredentialNumber, bool useBulletproofs = false)
	{
		if (useBulletproofs)
			return new BulletproofCredentialIssuer(secretKey, new BulletproofPlusPlusRangeProof(), random, credentialType.MaxAmountValue(), numberOfCredentials);
		return new CredentialIssuer(secretKey, random, credentialType.MaxAmountValue(), numberOfCredentials);
	}

	public static ICredentialIssuer CreateIssuer(this CredentialType credentialType, WasabiRandom random, int numberOfCredentials = DefaultCredentialNumber, bool useBulletproofs = false)
	{
		return credentialType.CreateIssuer(new CredentialIssuerSecretKey(random), random, numberOfCredentials, useBulletproofs);
	}

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
		ICredentialIssuer credentialIssuer,
		bool useBulletproofs = false)
	{
		var k = credentialIssuer.NumberOfCredentials;
		return new CredentialConfiguration(
			credentialIssuer.MaxAmount,
			credentialType.IssuanceIn(k),
			credentialType.IssuanceOut(k),
			credentialIssuer.CredentialIssuerSecretKey.ComputeCredentialIssuerParameters(),
			useBulletproofs);
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