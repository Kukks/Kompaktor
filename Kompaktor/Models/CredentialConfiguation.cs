using WabiSabi.Crypto;

namespace Kompaktor.Models;

public record CredentialConfiguation(long Max, IntRange IssuanceIn, IntRange IssuanceOut, CredentialIssuerParameters Parameters);