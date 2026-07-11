using ShiftSoftware.TypeAuth.Core;
using ShiftSoftware.TypeAuth.Core.Actions;
using System;

namespace ShiftSoftware.ShiftEntity.Core;

/// <summary>
/// Resolves the <see cref="ReadWriteDeleteAction"/> instance that a secure endpoint attribute
/// names (<c>ActionTreeType</c> + <c>ActionName</c> on a <see cref="ShiftEntityEndpointSpec"/>).
/// Used at startup by the service registration (to feed <see cref="ShiftEntityActionMap"/>) and
/// at map time by the generated minimal-API endpoints. The discovery itself is TypeAuth's
/// (<see cref="ActionTreeHelper.FindDeclaredAction"/> — an action is a public static field on
/// the action tree class, matched by exact name); this class only adds the
/// <see cref="ReadWriteDeleteAction"/> requirement and endpoint-specific error messages.
/// </summary>
internal static class ShiftEntityEndpointActionResolver
{
    internal static ReadWriteDeleteAction ResolveAction(Type actionTreeType, string actionName)
    {
        var action = ActionTreeHelper.FindDeclaredAction(actionTreeType, actionName);

        if (action is null)
            throw new InvalidOperationException(
                $"Action '{actionName}' was not found as a public static action field on '{actionTreeType.FullName}'. " +
                "The TActionTree of a secure endpoint attribute must directly declare the named action field " +
                "(exact name, case-sensitive).");

        if (action is not ReadWriteDeleteAction readWriteDeleteAction)
            throw new InvalidOperationException(
                $"Action '{actionName}' on '{actionTreeType.FullName}' is not a {nameof(ReadWriteDeleteAction)}. " +
                "Secure endpoints can only bind to a ReadWriteDeleteAction node (not a dynamic / data-level action).");

        return readWriteDeleteAction;
    }
}
