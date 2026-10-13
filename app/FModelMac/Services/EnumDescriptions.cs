using System.ComponentModel;
using System.Reflection;

namespace FModelMac.Services;

public static class EnumDescriptions
{
    /// <summary>The [Description] CUE4Parse gives enum values (ELanguage, ETexturePlatform), or the name.</summary>
    public static string GetDescription(this Enum value) =>
        value.GetType().GetField(value.ToString())?.GetCustomAttribute<DescriptionAttribute>()?.Description ?? value.ToString();
}
