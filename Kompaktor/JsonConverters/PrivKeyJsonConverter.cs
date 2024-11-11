using Kompaktor.Mapper;

namespace Kompaktor.JsonConverters;

public class PrivKeyJsonConverter : GenericStringJsonConverter<PrivKey>
{
    public override PrivKey Create(string str) => new(str);
}