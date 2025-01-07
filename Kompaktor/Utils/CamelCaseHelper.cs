namespace Kompaktor.Utils;

public static class CamelCaseHelper
{
    public static string ToCamelCase(string str)
    {
        if (string.IsNullOrEmpty(str))
        {
            return str;
        }

        return char.ToLowerInvariant(str[0]) + str[1..];
    }
}