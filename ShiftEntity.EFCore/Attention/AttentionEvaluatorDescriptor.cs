using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Core.Attention;

namespace ShiftSoftware.ShiftEntity.EFCore.Attention;

internal sealed class AttentionEvaluatorDescriptor
{
    public required Type TargetType { get; init; }
    public required string EvaluatorTypeName { get; init; }
    public required bool RequiresHistory { get; init; }

    public required Func<IServiceProvider, object, object?, ActionTypes, AttentionSignal?> Invoke { get; init; }

    public Func<IServiceProvider, object, object?, ActionTypes, IReadOnlyList<StoredAttentionSignal>, AttentionSignal?>? InvokeWithHistory { get; init; }
}
