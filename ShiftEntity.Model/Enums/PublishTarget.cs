using System.ComponentModel;

namespace ShiftSoftware.ShiftEntity.Model.Enums
{
    public enum PublishTarget
    {
        [Description("Website")]
        Website = 1,

        [Description("Mobile App")]
        MobileApp = 2,

        [Description("Chat Bot")]
        ChatBot = 3
    }
}
