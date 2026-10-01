using System;
using System.Linq;
using System.Reflection;
using ShiftSoftware.ShiftEntity.Model.Dtos;

namespace ShiftSoftware.ShiftEntity.Core.Pii;

/// <summary>
/// Creates ordinary response fields from server declarations. A client-provided kind or display
/// never selects the mask, and raw values never enter an ordinary response wrapper.
/// </summary>
public sealed class PiiFieldProtection(IPiiMasker masker)
{
    public PiiFieldDTO Protect(Type dtoType, string memberName, string? rawValue)
    {
        var member = dtoType.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public);
        if (member is null || member.PropertyType != typeof(PiiFieldDTO))
            throw new ArgumentException("The requested member is not a protected field.", nameof(memberName));

        var declaration = FindDeclaration(member);
        if (declaration is null)
            throw new ArgumentException("The requested member is not classified.", nameof(memberName));

        return new PiiFieldDTO
        {
            Display = masker.Mask(declaration.Kind, rawValue),
            Value = null,
            Write = "keep"
        };
    }

    public static PiiAttribute? FindDeclaration(PropertyInfo member)
    {
        var declaration = member.GetCustomAttribute<PiiAttribute>(inherit: false);
        if (declaration is not null)
            return declaration;

        // Property attributes do not reliably follow overridden accessors through reflection.
        // Walk each override level so an intermediate declaration can tighten the policy.
        var accessor = member.GetMethod ?? member.SetMethod;
        var baseAccessor = accessor?.GetBaseDefinition();
        if (baseAccessor is null || baseAccessor == accessor)
            return null;

        for (var type = member.DeclaringType?.BaseType; type is not null; type = type.BaseType)
        {
            var inheritedMember = type.GetProperties(BindingFlags.DeclaredOnly | BindingFlags.Instance |
                                                     BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(property =>
                {
                    var inheritedAccessor = property.GetMethod ?? property.SetMethod;
                    return property.Name == member.Name && inheritedAccessor?.GetBaseDefinition() == baseAccessor;
                });
            var inheritedDeclaration = inheritedMember?.GetCustomAttribute<PiiAttribute>(inherit: false);
            if (inheritedDeclaration is not null)
                return inheritedDeclaration;
        }

        return null;
    }
}
