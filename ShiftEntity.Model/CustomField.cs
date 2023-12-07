using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Model;

public class CustomFieldBase
{
    public bool IsPassword { get; set; }
    public bool IsEncrypted { get; set; }
    public string DisplayName { get; set; } = default!;

    public CustomFieldBase()
    { }

    public CustomFieldBase(string displayName, bool isPassword, bool isEncrypted)
    {
        IsPassword = isPassword;
        IsEncrypted = isEncrypted;
        DisplayName = displayName;
    }
}

public class CustomField : CustomFieldBase
{
    public string Value { get; set; } = default!;

    public CustomField()
    { }

    public CustomField(string displayName, bool isPassword, bool isEncrypted) : base(displayName, isPassword, isEncrypted)
    { }

    public CustomField(CustomFieldBase customFieldBase)
    {
        Set(customFieldBase);
    }

    public CustomField(string value, CustomFieldBase customFieldBase) : this(customFieldBase)
    {
        Value = value;
    }

    public CustomField Set(CustomFieldBase customFieldBase)
    {
        IsPassword = customFieldBase.IsPassword;
        IsEncrypted = customFieldBase.IsEncrypted;
        DisplayName = customFieldBase.DisplayName;
        return this;
    }
}
