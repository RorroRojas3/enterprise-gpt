using System.ComponentModel;

namespace RR.AI_Chat.Common.Enums
{
    public enum ChatRoles
    {
        [Description("system")]
        System = 1,

        [Description("assistant")]
        Assistant = 2,

        [Description("user")]
        User = 3,

        [Description("tool")]
        Tool = 4
    }
}
