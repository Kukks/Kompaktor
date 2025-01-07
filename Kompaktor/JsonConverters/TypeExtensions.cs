namespace Kompaktor.JsonConverters;

public static class TypeExtensions
{
    public static IEnumerable<Type> GetInterfacesAndSelf(this Type type) => 
        (type ?? throw new ArgumentNullException()).IsInterface ? new[] { type }.Concat(type.GetInterfaces()) : type.GetInterfaces();
}