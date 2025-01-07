using Kompaktor.Mapper;
using Kompaktor.Utils;
using WabiSabi.Crypto.ZeroKnowledge;

namespace Kompaktor.JsonConverters;

public class CredentialJsonConverter : GenericStringJsonConverter<BlindedCredential>
{
    public override BlindedCredential Create(string str)
    {
        return CredentialHelper.CredFromBytes(Convert.FromHexString(str));
    }
}


//
// public class DictionaryConverter<TKeyConverter, TValueConverter> : JsonConverterFactory 
//     where TKeyConverter: JsonConverter 
//     where TValueConverter:JsonConverter
// {
//     public override bool CanConvert(Type typeToConvert)
//     {
//         return typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Dictionary<,>);
//     }
//
//     public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
//     {
//         Type keyType = typeToConvert.GetGenericArguments()[0];
//         Type valueType = typeToConvert.GetGenericArguments()[1];
//
//         
//         
//         JsonConverter? keyConverter = typeof(TKeyConverter) == typeof(PlaceHolderJsonConverter) ? null:
//             (JsonConverter)Activator.CreateInstance(typeof(TKeyConverter))!;
//         
//         JsonConverter? valueConverter = typeof(TValueConverter) == typeof(PlaceHolderJsonConverter) ? null:(JsonConverter)Activator.CreateInstance(typeof(TValueConverter));
//       
//         return new DictionaryConverterInternal(keyType, valueType, keyConverter, valueConverter);
//     }
//     
//     
//     
// }
//
// public class DictionaryConverterInternal : JsonConverter<object>
// {
//     private readonly Type _keyType;
//     private readonly Type _valueType;
//     private readonly JsonConverter? _keyConverter;
//     private readonly JsonConverter? _valueConverter;
//
//     public DictionaryConverterInternal(Type keyType, Type valueType, JsonConverter? keyConverter, JsonConverter? valueConverter)
//     {
//         _keyType = keyType;
//         _valueType = valueType;
//         _keyConverter = keyConverter;
//         _valueConverter = valueConverter;
//     }
//
//     public override object Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
//     {
//         if (reader.TokenType  == JsonTokenType.Null)
//         {
//             return null;
//         }
//         if(reader.TokenType != JsonTokenType.StartObject)
//         {
//             throw new JsonException("Expected StartObject");
//         }
//
//         while (reader.Read())
//         {
//             if (reader.TokenType == JsonTokenType.PropertyName)
//             {
//                 if(_keyConverter != null)
//                 {
//                     var keyV = _keyConverter.Read(ref reader, _keyType, options);
//                 }
//             }
//             var 
//         }
//     }
//
//     public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
//     {
//         throw new NotImplementedException();
//     }
// }