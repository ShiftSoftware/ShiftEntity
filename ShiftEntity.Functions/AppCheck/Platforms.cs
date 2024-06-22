using System.ComponentModel;

namespace ShiftSoftware.ShiftEntity.Functions.AppCheck
{
    public enum Platforms
    {
        [Description("IOS OS")]
        IOS = 1,
        [Description("Normal Android")]
        Android = 2,
        [Description("HMS Android")]
        Huawei = 3,
    }
}   
