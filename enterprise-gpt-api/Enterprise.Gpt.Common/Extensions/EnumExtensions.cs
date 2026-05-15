using System.ComponentModel;
using System.Reflection;

namespace RR.AI_Chat.Common.Extensions
{
    public static class EnumExtensions
    {
        public static string GetDescription(this Enum value)
        {
            FieldInfo? field = value.GetType().GetField(value.ToString());
            
            if (field is null)
            {
                return value.ToString();
            }

            DescriptionAttribute? attribute = field.GetCustomAttribute<DescriptionAttribute>();
            
            return attribute?.Description ?? value.ToString();
        }
    }
}
