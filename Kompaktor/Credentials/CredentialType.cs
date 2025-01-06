using System.Text.Json.Serialization;

namespace Kompaktor.Credentials;

[JsonConverter(typeof(JsonStringEnumConverter))]

public enum CredentialType
{
	Amount,
}