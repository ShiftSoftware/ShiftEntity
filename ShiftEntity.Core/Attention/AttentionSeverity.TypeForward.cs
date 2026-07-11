using System.Runtime.CompilerServices;

// AttentionSeverity physically moved to ShiftEntity.Model (see ShiftEntity.Model/Attention/AttentionSeverity.cs)
// but keeps its ShiftSoftware.ShiftEntity.Core.Attention namespace. Consumers compiled against
// older ShiftEntity.Core builds (assembly "ShiftSoftware.ShiftEntity") still hold typerefs
// expecting the type there; this forward keeps those binaries loadable without a recompile.
[assembly: TypeForwardedTo(typeof(ShiftSoftware.ShiftEntity.Core.Attention.AttentionSeverity))]
