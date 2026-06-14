using System.Collections.Generic;

namespace ShiftSoftware.ShiftEntity.Core.Tagging;

/// <summary>
/// Opt-in marker for entities that participate in the framework's tagging system.
/// Implementing this enables an auto-wired many-to-many to <see cref="Tag"/> when
/// tagging is registered via <c>AddShiftTagging</c>.
/// </summary>
public interface IShiftEntityTaggable
{
    ICollection<Tag> Tags { get; set; }
}
