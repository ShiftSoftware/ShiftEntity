using System;

namespace ShiftSoftware.ShiftEntity.Core;

/// <summary>
/// Marks a partial class implementing <see cref="IShiftEntityMapper{TEntity, TListDTO, TViewDTO}"/> whose
/// mapping methods are filled in by the ShiftEntity source generator. Any of the four methods the
/// programmer implements in their own partial half is skipped by the generator (user-implemented wins).
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class ShiftEntityMapperAttribute : Attribute
{
}
