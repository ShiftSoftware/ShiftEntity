using System.Collections.Generic;

namespace ShiftSoftware.ShiftEntity.Model.Dtos.Tagging;

/// <summary>
/// Opt-in marker for DTOs (view/upsert and list) of entities that participate in
/// the framework's tagging system. The framework detects this on save to apply the
/// upsert-then-attach tag pipeline, and on UI binding to render tag chips/picker
/// automatically.
/// </summary>
public interface IShiftEntityTaggableDTO
{
    List<TagDTO> Tags { get; set; }
}
