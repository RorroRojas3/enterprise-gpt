using System.ComponentModel;
using System.Reflection;

namespace Enterprise.Gpt.Common.Extensions;

public static class EnumExtensions
{
    #region Public static methods

    public static string GetDescription(this Enum value)
    {
        var field = value.GetType().GetField(value.ToString());

        if (field is null)
        {
            return value.ToString();
        }

        var attribute = field.GetCustomAttribute<DescriptionAttribute>();

        return attribute?.Description ?? value.ToString();
    }

    #endregion
}
