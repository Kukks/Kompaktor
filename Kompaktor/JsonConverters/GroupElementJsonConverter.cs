using WabiSabi.Crypto.Groups;

namespace Kompaktor.JsonConverters;

public class GroupElementJsonConverter : GenericStringJsonConverter<GroupElement>
{
    public override GroupElement Create(string str)
    {
        return GroupElement.FromBytes(Convert.FromHexString(str));
    }
}