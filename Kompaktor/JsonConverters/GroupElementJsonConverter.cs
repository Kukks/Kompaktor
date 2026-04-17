using WabiSabi.Crypto.Groups;

namespace Kompaktor.JsonConverters;

public class GroupElementJsonConverter : GenericStringJsonConverter<GroupElement>
{
    public override GroupElement Create(string str)
    {
        try
        {
            return GroupElement.FromBytes(Convert.FromHexString(str));
        }
        catch (FormatException ex)
        {
            throw new System.Text.Json.JsonException($"Invalid GroupElement hex value: '{str}'", ex);
        }
    }

    public override string ToString(GroupElement value) =>
        Convert.ToHexString(value.ToBytes()).ToLowerInvariant();
}