using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.TypeAuth.Core;
using System;
using System.Globalization;
using System.Net;

namespace ShiftSoftware.ShiftEntity.Core;

/// <summary>
/// Reads and enforces <see cref="GeneralActionTree.DataGridBulkWriteMaxTop"/>, on the server and in the browser.
/// </summary>
public static class DataGridBulkWriteExtensions
{
    /// <summary>
    /// The most rows the user can write in one data grid action, or 0 when the user can write none.
    /// </summary>
    public static int GetDataGridBulkWriteMaxTop(this ITypeAuthService typeAuth)
    {
        var action = GeneralActionTree.DataGridBulkWriteMaxTop;

        var value = typeAuth.AccessValue(action) ?? decimal.Parse(action.MinimumAccess!, CultureInfo.InvariantCulture);

        return (int)Math.Clamp(Math.Floor(value), 0, int.MaxValue);
    }

    /// <summary>
    /// Refuses a data grid action that writes more rows than the user can write in one action.
    /// </summary>
    /// <param name="typeAuth">The access of the user who runs the action.</param>
    /// <param name="rowCount">The rows the action writes, counted on the server.</param>
    /// <exception cref="ShiftEntityException">400, when <paramref name="rowCount"/> is over the limit.</exception>
    public static void EnsureDataGridBulkWriteMaxTop(this ITypeAuthService typeAuth, int rowCount)
    {
        var maxTop = typeAuth.GetDataGridBulkWriteMaxTop();

        if (rowCount > maxTop)
        {
            throw new ShiftEntityException(new Message(
                "Bulk Write Limit Exceeded",
                $"The number of records to change ({rowCount}) exceeds the maximum of {maxTop} allowed in one action. Please change fewer records at a time."
            ), (int)HttpStatusCode.BadRequest);
        }
    }
}
