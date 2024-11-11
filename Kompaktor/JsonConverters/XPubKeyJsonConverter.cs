using Kompaktor.Mapper;

namespace Kompaktor.JsonConverters;

public class XPubKeyJsonConverter : GenericStringJsonConverter<XPubKey>
{
    public override XPubKey Create(string str) => new(str);
}